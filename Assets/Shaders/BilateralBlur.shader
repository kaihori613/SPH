Shader "Fluid/BilateralBlur"
{
    // Passes 0/1 blur an arbitrary texture guided by a separate depth texture (_Guide).
    // Passes 2/3 blur DEPTH itself, using _MainTex as its own guide — this is the one that
    // matters for surface quality (Green, "Screen Space Fluid Rendering for Games", GDC 2010):
    // normals are derived from depth, so depth must be smoothed or the surface reads as
    // per-particle bumps. Bilateral (not Gaussian) so silhouettes between separate fluid
    // bodies stay sharp instead of smearing into each other.
    Properties {
        _Radius ("Radius (pixels)", Int) = 8
        _SigmaS ("Sigma Spatial", Float) = 4.0
        _SigmaR ("Sigma Range (depth)", Float) = 0.5
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "UnityCG.cginc"
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _Guide;
        float4    _MainTex_TexelSize;   // (1/w, 1/h, w, h)

        int   _Radius;
        float _SigmaS;
        float _SigmaR;

        // Bilateral tap loop along `step`. `guideIsSelf` picks whether range weights come from
        // _MainTex (depth blurring itself) or the separate _Guide texture.
        float4 BlurAlong(float2 uv, float2 step, bool guideIsSelf)
        {
            float zc = guideIsSelf ? tex2D(_MainTex, uv).r : tex2D(_Guide, uv).r;

            // Background: nothing to smooth, and blending with -1e20 would poison neighbours.
            if (zc <= -1e19) return tex2D(_MainTex, uv);

            float4 acc  = tex2D(_MainTex, uv);
            float  wsum = 1.0;

            float inv2s2 = 0.5 / max(_SigmaS * _SigmaS, 1e-8);
            float inv2r2 = 0.5 / max(_SigmaR * _SigmaR, 1e-8);

            [loop]
            for (int j = 1; j <= _Radius; j++)
            {
                float2 o = step * j;
                float wS = exp(-(j * j) * inv2s2);

                float2 uvA = uv - o;
                float2 uvB = uv + o;

                float zA = guideIsSelf ? tex2D(_MainTex, uvA).r : tex2D(_Guide, uvA).r;
                float zB = guideIsSelf ? tex2D(_MainTex, uvB).r : tex2D(_Guide, uvB).r;

                // Background neighbours get zero weight rather than dragging the surface out.
                float dA = zA - zc, dB = zB - zc;
                float wA = (zA <= -1e19) ? 0.0 : wS * exp(-dA * dA * inv2r2);
                float wB = (zB <= -1e19) ? 0.0 : wS * exp(-dB * dB * inv2r2);

                acc  += tex2D(_MainTex, uvA) * wA;
                acc  += tex2D(_MainTex, uvB) * wB;
                wsum += wA + wB;
            }

            return acc / max(wsum, 1e-6);
        }
        ENDHLSL

        Pass // 0: horizontal, guided by _Guide
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            { return BlurAlong(i.uv, float2(_MainTex_TexelSize.x, 0), false); }
            ENDHLSL
        }

        Pass // 1: vertical, guided by _Guide
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            { return BlurAlong(i.uv, float2(0, _MainTex_TexelSize.y), false); }
            ENDHLSL
        }

        Pass // 2: horizontal DEPTH blur (self-guided)
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            { return BlurAlong(i.uv, float2(_MainTex_TexelSize.x, 0), true); }
            ENDHLSL
        }

        Pass // 3: vertical DEPTH blur (self-guided)
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            { return BlurAlong(i.uv, float2(0, _MainTex_TexelSize.y), true); }
            ENDHLSL
        }
    }
}
