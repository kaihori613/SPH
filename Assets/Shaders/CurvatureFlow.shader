// Screen-space curvature flow (van der Laan, Green, Sainz 2009 — "Screen Space Fluid
// Rendering with Curvature Flow"). ONE explicit mean-curvature-flow step on the view-space
// front-depth field. The renderer runs it N times, ping-ponging between two RFloat depth RTs.
//
// Why this instead of the bilateral blur already in Composite.shader: a bilateral filter is
// edge-preserving-but-flattening — to erase per-particle bumps it also flattens real ripples,
// and (as logged) widening it never removed the "glass beads". Curvature flow instead evolves
// the surface along its MEAN CURVATURE, so it specifically dissolves the high-curvature
// per-particle bulges while leaving low-curvature regions (flat pool, gentle waves) alone.
// That is the surface-reconstruction lever the bilateral doesn't have.
//
// Ping-pong RTs are the documented Metal hazard on this project: Graphics.Blit AND
// CommandBuffer.Blit both collapse an RFloat depth RT to background inside OnRenderImage. The
// certified-good primitive is CommandBuffer.SetRenderTarget + a fullscreen DrawMesh (what the
// renderer uses), so this shader is written as a plain fullscreen pass driven that way.
Shader "Fluid/CurvatureFlow"
{
    Properties
    {
        _FlowDt("Flow step size (dt)", Float) = 0.001
        _MaxStep("Max |dz| per iteration (world units)", Float) = 0.02
        _FlowStride("Finite-difference stencil stride (texels)", Int) = 1
        _UvFlipY("Flip Y of output (Metal RT insurance)", Float) = 0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #include "UnityCG.cginc"
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            sampler2D _DepthTex;         // view-space z (negative in front); background = -1e20
            float4 _DepthTex_TexelSize;  // (1/w, 1/h, w, h)
            float4x4 _Proj;              // camera projection; _m00/_m11 are the focal terms
            float _FlowDt;
            float _MaxStep;              // per-iteration |dz| clamp — stability + kills sparkle
            int   _FlowStride;           // sample neighbours this many texels out (wider = smoother/iter)
            float _UvFlipY;

            struct appdata { float3 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            // Fullscreen passthrough. The renderer draws the same [-1,1] quad the thickness
            // pass uses; here the vertex position is already clip space. _UvFlipY flips the
            // OUTPUT geometry (not the sampled uv) as insurance against a Metal viewport
            // Y-flip between our DrawMesh and what tex2D expects — default off, toggle from C#
            // without recompiling if the ping-pong comes out inverted.
            v2f vert(appdata v)
            {
                v2f o;
                float ys = (_UvFlipY > 0.5) ? -1.0 : 1.0;
                o.pos = float4(v.vertex.x, v.vertex.y * ys, 0.0, 1.0);
                o.uv  = v.uv;
                return o;
            }

            static const float BG = -1e19;

            float frag(v2f i) : SV_Target
            {
                float2 texel = _DepthTex_TexelSize.xy;
                float  z = tex2D(_DepthTex, i.uv).r;
                if (z <= BG) return z;                 // background stays background

                // Stencil spacing. Sampling S texels out (instead of 1) low-pass-filters the
                // finite differences — it ignores sub-stride wobble, so the flow stops seeding
                // the salt-and-pepper depth spikes that showed up as a bright per-pixel sparkle,
                // and it smooths a wider band per iteration (so fewer iterations are needed).
                int S = max(1, _FlowStride);
                float2 t = texel * (float)S;

                // 8-neighbourhood. Background samples are CLAMPED to the centre depth (a flat
                // Neumann boundary) rather than frozen. An earlier version returned z unchanged
                // whenever a 4-neighbour was background — but that left edge texels at raw splat
                // depth while their interior neighbours evolved away, forming a 1-texel depth
                // CLIFF at the silhouette. The reconstructed normal on that cliff points sideways,
                // Fresnel saturates, and the surface grew a thick bright sky-reflection rim.
                // Clamping missing neighbours to z makes the edge see a flat continuation: the
                // texel still evolves (gently) with its valid interior side, no cliff, no rim.
                float zL  = tex2D(_DepthTex, i.uv + float2(-t.x,   0)).r;
                float zR  = tex2D(_DepthTex, i.uv + float2( t.x,   0)).r;
                float zD  = tex2D(_DepthTex, i.uv + float2(   0,-t.y)).r;
                float zU  = tex2D(_DepthTex, i.uv + float2(   0, t.y)).r;
                float zLU = tex2D(_DepthTex, i.uv + float2(-t.x, t.y)).r;
                float zRU = tex2D(_DepthTex, i.uv + float2( t.x, t.y)).r;
                float zLD = tex2D(_DepthTex, i.uv + float2(-t.x,-t.y)).r;
                float zRD = tex2D(_DepthTex, i.uv + float2( t.x,-t.y)).r;

                // Isolated speck (all 4 face-neighbours background): nothing to smooth against.
                if (zL <= BG && zR <= BG && zD <= BG && zU <= BG) return z;

                zL  = (zL  <= BG) ? z : zL;   zR  = (zR  <= BG) ? z : zR;
                zD  = (zD  <= BG) ? z : zD;   zU  = (zU  <= BG) ? z : zU;
                zLU = (zLU <= BG) ? z : zLU;  zRU = (zRU <= BG) ? z : zRU;
                zLD = (zLD <= BG) ? z : zLD;  zRD = (zRD <= BG) ? z : zRD;

                // Derivatives normalised back to PER-TEXEL units (divide by the stride) so the
                // perspective constants below stay valid regardless of S.
                float invS  = 1.0 / (float)S;
                float invS2 = invS * invS;
                float zx  = 0.5 * (zR - zL) * invS;
                float zy  = 0.5 * (zU - zD) * invS;
                float zxx = (zR - 2.0 * z + zL) * invS2;
                float zyy = (zU - 2.0 * z + zD) * invS2;
                float zxy = 0.25 * (zRU - zLU - zRD + zLD) * invS2;

                // Perspective constants: convert texel-space derivatives to the view-space
                // metric. Res comes from _TexelSize.zw, focal terms from the projection.
                float Cx  = 2.0 / (_DepthTex_TexelSize.z * _Proj._m00);
                float Cy  = 2.0 / (_DepthTex_TexelSize.w * _Proj._m11);
                float Cx2 = Cx * Cx, Cy2 = Cy * Cy;

                float D  = Cy2 * zx * zx + Cx2 * zy * zy + Cx2 * Cy2 * z * z;
                float Dx = 2.0 * Cy2 * zx * zxx + 2.0 * Cx2 * zy * zxy + 2.0 * Cx2 * Cy2 * z * zx;
                float Dy = 2.0 * Cy2 * zx * zxy + 2.0 * Cx2 * zy * zyy + 2.0 * Cx2 * Cy2 * z * zy;
                float Ex = 0.5 * zx * Dx - zxx * D;
                float Ey = 0.5 * zy * Dy - zyy * D;

                float Dpow = pow(max(D, 1e-12), 1.5);
                float H = (Cy * Ex + Cx * Ey) / (2.0 * Dpow);   // ~ mean curvature

                // Explicit Euler step, CLAMPED. The raw step blows up wherever D is tiny or the
                // depth has a sharp per-particle discontinuity (the first iteration reads raw
                // splats) — those overshoots oscillate into the salt-and-pepper sparkle. Capping
                // |dz| per iteration keeps the flow stable across a wide dt range: it just takes
                // more iterations to cross a big gap instead of ringing.
                float step = clamp(_FlowDt * H, -_MaxStep, _MaxStep);
                return z + step;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
