// Final composite for the try-on view. Per pixel:
//   1. Real clothing that is NOT covered by the AR shirt (a loose real shirt sticking out past its edge) is
//      replaced, but only inside the shirts' screen boxes above the hip line (so trousers stay): with the colour
//      of the nearest shirt pixel, or with the clean background plate when one was captured.
//   2. The shirt render (premultiplied by its MSAA-resolved alpha) is laid over the result.
//
// Inputs (all set by TryOnCompositor):
//   _MainTex         webcam frame (unmirrored, Unity orientation; _FlipCameraY if the driver stores it upside down)
//   _GarmentTex      garment camera render, already in display (mirrored) space, alpha = coverage
//   _PersonMask0..2  MediaPipe per-person masks, Unity orientation, unmirrored
//   _ClassMask       "clothes" probability from the multiclass segmenter, same layout as the person masks
//   _PlateTex        clean background captured while nobody was in view (same layout as _MainTex)
//   _Zones[3]        viewport boxes (x0, y0, x1, y1) where real clothing may be replaced
Shader "ARTryOn/Composite"
{
    Properties
    {
        _MainTex ("Camera", 2D) = "black" {}
        _GarmentTex ("Garment", 2D) = "black" {}
        _ClassMask ("Clothes Mask", 2D) = "black" {}
        _PlateTex ("Background Plate", 2D) = "black" {}
        _PersonMask0 ("Person Mask 0", 2D) = "black" {}
        _PersonMask1 ("Person Mask 1", 2D) = "black" {}
        _PersonMask2 ("Person Mask 2", 2D) = "black" {}
        _PersonMaskCount ("Person Mask Count", Int) = 0
        _ZoneCount ("Zone Count", Int) = 0
        _HasClassMask ("Has Clothes Mask", Float) = 0
        _HasPlate ("Has Plate", Float) = 0
        _Mirror ("Mirror", Float) = 1
        _FlipCameraY ("Flip Camera Y", Float) = 0
        _FillRadius ("Edge Fill Radius (px)", Float) = 40
        _ClothesThreshold ("Clothes Threshold", Range(0, 1)) = 0.5
        _MaskSoftness ("Mask Edge Softness", Range(0.01, 0.5)) = 0.15
        _Debug ("Debug Masks", Float) = 0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex, _GarmentTex, _ClassMask, _PlateTex;
            sampler2D _PersonMask0, _PersonMask1, _PersonMask2;
            float4 _GarmentTex_TexelSize;
            float4 _Zones[3];
            int _PersonMaskCount, _ZoneCount;
            float _HasClassMask, _HasPlate, _Mirror, _FlipCameraY, _FillRadius, _ClothesThreshold, _MaskSoftness, _Debug;

            float PersonCoverage(float2 uv)
            {
                float p = 0;
                if (_PersonMaskCount > 0) p = max(p, tex2D(_PersonMask0, uv).r);
                if (_PersonMaskCount > 1) p = max(p, tex2D(_PersonMask1, uv).r);
                if (_PersonMaskCount > 2) p = max(p, tex2D(_PersonMask2, uv).r);
                return p;
            }

            // Soft-edged membership in any shirt zone, so the replaced area never ends in a straight line.
            float InZone(float2 uv)
            {
                const float e = 0.02;
                float best = 0;
                [unroll] for (int i = 0; i < 3; i++)
                {
                    if (i >= _ZoneCount) break;
                    float4 z = _Zones[i];
                    float w = smoothstep(z.x - e, z.x, uv.x) * (1 - smoothstep(z.z, z.z + e, uv.x)) *
                              smoothstep(z.y - e, z.y + e, uv.y) * (1 - smoothstep(z.w, z.w + e, uv.y));
                    best = max(best, w);
                }
                return best;
            }

            // Colour of the nearest opaque shirt pixel within _FillRadius (8 directions x 3 rings).
            // Alpha fades a little with distance so the fill blends into the real image.
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
                        if (g.a > 0.9) return float4(g.rgb / g.a, 1.0 - 0.15 * (ring - 1));
                    }
                }
                return 0;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;                                        // display space (mirrored in selfie view)
                float2 maskUV = float2(_Mirror > 0.5 ? 1 - uv.x : uv.x, uv.y);
                float2 camUV = float2(maskUV.x, _FlipCameraY > 0.5 ? 1 - uv.y : uv.y);

                float3 baseCol = tex2D(_MainTex, camUV).rgb;
                float4 garment = tex2D(_GarmentTex, uv);

                // 1. Hide real clothing the shirt does not cover.
                float uncovered = 0;
                if (_HasClassMask > 0.5)
                {
                    float clothes = smoothstep(_ClothesThreshold - _MaskSoftness, _ClothesThreshold + _MaskSoftness,
                                               tex2D(_ClassMask, maskUV).r);
                    float person = saturate(PersonCoverage(maskUV) * 2); // only on tracked people
                    uncovered = clothes * person * InZone(uv) * (1 - garment.a);
                    if (uncovered > 0.01)
                    {
                        if (_HasPlate > 0.5)
                            baseCol = lerp(baseCol, tex2D(_PlateTex, camUV).rgb, uncovered);
                        else
                        {
                            float4 fill = NearestGarment(uv);
                            baseCol = lerp(baseCol, fill.rgb, uncovered * fill.a);
                        }
                    }
                }

                // 2. Shirt over (the MSAA-resolved render texture is effectively premultiplied).
                float3 rgb = garment.rgb + baseCol * (1 - garment.a);

                if (_Debug > 0.5)
                {
                    rgb = lerp(rgb, float3(0, 1, 0.3), 0.25 * PersonCoverage(maskUV));
                    rgb = lerp(rgb, float3(1, 0.1, 0.6), 0.6 * uncovered);
                }
                return fixed4(rgb, 1);
            }
            ENDCG
        }
    }
}
