// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'


Shader "Self-Illumin/AngryBots/InterlacePatternBlend" {
	Properties {
		_MainTex ("Base", 2D) = "white" {}
		_TintColor ("TintColor", Color) = (1,1,1,1) // needed simply for shader replacement           
		_InterlacePattern ("InterlacePattern", 2D) = "white" {}
		_Illum ("_Illum", 2D) = "white" {}
		_EmissionLM ("Emission (Lightmapper)", Float) = 1.0	
	}

	CGINCLUDE

		#include "UnityCG.cginc"
	
		sampler2D _MainTex;
		sampler2D _InterlacePattern;
						
		half4 _InterlacePattern_ST;
		half4 _MainTex_ST;
		fixed4 _TintColor;				
						
		struct v2f {
			half4 pos : SV_POSITION;
			half2 uv : TEXCOORD0;
			half2 uv2 : TEXCOORD1;
		};
	
		v2f vert(appdata_full v)
		{
			v2f o;
			
			o.pos = UnityObjectToClipPos (v.vertex);	
			o.uv.xy = TRANSFORM_TEX(v.texcoord.xy, _MainTex);
			o.uv2.xy = TRANSFORM_TEX(v.texcoord.xy, _InterlacePattern) + _Time.xx * _InterlacePattern_ST.zw;
					
			return o; 
		}
		
		fixed4 frag( v2f i ) : COLOR
		{	
			fixed4 colorTex = tex2D (_MainTex, i.uv);
			fixed4 interlace = tex2D (_InterlacePattern, i.uv2);
			colorTex *= interlace;
			
			return colorTex;
		}

	ENDCG

	SubShader {
    	Tags {"RenderType" = "Transparent" "Queue" = "Transparent" "Reflection" = "RenderReflectionTransparentBlend" }
		Cull Off
		ZWrite Off
       	Blend SrcAlpha OneMinusSrcAlpha
			
		Pass {
	
			CGPROGRAM
			
			#pragma vertex vert
			#pragma fragment frag
			#pragma fragmentoption ARB_precision_hint_fastest 
			
			ENDCG
		 
		}
				
	} 
	FallBack Off
}

