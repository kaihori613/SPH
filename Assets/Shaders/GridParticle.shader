Shader "Instanced/GridTestParticleShader" {
	Properties{
		_MainTex("Albedo (RGB)", 2D) = "white" {}
		_Glossiness("Smoothness", Range(0,1)) = 0.5
		_Metallic("Metallic", Range(0,1)) = 0.0
        _Color("Color", Color) = (0.25, 0.5, 0.5, 1)
		_DensityRange ("Density Range", Range(0,500000)) = 1.0
		[Enum(Uniform,0,Speed,1,Density,2)] _ColorMode ("Color Mode", Float) = 1
		_ColorMin ("Color Scale Min", Float) = 0
		_ColorMax ("Color Scale Max", Float) = 6
		_Emission ("Emission", Range(0,2)) = 0.3
	}
		SubShader{
			Tags { "RenderType" = "Opaque" }
			LOD 200

			CGPROGRAM
			// Physically based Standard lighting model
			#pragma surface surf Standard addshadow fullforwardshadows
			#pragma multi_compile_instancing
			#pragma instancing_options procedural:setup

			sampler2D _MainTex;
			float _size;
            float3 _Color;
			float _DensityRange;
			float _ColorMode; // 0 = uniform, 1 = speed, 2 = density
			float _ColorMin;
			float _ColorMax;
			float _Emission;

			// Blue -> cyan -> green -> yellow -> red ramp (matches Seb Lague's speed coloring).
			float3 SpeedGradient(float t)
			{
				t = saturate(t);
				float3 blue = float3(0.1, 0.2, 0.9), cyan = float3(0.0, 0.9, 0.9),
				       green = float3(0.1, 0.9, 0.2), yellow = float3(1.0, 0.9, 0.1), red = float3(1.0, 0.15, 0.1);
				if (t < 0.25)      return lerp(blue,  cyan,   t / 0.25);
				else if (t < 0.5)  return lerp(cyan,  green, (t - 0.25) / 0.25);
				else if (t < 0.75) return lerp(green, yellow,(t - 0.5) / 0.25);
				else               return lerp(yellow, red,  (t - 0.75) / 0.25);
			}

			struct Input {
				float2 uv_MainTex;
			};

			struct Particle
			{
                float pressure;
                float density;
                float3 currentForce;
                float3 velocity;
				float3 position;
				
			};

		#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
			StructuredBuffer<Particle> _particlesBuffer;
		#endif

			void setup()
			{
			#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
				float3 pos = _particlesBuffer[unity_InstanceID].position;
				float size = _size;

				unity_ObjectToWorld._11_21_31_41 = float4(size, 0, 0, 0);
				unity_ObjectToWorld._12_22_32_42 = float4(0, size, 0, 0);
				unity_ObjectToWorld._13_23_33_43 = float4(0, 0, size, 0);
				unity_ObjectToWorld._14_24_34_44 = float4(pos.xyz, 1);
				unity_WorldToObject = unity_ObjectToWorld;
				unity_WorldToObject._14_24_34 *= -1;
				unity_WorldToObject._11_22_33 = 1.0f / unity_WorldToObject._11_22_33;
			#endif
			}

			half _Glossiness;
			half _Metallic;

			void surf(Input IN, inout SurfaceOutputStandard o) {
				float3 col = _Color;
				#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
					if (_ColorMode > 0.5)
					{
						Particle p = _particlesBuffer[unity_InstanceID];
						float val = (_ColorMode < 1.5) ? length(p.velocity) : p.density;
						float t = saturate((val - _ColorMin) / max(_ColorMax - _ColorMin, 1e-5));
						col = SpeedGradient(t);
					}
				#endif
				o.Albedo = col;
				o.Emission = col * _Emission;
			}
			ENDCG
		}
			FallBack "Diffuse"
}