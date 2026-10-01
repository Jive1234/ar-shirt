// Final composite for the try-on view. Per pixel:
//   1. Real clothing that is NOT covered by the virtual garment (a loose real shirt sticking out past the
//      AR shirt's edge) is replaced: with the clean background plate when one was captured, otherwise
//      with the colour of the nearest garment pixel (the garment "grows" over the leftover real fabric).
//   2. The garment render (premultiplied by its MSAA-resolved alpha) is laid over the result.
//
// Inputs (all set by TryOnCompositor):
//   _MainTex         webcam frame for this result (unmirrored, Unity orientation)
//   _GarmentTex      garment camera render, already in display (mirrored) space, alpha = coverage
//   _PersonMask0..3  MediaPipe per-person masks (top-left origin, unmirrored), R = confidence
//   _ClassMask       optional multiclass segmentation, R = "clothes" probability (same layout as person masks)
//   _PlateTex        clean background captured while nobody was in frame (same layout as _MainTex)
Shader "ARTryOn/Composite"
{
    Properties
    {
        _MainTex ("Camera", 2D) = "black" {}
        _GarmentTex ("Garment", 2D) = "black" {}
        _ClassMask ("Class Mask", 2D) = "black" {}
        _PlateTex ("Background Plate", 2D) = "black" {}
        _PersonMask0 ("Person Mask 0", 2D) = "black" {}
        _PersonMask1 ("Person Mask 1", 2D) = "black" {}
        _PersonMask2 ("Person Mask 2", 2D) = "black" {}
        _PersonMask3 ("Person Mask 3", 2D) = "black" {}
        _PersonMaskCount ("Person Mask Count", Int) = 0
        _HasClassMask ("Has Class Mask", Float) = 0
        _HasPlate ("Has Plate", Float) = 0
        _MirrorMask ("Mirror", Float) = 1
        _FillRadius ("Edge Fill Radius (px)", Float) = 24
        _ClothesThreshold ("Clothes Threshold", Range(0, 1)) = 0.5
        _MaskSoftness ("Mask Edge Softness", Range(0.01, 0.5)) = 0.15
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex, _GarmentTex, _ClassMask, _PlateTex;
            sampler2D _PersonMask0, _PersonMask1, _PersonMask2, _PersonMask3;
            float4 _GarmentTex_TexelSize;
            int _PersonMaskCount;
            float _HasClassMask, _HasPlate, _MirrorMask, _FillRadius, _ClothesThreshold, _MaskSoftness;

            float PersonCoverage(float2 maskUV)
            {
                float p = 0;
                if (_PersonMaskCount > 0) p = max(p, tex2D(_PersonMask0, maskUV).r);
                if (_PersonMaskCount > 1) p = max(p, tex2D(_PersonMask1, maskUV).r);
                if (_PersonMaskCount > 2) p = max(p, tex2D(_PersonMask2, maskUV).r);
                if (_PersonMaskCount > 3) p = max(p, tex2D(_PersonMask3, maskUV).r);
                return p;
            }

            // Colour of the closest opaque garment pixel within _FillRadius (8 directions x 3 rings).
            // Returns alpha = 1 if found. Only runs on the few pixels of uncovered real clothing.
            float4 NearestGarment(float2 uv)
            {
                const float2 dirs[8] =
                {
                    float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
                    float2(0.7071, 0.7071), float2(-0.7071, 0.7071), float2(0.7071, -0.7071), float2(-0.7071, -0.7071)
                };
                [unroll] for (int ring = 1; ring <= 3; ring++)
                {
                    float r = _FillRadius * ring / 3.0;
                    [unroll] for (int k = 0; k < 8; k++)
                    {
                        float4 g = tex2Dlod(_GarmentTex, float4(uv + dirs[k] * r * _GarmentTex_TexelSize.xy, 0, 0));
                        if (g.a > 0.9) return float4(g.rgb / g.a, 1);
                    }
                }
                return 0;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;                                   // display space (mirrored if selfie view)
                float2 camUV = float2(_MirrorMask > 0.5 ? 1 - uv.x : uv.x, uv.y);
                float2 maskUV = float2(camUV.x, 1 - camUV.y);       // MediaPipe masks: top-left origin

                float3 baseCol = tex2D(_MainTex, camUV).rgb;
                float4 garment = tex2D(_GarmentTex, uv);

                // --- 1. hide real clothing the garment does not cover --------------------------------------
                if (_HasClassMask > 0.5)
                {
                    float clothes = smoothstep(_ClothesThreshold - _MaskSoftness, _ClothesThreshold + _MaskSoftness,
                                               tex2D(_ClassMask, maskUV).r);
                    // Restrict to tracked people so clothes on a chair or a poster are left alone.
                    float person = saturate(PersonCoverage(maskUV) * 2);
                    float uncovered = clothes * person * (1 - garment.a);

                    if (uncovered > 0.01)
                    {
                        if (_HasPlate > 0.5)
                        {
                            baseCol = lerp(baseCol, tex2D(_PlateTex, camUV).rgb, uncovered);
                        }
                        else
                        {
                            float4 fill = NearestGarment(uv);
                            baseCol = lerp(baseCol, fill.rgb, uncovered * fill.a);
                        }
                    }
                }

                // --- 2. garment over (MSAA-resolved RT is effectively premultiplied) ------------------------
                float3 rgb = garment.rgb + baseCol * (1 - garment.a);
                return fixed4(rgb, 1);
            }
            ENDCG
        }
    }
}
