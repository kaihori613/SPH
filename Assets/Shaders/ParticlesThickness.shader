Shader "Fluid/ParticlesThickness"
{
    Properties{}
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Cull Off ZWrite Off ZTest Always

        // RT0: thickness = additive sum
        Blend   0 One One
        BlendOp 0 Add

        // RT1: front depth (view-space z) = MIN
        Blend   1 One One
        BlendOp 1 Max

        Pass 
        {
            HLSLPROGRAM
            #include "UnityCG.cginc"
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            struct Particle
            {
                float pressure;
                float density;
                float3 currentForce;
                float3 velocity;
                float3 position;
            };

            StructuredBuffer<Particle> _particlesBuffer;

            float _ParticleRadius;
            float4x4 _VP;
            float3 _CamRight, _CamUp; // world-space camera axes

            struct appdata {
                float3 vertex : POSITION;   // unused
                float2 uv     : TEXCOORD0;  // unit quad UVs (0..1)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VSOut{
                float4 pos : SV_POSITION;
                float2 q : TEXCOORD0; // [-1,1] quad coords
                float3 centerVS : TEXCOORD1; // particle center in view space
                float radius: TEXCOORD2; // world radius
            };

            struct MRTOut{
                float th : SV_Target0; 
                float zV : SV_Target1;
            };

            MRTOut frag(VSOut i);{
                MRTOut o;
                o.th = t;
                o.zV = zV;
                return o;
            }

            VSOut vert(appdata v, uint inst : SV_InstanceID)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                
                VSOut o;

                float2 q = v.uv * 2.0 - 1.0; 
                float r = _ParticleRadius;

                float3 Cw= _particlesBuffer[inst].position; // particle center in world space
                float3 Pw = Cw + r * (q.x * _CamRight + q.y * _CamUp);

                o.pos = mul(_VP, float4(Pw,1));
                o.q = q;
                o.centerVS = mul(UNITY_MATRIX_V, float4(Cw,1)).xyz;
                o.radius = r;

                return o;
            }

            struct PSOut{
                float4 thickness : SV_Target0;
                float4 front     : SV_Target1; 
            };

            PSOut frag(VSOut i)
            {
                PSOut o;
                
                float d2 = dot(i.quad, i.quad);
                if (d2 > 1.0){
                    discard;
                }

                float r  = i.radius;
                float dw = r * sqrt(d2);
                float a  = sqrt(max(1e-8, r*r - dw*dw)); // half-sphere depth

                float thickness = 2.0 * a;
                float zFront = i.centerVS.z + a; // view-space z (negative), front = less negative

                o.thickness = float4(thickness, 0, 0, 1);
                o.front = float4(zFront, 0, 0, 1);
                return o;
            }
            ENDHLSL
        }
    }
    Fallback Off
}