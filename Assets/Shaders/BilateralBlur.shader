Shader "Fluid/BilateralBlur"
{
    Properties { 
        _Radius ("Radius (pixels)", Int) = 3
        _SigmaS ("Sigma Spatial", Float) = 2.5
        _SigmaR ("Sigma Range (depth)", Float) = 0.02
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass // 0: Horizontal
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex   vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;              // thickness RT (low-res)
            sampler2D _Guide;                // front-depth RT (low-res, view-space z)
            float4    _MainTex_TexelSize;    // (1/w, 1/h, w, h)

            int   _Radius;
            float _SigmaS;
            float _SigmaR;
            
            float4 frag(v2f_img i) : SV_Target {

                float2 uv = i.uv;

                float zc = tex2D(_Guide, uv).r;
             
                if (zc <= -1e19) {
                    return tex2D(_MainTex, uv);
                }

                float4 acc   = tex2D(_MainTex, uv);
                float  wsum  = 1.0;

                float inv2s2 = 0.5 / (_SigmaS * _SigmaS);
                float inv2r2 = 0.5 / (_SigmaR * _SigmaR);

                float2 step = float2(_MainTex_TexelSize.x, 0);

                [unroll(32)]
                for (int j = 1; j <= _Radius; j++)
                {
                    float2 o  = step * j;
                    float  zl = tex2D(_Guide, uv - o).r;
                    float  zr = tex2D(_Guide, uv + o).r;

                    // spatial weights (gaussian over pixel distance)
                    float wS = exp(- (j*j) * inv2s2);

                    // range weights (gaussian over depth difference)
                    float wRl = exp(- (zl - zc)*(zl - zc) * inv2r2);
                    float wRr = exp(- (zr - zc)*(zr - zc) * inv2r2);

                    float wl = wS * wRl;
                    float wr = wS * wRr;

                    acc  += tex2D(_MainTex, uv - o) * wl;
                    acc  += tex2D(_MainTex, uv + o) * wr;
                    wsum += wl + wr;
                }

                return acc / max(wsum, 1e-6);
            }
            ENDHLSL
        }

        Pass // 1: Vertical
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex   vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _Guide;
            float4    _MainTex_TexelSize;

            int   _Radius;
            float _SigmaS;
            float _SigmaR;

            float4 frag(v2f_img i) : SV_Target {

                float2 uv = i.uv;

                float zc = tex2D(_Guide, uv).r;
                if (zc <= -1e19) return tex2D(_MainTex, uv);

                float4 acc   = tex2D(_MainTex, uv);
                float  wsum  = 1.0;

                float inv2s2 = 0.5 / (_SigmaS * _SigmaS);
                float inv2r2 = 0.5 / (_SigmaR * _SigmaR);

                float2 step = float2(0, _MainTex_TexelSize.y);

                [unroll(32)]
                for (int j = 1; j <= _Radius; j++)
                {
                    float2 o  = step * j;
                    float  zd = tex2D(_Guide, uv - o).r;
                    float  zu = tex2D(_Guide, uv + o).r;

                    float wS = exp(- (j*j) * inv2s2);
                    float wRd = exp(- (zd - zc)*(zd - zc) * inv2r2);
                    float wRu = exp(- (zu - zc)*(zu - zc) * inv2r2);

                    float wd = wS * wRd;
                    float wu = wS * wRu;

                    acc  += tex2D(_MainTex, uv - o) * wd;
                    acc  += tex2D(_MainTex, uv + o) * wu;
                    wsum += wd + wu;
                }

                return acc / max(wsum, 1e-6);
            }
            ENDHLSL
        }
    }
}