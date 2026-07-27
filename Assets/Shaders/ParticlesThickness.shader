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
    StructuredBuffer<float3> _renderPositions; // Yu-Turk smoothed centres (sorted-slot indexed)
    StructuredBuffer<float4> _renderAniso;     // Stage 2: xyz = flatten axis (world), w = surface confidence

    float _ParticleRadius;
    float4x4 _VP;
    float4x4 _View;           // set by the renderer: CommandBuffer draws outside the camera loop, so UNITY_MATRIX_V is stale here
    float3 _CamRight, _CamUp; // world-space camera axes
    float _FlattenK;          // Stage 2: ellipsoid thickness along the normal (1 = sphere, <1 = flatter disc)
    float _MinSurfaceNbrs;    // neighbour count below which a particle is "isolated spray" and gets shrunk
    float _IsolatedScale;     // radius multiplier for fully-isolated particles (0 = cull, ~0.3 = shrink)

    struct appdata {
        float3 vertex : POSITION;   // unused
        float2 uv     : TEXCOORD0;  // unit quad UVs (0..1)
    };

    struct VSOut{
        float4 pos : SV_POSITION;
        float2 q : TEXCOORD0;        // [-1,1] quad coords
        float3 centerVS : TEXCOORD1; // particle center in view space
        float radius: TEXCOORD2;     // world radius
        float3 axisVS : TEXCOORD3;   // flatten axis in view space (unit)
        float k : TEXCOORD4;         // ellipsoid thickness along axisVS (1 = sphere)
    };

    VSOut vert(appdata v, uint inst : SV_InstanceID)
    {
        VSOut o;

        float2 q = v.uv * 2.0 - 1.0;
        float3 Cw = _renderPositions[inst];  // Yu-Turk smoothed centre
        float4 aniso = _renderAniso[inst];   // xyz = flatten axis, w = neighbour count

        // Isolation cull: a lone droplet (few neighbours) isn't part of the water sheet, so it
        // shouldn't render as a little dark-rimmed dome on the surface. Shrink it toward nothing
        // as the neighbour count drops below _MinSurfaceNbrs. Well-connected particles keep full r.
        float surf = smoothstep(_MinSurfaceNbrs, _MinSurfaceNbrs + 8.0, aniso.w);
        float r = _ParticleRadius * lerp(_IsolatedScale, 1.0, surf);

        float3 Pw = Cw + r * (q.x * _CamRight + q.y * _CamUp);

        o.pos = mul(_VP, float4(Pw, 1));
        o.q = q;
        o.centerVS = mul(_View, float4(Cw, 1)).xyz;
        o.radius = r;

        // Flatten only well-connected surface particles into surface-aligned discs (Stage 2).
        o.k = lerp(1.0, _FlattenK, surf);
        o.axisVS = normalize(mul((float3x3)_View, aniso.xyz)); // world axis -> view space

        return o;
    }

    // Ray-ellipsoid intersection along the view ray through this quad texel. The ellipsoid is a
    // sphere of radius r squashed to thickness k*r along axisVS (k = 1 -> exact sphere, the safe
    // fallback). Camera sits at the view-space origin, so the ray is O = 0, D = the direction to
    // the quad point at the centre's depth plane. Returns (tNear, tFar, D.z); discards on a miss.
    float3 EllipsoidHit(VSOut i)
    {
        float r = i.radius;
        float3 qp = i.centerVS + r * float3(i.q.x, i.q.y, 0.0); // quad point in view space
        float3 D = normalize(qp);                               // view ray direction
        float3 c = i.centerVS;
        float3 a = i.axisVS;

        // Metric M = I + s * a a^T stretches space along a by 1/k, mapping the thin ellipsoid to a
        // sphere of radius r: p is on the surface when (p-c)^T M (p-c) = r^2. s >= 0 for k <= 1, so
        // A = D^T M D >= 1 and the quadratic is always well-conditioned.
        float s = 1.0 / max(i.k * i.k, 1e-6) - 1.0;
        float3 rel = -c;                                        // O - c (O = 0)
        float3 MD  = D   + s * a * dot(a, D);
        float3 Mr  = rel + s * a * dot(a, rel);

        float A = dot(D, MD);
        float B = 2.0 * dot(rel, MD);
        float C = dot(rel, Mr) - r * r;
        float disc = B * B - 4.0 * A * C;
        if (disc < 0.0) discard;                                // ray misses the ellipsoid

        float sq = sqrt(disc);
        float tN = (-B - sq) / (2.0 * A);
        float tF = (-B + sq) / (2.0 * A);
        return float3(tN, tF, D.z);
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
                float3 h = EllipsoidHit(i);
                return float4(max(0.0, h.y - h.x), 0, 0, 1); // chord length (D is unit) through the ellipsoid
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
                float3 h = EllipsoidHit(i);
                return float4(h.x * h.z, 0, 0, 1); // near hit view-space z (tNear * D.z), negative
            }
            ENDHLSL
        }
    }
    Fallback Off
}
