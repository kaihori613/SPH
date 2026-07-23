
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
        _SmoothSigmaS("Depth smooth spatial sigma", Float) = 4.0
        _SmoothSigmaR("Depth smooth range sigma (world)", Float) = 0.4
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

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float _Eta, _RefractScale, _F0;
            float3 _SigmaA;
            float4x4 _Proj;
            float _SpecPower, _SpecIntensity;
            float3 _SunDirVS;   // view-space dir TOWARD the sun; set by FluidScreenSpaceRenderer
            float3 _SunColor;   // sun color * intensity

            int   _SmoothRadius;
            float _SmoothSigmaS, _SmoothSigmaR;

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
            float SmoothFrontDepth(float2 uv, float zc)
            {
                if (_SmoothRadius <= 0) return zc;

                float inv2s2 = 0.5 / max(_SmoothSigmaS * _SmoothSigmaS, 1e-8);
                float inv2r2 = 0.5 / max(_SmoothSigmaR * _SmoothSigmaR, 1e-8);

                float acc = zc, wsum = 1.0;
                float2 texel = _DepthTex_TexelSize.xy;

                [loop]
                for (int y = -_SmoothRadius; y <= _SmoothRadius; y++)
                [loop]
                for (int x = -_SmoothRadius; x <= _SmoothRadius; x++)
                {
                    if (x == 0 && y == 0) continue;
                    float2 o = float2(x, y) * texel;
                    float zs = tex2D(_DepthTex, uv + o).r;
                    if (zs <= -1e19) continue;            // skip background

                    float r2 = x * x + y * y;
                    float d  = zs - zc;
                    float w  = exp(-r2 * inv2s2) * exp(-d * d * inv2r2);
                    acc  += zs * w;
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

                float zV = SmoothFrontDepth(uv, zRaw); // bilateral-smoothed depth

                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float sceneZ   = -sceneEye;  

                if (zV <= sceneZ) {
                    return tex2D(_SceneTex, uv); 
                } // If fluid front is behind the opaque scene, show scene and exit

                 // reconstruct normal from front depth
                float3 P = ReconstructViewPos(uv, zV); // view-space position
                float zdx = ddx(zV), zdy = ddy(zV);
                float3 Px = ReconstructViewPos(uv + float2(_DepthTex_TexelSize.x, 0), zV + zdx);
                float3 Py = ReconstructViewPos(uv + float2(0, _DepthTex_TexelSize.y), zV + zdy);
                // Order matters: for a camera-facing surface (Px-P)x(Py-P) gives +z (toward
                // the camera in view space). Reversed, N faces away, Fresnel saturates to 1
                // and the composite degenerates to the unmodified scene — invisible water.
                float3 N = normalize(cross(Px - P, Py - P)); // normal in view space

                float T = tex2D(_ThicknessTex, uv).r; // thickness in world space
                float3 transmittance = exp(-_SigmaA * T); // Beer's law

                float3 V = normalize(-P); // view vector in view space
                float F = _F0 + (1 - _F0) * pow(1 - saturate(dot(N, V)), 5); // Schlick's approx

                // cheap refraction: offset by normal
                float2 rUV = saturate(uv + _RefractScale * (N.xy / max(-N.z, 0.05))); // view-space to screen-space: divide by -z

                float3 refr = tex2D(_SceneTex, rUV).rgb * transmittance; // refracted color
                float3 refl = tex2D(_SceneTex, uv).rgb; // reflected color

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