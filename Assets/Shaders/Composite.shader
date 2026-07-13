
Shader "Fluid/Composite"
{
    Properties {
        _Eta("IOR (water~1.33)", Float) = 1.33 
        _SigmaA("Absoption coeff", Vector) = (0.15, 0.06, 0.03, 0) // RGB 
        _RefractScale("Refraction scale", Float) = 0.02
        _F0("Base reflectivity", Float) = 0.02
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

            float3 ReconstructViewPos(float2 uv, float zView)
            {
                float2 ndc = uv * 2 - 1;
                float vx = ndc.x * zView / _Proj._m00;
                float vy = ndc.y * zView / _Proj._m11;
                return float3(vx, vy, zView);
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;

                float zV = tex2D(_DepthTex, uv).r; // view-space z

                if (zV <= -1e19){
                    return tex2D(_SceneTex, uv); // no fluid here
                }

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
                float3 N = normalize(cross(Py - P, Px - P)); // normal in view space

                float T = tex2D(_ThicknessTex, uv).r; // thickness in world space
                float3 transmittance = exp(-_SigmaA * T); // Beer's law

                float3 V = normalize(-P); // view vector in view space
                float F = _F0 + (1 - _F0) * pow(1 - saturate(dot(N, V)), 5); // Schlick's approx

                // cheap refraction: offset by normal
                float2 rUV = saturate(uv + _RefractScale * (N.xy / max(-N.z, 0.05))); // view-space to screen-space: divide by -z

                float3 refr = tex2D(_SceneTex, rUV).rgb * transmittance; // refracted color
                float3 refl = tex2D(_SceneTex, uv).rgb; // reflected color

                float3 color = lerp(refr, refl, F);
                return float4(color, 1);
            }
            
            ENDHLSL
        }
    }
}