Shader "Fluid/ParticlesThickness"
{
    // Two single-RT passes instead of one MRT pass: per-render-target blend state
    // (Blend N / BlendOp N) is silently ignored on this Unity/Metal path — both targets
    // fell back to replace, so thickness never accumulated. Global (non-indexed) blend
    // state per pass is reliable everywhere. The renderer issues one draw per pass.
    Properties{}

    HLSLINCLUDE
    #include "UnityCG.cginc"

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
    float4x4 _View;           // set by the renderer: CommandBuffer draws outside the camera loop, so UNITY_MATRIX_V is stale here
    float3 _CamRight, _CamUp; // world-space camera axes

    struct appdata {
        float3 vertex : POSITION;   // unused
        float2 uv     : TEXCOORD0;  // unit quad UVs (0..1)
    };

    struct VSOut{
        float4 pos : SV_POSITION;
        float2 q : TEXCOORD0;        // [-1,1] quad coords
        float3 centerVS : TEXCOORD1; // particle center in view space
        float radius: TEXCOORD2;     // world radius
    };

    VSOut vert(appdata v, uint inst : SV_InstanceID)
    {
        VSOut o;

        float2 q = v.uv * 2.0 - 1.0;
        float r = _ParticleRadius;

        float3 Cw = _particlesBuffer[inst].position; // particle center in world space
        float3 Pw = Cw + r * (q.x * _CamRight + q.y * _CamUp);

        o.pos = mul(_VP, float4(Pw, 1));
        o.q = q;
        o.centerVS = mul(_View, float4(Cw, 1)).xyz;
        o.radius = r;

        return o;
    }

    // Sphere depth along the view ray for this quad texel; discards outside the circle.
    float SphereHalfDepth(VSOut i)
    {
        float d2 = dot(i.q, i.q);
        if (d2 > 1.0) discard;
        float r = i.radius;
        float dw = r * sqrt(d2);
        return sqrt(max(1e-8, r * r - dw * dw));
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Cull Off ZWrite Off ZTest Always

        // Pass 0: thickness, additive accumulation
        Pass
        {
            Blend One One
            BlendOp Add
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment fragThickness

            float4 fragThickness(VSOut i) : SV_Target
            {
                float a = SphereHalfDepth(i);
                return float4(2.0 * a, 0, 0, 1); // chord length through the sphere
            }
            ENDHLSL
        }

        // Pass 1: front depth (view-space z, negative; front = largest), Max blend
        Pass
        {
            Blend One One
            BlendOp Max
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment fragFrontDepth

            float4 fragFrontDepth(VSOut i) : SV_Target
            {
                float a = SphereHalfDepth(i);
                return float4(i.centerVS.z + a, 0, 0, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
