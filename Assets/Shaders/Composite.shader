
Shader "Fluid/Composite"
{
    Properties {
        _Eta("IOR (water~1.33)", Float) = 1.33
        _SigmaA("Absoption coeff", Vector) = (0.15, 0.06, 0.03, 0) // RGB
        _RefractScale("Refraction scale", Float) = 0.02
        _F0("Base reflectivity", Float) = 0.02
        _SpecPower("Sun specular power", Float) = 150
        _SpecIntensity("Sun specular intensity", Float) = 1.0
        _SmoothRadius("Depth smooth radius (texels)", Int) = 5
        _SmoothStride("Depth smooth sample stride (texels)", Int) = 2
        _SmoothSigmaS("Depth smooth spatial sigma", Float) = 4.0
        _SmoothSigmaR("Depth smooth range sigma (world)", Float) = 0.4
        _SkyZenith("Env: zenith color", Color) = (0.30, 0.45, 0.72, 1)
        _SkyHorizon("Env: horizon color", Color) = (0.62, 0.66, 0.70, 1)
        _SkyGround("Env: below-horizon color", Color) = (0.22, 0.20, 0.18, 1)
        _ReflIntensity("Env reflection intensity", Range(0, 3)) = 1.0
        _WaterColor("Water body color (rgb) + strength (a)", Color) = (0.06, 0.4, 0.55, 1)
    }

    SubShader
    {
        Tags{ "RenderType"="Opaque" "Queue"="Transparent+100" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #include "UnityCG.cginc"
            #pragma target 4.5
            #pragma vertex   vert_img
            #pragma fragment frag

            sampler2D _SceneTex, _DepthTex, _ThicknessTex;
            float4 _DepthTex_TexelSize;
            float4 _ThicknessTex_TexelSize;

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float _Eta, _RefractScale, _F0;
            float3 _SigmaA;
            float4x4 _Proj;
            float _SpecPower, _SpecIntensity;
            float3 _SunDirVS;   // view-space dir TOWARD the sun; set by FluidScreenSpaceRenderer
            float3 _SunColor;   // sun color * intensity

            int   _SmoothRadius;
            int   _SmoothStride;
            float _SmoothSigmaS, _SmoothSigmaR;

            float4 _SkyZenith, _SkyHorizon, _SkyGround;
            float  _ReflIntensity;
            float4 _WaterColor;   // rgb = water body colour, a = in-scattering strength
            float4x4 _CamToWorld;   // set by the renderer; view-space reflection dir -> world

            // Analytic environment for the Fresnel reflection term.
            //
            // The old code sampled _SceneTex at the SAME uv as the refraction, so the Fresnel
            // lerp blended two near-identical images and the term did nothing — the surface
            // never got the bright grazing-angle "skin" that makes water read as liquid rather
            // than as glass beads. A directional gradient supplies that contrast for free and,
            // unlike a reflection-probe sample, doesn't depend on unity_SpecCube0 actually
            // being bound for a fullscreen blit (which is not guaranteed on this Metal path).
            // If a real skybox is ever set up, swap the body for a UNITY_SAMPLE_TEXCUBE of
            // unity_SpecCube0 + DecodeHDR and keep the same signature.
            float3 SampleEnv(float3 dirWS)
            {
                float up = dirWS.y;
                float3 above = lerp(_SkyHorizon.rgb, _SkyZenith.rgb, sqrt(saturate( up)));
                float3 below = lerp(_SkyHorizon.rgb, _SkyGround.rgb, sqrt(saturate(-up)));
                return (up >= 0.0) ? above : below;
            }

            float3 ReconstructViewPos(float2 uv, float zView)
            {
                float2 ndc = uv * 2 - 1;
                float vx = ndc.x * zView / _Proj._m00;
                float vy = ndc.y * zView / _Proj._m11;
                return float3(vx, vy, zView);
            }

            // Bilateral smoothing of the front-depth buffer, done here in the composite instead
            // of as a separate ping-pong blur pass — the separable RT approach kept collapsing
            // to background on Metal inside OnRenderImage. Derives normals from the smoothed
            // depth (Green GDC10), which is what turns per-particle bumps into a surface.
            // Background sentinel (-1e20) neighbours are excluded so edges stay sharp.
            //
            // Cost: a full 2D kernel is (2R+1)^2 taps/px (441 at R=10) and was measured at
            // ~23 ms. _SmoothStride subsamples the kernel every N texels — R=10, stride=2 is
            // 11x11=121 taps (~3.6x fewer) — while keeping the true gaussian weight at each
            // tap's real offset, so the effective kernel width is preserved. This is the safe,
            // in-pass cost cut; a proper separable (2x1D) blur is the bigger win but needs the
            // RT-pass rework that hit the Metal bug above.
            // Per-texel depth gradient from the valid 4-neighbourhood. At a grazing view angle a
            // perfectly smooth surface still ramps steeply in screen space, so a fixed range
            // tolerance on the RAW depth difference (zs - zc) rejects legitimate neighbours and
            // the bilateral collapses back to the noisy per-particle depth. The gradient lets the
            // range term compare against a locally-planar PREDICTION instead, which is what makes
            // the filter independent of camera angle. Background sentinels (-1e20) are excluded so
            // the gradient isn't poisoned at the silhouette; one-sided differences are used there.
            float2 DepthGradient(float2 uv, float zc, float2 texel)
            {
                float zL = tex2D(_DepthTex, uv - float2(texel.x, 0)).r;
                float zR = tex2D(_DepthTex, uv + float2(texel.x, 0)).r;
                float zD = tex2D(_DepthTex, uv - float2(0, texel.y)).r;
                float zU = tex2D(_DepthTex, uv + float2(0, texel.y)).r;

                bool okL = zL > -1e19, okR = zR > -1e19, okD = zD > -1e19, okU = zU > -1e19;
                float gx = 0, gy = 0;
                if      (okL && okR) gx = 0.5 * (zR - zL);
                else if (okR)        gx = zR - zc;
                else if (okL)        gx = zc - zL;
                if      (okD && okU) gy = 0.5 * (zU - zD);
                else if (okU)        gy = zU - zc;
                else if (okD)        gy = zc - zD;
                return float2(gx, gy);   // depth change per texel in x / y
            }

            // Returns the smoothed depth, and via gradOut a per-texel surface gradient fitted
            // across the whole kernel (see the plane-fit note below).
            float SmoothFrontDepth(float2 uv, float zc, out float2 gradOut)
            {
                float2 texel = _DepthTex_TexelSize.xy;
                float2 grad  = DepthGradient(uv, zc, texel); // local surface slope
                gradOut = grad;

                if (_SmoothRadius <= 0) return zc;
                int stride = max(1, _SmoothStride);

                float inv2s2 = 0.5 / max(_SmoothSigmaS * _SmoothSigmaS, 1e-8);
                float inv2r2 = 0.5 / max(_SmoothSigmaR * _SmoothSigmaR, 1e-8);

                float acc = zc, wsum = 1.0;

                // Weighted least-squares plane fit, accumulated in the SAME loop as the blur.
                // Reconstructing normals from ddx/ddy uses a single 2x2-quad texel difference,
                // so any residual depth wobble becomes normal noise — that is the glittery
                // "heap of glass beads" look, and a real reflection term only amplifies it.
                // Fitting a plane over the full kernel averages that noise across a wide
                // baseline. It costs a handful of MADs per tap because the taps and their
                // bilateral weights are already being computed here.
                float Sxx = 0, Syy = 0, Sxy = 0, Sxz = 0, Syz = 0;

                [loop]
                for (int y = -_SmoothRadius; y <= _SmoothRadius; y += stride)
                [loop]
                for (int x = -_SmoothRadius; x <= _SmoothRadius; x += stride)
                {
                    if (x == 0 && y == 0) continue;
                    float r2 = x * x + y * y;
                    if (r2 > _SmoothRadius * _SmoothRadius) continue; // circular kernel: no square footprint
                    float2 o = float2(x, y) * texel;
                    float zs = tex2D(_DepthTex, uv + o).r;
                    if (zs <= -1e19) continue;            // skip background
                    // Deviation from the locally-planar prediction, not the raw depth gap: a
                    // smooth ramp (grazing angle) predicts perfectly and passes; only true bumps
                    // and separate surfaces deviate, so edges are still preserved.
                    float predicted = zc + grad.x * x + grad.y * y;
                    float d  = zs - predicted;
                    float w  = exp(-r2 * inv2s2) * exp(-d * d * inv2r2);
                    acc  += zs * w;
                    wsum += w;

                    float dz = zs - zc;
                    Sxx += w * x * x;  Syy += w * y * y;  Sxy += w * x * y;
                    Sxz += w * x * dz; Syz += w * y * dz;
                }

                // Solve the 2x2 normal equations. Near a silhouette too few taps survive and the
                // system goes singular — keep the 4-neighbour gradient in that case.
                float det = Sxx * Syy - Sxy * Sxy;
                if (abs(det) > 1e-12)
                    gradOut = float2(Syy * Sxz - Sxy * Syz, Sxx * Syz - Sxy * Sxz) / det;

                return acc / max(wsum, 1e-6);
            }

            // Green GDC10 renders thickness as splats "and then blur" (slide 45). We were passing
            // it RAW, so per-particle lumps in thickness became per-particle lumps in the Beer's-law
            // absorption below — the "beads" that survive even a perfectly smooth depth. A plain
            // separable-ish box blur is all thickness needs (no edge to preserve — it's already a
            // soft accumulation), reusing the depth smooth radius/stride so one knob drives both.
            float SmoothThickness(float2 uv)
            {
                float2 texel = _ThicknessTex_TexelSize.xy;
                float T = tex2D(_ThicknessTex, uv).r;
                if (_SmoothRadius <= 0) return T;
                int stride = max(1, _SmoothStride);

                float inv2s2 = 0.5 / max(_SmoothSigmaS * _SmoothSigmaS, 1e-8);
                float acc = 0, wsum = 0;
                [loop]
                for (int y = -_SmoothRadius; y <= _SmoothRadius; y += stride)
                [loop]
                for (int x = -_SmoothRadius; x <= _SmoothRadius; x += stride)
                {
                    float r2 = x * x + y * y;
                    if (r2 > _SmoothRadius * _SmoothRadius) continue; // circular kernel: no square footprint
                    float w = exp(-r2 * inv2s2);
                    acc  += tex2D(_ThicknessTex, uv + float2(x, y) * texel).r * w;
                    wsum += w;
                }
                return acc / max(wsum, 1e-6);
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;

                float zRaw = tex2D(_DepthTex, uv).r; // raw view-space z

                if (zRaw <= -1e19){
                    return tex2D(_SceneTex, uv); // no fluid here
                }

                float2 gradS;                                 // kernel-fitted surface gradient
                float zV = SmoothFrontDepth(uv, zRaw, gradS); // bilateral-smoothed depth

                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float sceneZ   = -sceneEye;  

                if (zV <= sceneZ) {
                    return tex2D(_SceneTex, uv); 
                } // If fluid front is behind the opaque scene, show scene and exit

                 // reconstruct normal from front depth, stepping one texel along the fitted
                 // gradient rather than along ddx/ddy (see SmoothFrontDepth for why)
                float3 P = ReconstructViewPos(uv, zV); // view-space position
                float3 Px = ReconstructViewPos(uv + float2(_DepthTex_TexelSize.x, 0), zV + gradS.x);
                float3 Py = ReconstructViewPos(uv + float2(0, _DepthTex_TexelSize.y), zV + gradS.y);
                // Order matters: for a camera-facing surface (Px-P)x(Py-P) gives +z (toward
                // the camera in view space). Reversed, N faces away, Fresnel saturates to 1
                // and the composite degenerates to the unmodified scene — invisible water.
                float3 N = normalize(cross(Px - P, Py - P)); // normal in view space

                float T = SmoothThickness(uv); // blurred thickness (was raw -> beads in absorption)
                float3 transmittance = exp(-_SigmaA * T); // Beer's law

                float3 V = normalize(-P); // view vector in view space
                float F = _F0 + (1 - _F0) * pow(1 - saturate(dot(N, V)), 5); // Schlick's approx

                // cheap refraction: offset by normal (view-space -> screen-space via /-z).
                // The -N.z floor was 0.05, which let the offset hit ~20x at grazing normals
                // (every surface bump's silhouette), sampling far into the dark background ->
                // the dark "labyrinth" ridges / voids around bumps. Raise the floor and hard-cap
                // the excursion so bumps refract GENTLY; flat surfaces (small N.xy) are unaffected.
                float2 rDir = N.xy / max(-N.z, 0.25);          // was 0.05: caps 20x -> ~4x
                float2 rOff = _RefractScale * clamp(rDir, -4.0, 4.0);
                float2 rUV  = saturate(uv + rOff);

                // Refracted background, dimmed by Beer's-law absorption, PLUS the water's own
                // in-scattered body colour that grows with depth (1 - transmittance). Without this
                // term thin water just shows the grey background and reads as wet concrete; with it
                // the water takes on _WaterColor and stands out from whatever is behind it.
                float3 refr = tex2D(_SceneTex, rUV).rgb * transmittance;
                refr += _WaterColor.rgb * (1.0 - transmittance) * _WaterColor.a;

                // Reflected color: mirror the view vector about the surface normal and look up
                // the environment. This is the term that gives the surface a bright sky-toned
                // sheen at grazing angles (where F -> 1), which is the strongest single cue
                // that reads as "liquid" instead of "glassy sphere".
                float3 Rvs  = reflect(-V, N);                                // view-space
                float3 Rws  = normalize(mul((float3x3)_CamToWorld, Rvs));     // -> world space
                float3 refl = SampleEnv(Rws) * _ReflIntensity;

                float3 color = lerp(refr, refl, F);

                // Sun specular (Blinn-Phong). This is what makes ripples readable at grazing
                // angles: without it the surface only shows by distorting the background,
                // which vanishes over uniform backdrops.
                float3 H = normalize(_SunDirVS + V);
                float spec = pow(saturate(dot(N, H)), _SpecPower) * _SpecIntensity;
                color += spec * _SunColor;

                return float4(color, 1);
            }
            
            ENDHLSL
        }
    }
}