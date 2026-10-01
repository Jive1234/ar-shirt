// Invisible occluder: writes depth, writes no colour (and no alpha), so garment fragments behind it are
// rejected and the composite shows the real camera pixel there (hands/forearms/head in front of the shirt).
// Works in Built-in RP and URP (the pass falls into URP's SRPDefaultUnlit).
Shader "ARTryOn/DepthOnlyOccluder"
{
    SubShader
    {
        // Draw before the garment (Geometry = 2000) so depth is already in place.
        Tags { "Queue" = "Geometry-10" "RenderType" = "Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back
        }
    }
}
