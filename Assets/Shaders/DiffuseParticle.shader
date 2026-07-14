Shader "Instanced/DiffuseParticle" {
	Properties{
		_Glossiness("Smoothness", Range(0,1)) = 0.2
		_Metallic("Metallic", Range(0,1)) = 0.0
	}
		SubShader{
			Tags { "RenderType" = "Opaque" }
			LOD 100

			CGPROGRAM
			#pragma surface surf Standard addshadow fullforwardshadows
			#pragma multi_compile_instancing
			#pragma instancing_options procedural:setup

			float _size;

			struct Input {
				float2 uv_MainTex;
			};

			struct DiffuseParticle
			{
				float3 position;
				float3 velocity;
				float lifetime;
				int type;   // -1 inactive, 0 spray, 1 foam, 2 bubble
			};

		#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
			StructuredBuffer<DiffuseParticle> _diffuseBuffer;
		#endif

			void setup()
			{
			#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
				DiffuseParticle d = _diffuseBuffer[unity_InstanceID];
				// Collapse inactive/expired markers to zero size so they don't render.
				float size = (d.type < 0 || d.lifetime <= 0.0) ? 0.0 : _size;
				float3 pos = d.position;

				unity_ObjectToWorld._11_21_31_41 = float4(size, 0, 0, 0);
				unity_ObjectToWorld._12_22_32_42 = float4(0, size, 0, 0);
				unity_ObjectToWorld._13_23_33_43 = float4(0, 0, size, 0);
				unity_ObjectToWorld._14_24_34_44 = float4(pos.xyz, 1);
				unity_WorldToObject = unity_ObjectToWorld;
				unity_WorldToObject._14_24_34 *= -1;
				// Collapsed instances have size 0; avoid 1/0 -> Inf (geometry is degenerate anyway).
				unity_WorldToObject._11_22_33 = 1.0f / max(unity_WorldToObject._11_22_33, 1e-6);
			#endif
			}

			half _Glossiness;
			half _Metallic;

			void surf(Input IN, inout SurfaceOutputStandard o) {
				float3 col = float3(1, 1, 1); // spray / foam = white
			#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
				if (_diffuseBuffer[unity_InstanceID].type == 2) col = float3(0.8, 0.9, 1.0); // bubble tint
			#endif
				o.Albedo = col;
				o.Metallic = _Metallic;
				o.Smoothness = _Glossiness;
			}
			ENDCG
		}
			FallBack "Diffuse"
}
