Shader "Instanced/DiffuseParticle" {
	Properties{
		_size("Size (set by SPH)", Float) = 0.3
		_FadeTime("Fade-out seconds", Float) = 0.6
		_SprayScale("Spray size x", Range(0.2, 1.5)) = 0.6
		_FoamScale("Foam size x", Range(0.2, 2.0)) = 1.0
		_BubbleScale("Bubble size x", Range(0.2, 2.0)) = 0.9
		_Brightness("Foam brightness", Range(0, 4)) = 1.6
		_FoamAlpha("Foam opacity", Range(0, 1)) = 1.0
		_BubbleAlpha("Bubble opacity", Range(0, 1)) = 0.45
		_BubbleRim("Bubble rim strength", Range(0, 3)) = 1.3
		_BubbleRimPower("Bubble rim sharpness", Range(0.5, 6)) = 2.5
		_BubbleColor("Bubble color", Color) = (0.75, 0.88, 1.0, 1)
	}
	SubShader{
		// Transparent so foam/spray reads as soft, glowing specks that fade instead of hard opaque balls.
		Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
		LOD 100

		CGPROGRAM
		// alpha:fade -> standard alpha blending with per-instance opacity. Transparent, so no shadow pass.
		#pragma surface surf Standard alpha:fade
		#pragma multi_compile_instancing
		#pragma instancing_options procedural:setup
		#pragma target 4.5

		float _size, _FadeTime, _SprayScale, _FoamScale, _BubbleScale, _Brightness, _FoamAlpha, _BubbleAlpha;
		float _BubbleRim, _BubbleRimPower;
		float4 _BubbleColor;

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

		struct Input {
			float3 worldPos;
			float3 worldNormal;
		};

		void setup()
		{
		#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
			DiffuseParticle d = _diffuseBuffer[unity_InstanceID];

			// Per-type size: spray is fine mist, foam is medium, bubbles are small.
			float typeScale = (d.type == 0) ? _SprayScale : ((d.type == 2) ? _BubbleScale : _FoamScale);
			float size = (d.type < 0 || d.lifetime <= 0.0) ? 0.0 : _size * typeScale;
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

		void surf(Input IN, inout SurfaceOutputStandard o) {
			float3 col = float3(1, 1, 1);
			float baseAlpha = _FoamAlpha;
			float glow = _Brightness;
			float fade = 1.0;
			int ptype = 1;

		#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
			DiffuseParticle d = _diffuseBuffer[unity_InstanceID];
			// Fade opacity out over the last _FadeTime seconds of life so markers dissolve, not pop.
			fade = saturate(d.lifetime / max(_FadeTime, 1e-3));
			ptype = d.type;

			if (d.type == 2)        // bubble
			{
				col = _BubbleColor.rgb;
				baseAlpha = _BubbleAlpha;
			}
			else if (d.type == 0)   // spray: airborne mist, brightest
			{
				col = float3(1, 1, 1);
				baseAlpha = min(1.0, _FoamAlpha * 0.95);
				glow = _Brightness * 1.35;
			}
			// type 1 (foam) uses the white defaults
		#endif

			// View-dependent rim (Fresnel), computed in world space to avoid tangent-space ambiguity.
			float3 V = normalize(_WorldSpaceCameraPos.xyz - IN.worldPos);
			float ndv = saturate(dot(V, normalize(IN.worldNormal)));
			float rim = 1.0 - ndv;

			o.Albedo = col;
			o.Metallic = 0.0;

			if (ptype == 2)
			{
				// Hollow air bubble: near-transparent centre, bright thin shell at the silhouette.
				float shell = pow(rim, _BubbleRimPower);
				o.Emission = col * (_Brightness * 0.12 + shell * _BubbleRim) * fade;
				o.Alpha = saturate((baseAlpha * 0.2 + shell * _BubbleRim) * fade);
				o.Smoothness = 0.7;
			}
			else
			{
				// Foam / spray: bright soft speck that fades slightly toward its edge.
				o.Emission = col * glow * fade;
				o.Alpha = saturate(baseAlpha * fade * (0.55 + 0.45 * ndv));
				o.Smoothness = 0.15;
			}
		}
		ENDCG
	}
	FallBack Off
}
