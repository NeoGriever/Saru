using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Saru;

internal readonly record struct WorldMarker(Vector3 Position, float Scale, byte Red, byte Green, byte Blue, uint TerritoryId, uint Instance);

internal static class WorldMarkerRenderer
{
    private const int Sides = 8;

    public static void Draw(WorldMarker marker, bool foreground = false, string? label = null)
    {
        var position = marker.Position;
        var scale = marker.Scale;
        var shaftTop = new Vector2[Sides];
        var shaftBottom = new Vector2[Sides];
        var headBase = new Vector2[Sides];

        if (!Plugin.GameGui.WorldToScreen(position + new Vector3(0, 0.1f * scale, 0), out var tip)) return;
        for (var i = 0; i < Sides; i++)
        {
            var angle = 2f * MathF.PI * i / Sides;
            var direction = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            if (!Plugin.GameGui.WorldToScreen(position + direction * (0.14f * scale) + new Vector3(0, 1.8f * scale, 0), out shaftTop[i]) ||
                !Plugin.GameGui.WorldToScreen(position + direction * (0.14f * scale) + new Vector3(0, 0.7f * scale, 0), out shaftBottom[i]) ||
                !Plugin.GameGui.WorldToScreen(position + direction * (0.45f * scale) + new Vector3(0, 0.7f * scale, 0), out headBase[i])) return;
        }

        var color = ImGui.ColorConvertFloat4ToU32(new Vector4(marker.Red / 255f, marker.Green / 255f, marker.Blue / 255f, 1f));
        var drawList = foreground ? ImGui.GetForegroundDrawList() : ImGui.GetBackgroundDrawList();
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            drawList.AddTriangleFilled(shaftTop[i], shaftBottom[i], shaftBottom[next], color);
            drawList.AddTriangleFilled(shaftTop[i], shaftBottom[next], shaftTop[next], color);
            drawList.AddTriangleFilled(headBase[i], tip, headBase[next], color);
        }

        const uint edgeColor = 0xFF000000;
        const float edgeThickness = 1.8f;
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            drawList.AddLine(shaftTop[i], shaftTop[next], edgeColor, edgeThickness);
            drawList.AddLine(shaftTop[i], shaftBottom[i], edgeColor, edgeThickness);
            drawList.AddLine(headBase[i], headBase[next], edgeColor, edgeThickness);
            drawList.AddLine(headBase[i], tip, edgeColor, edgeThickness);
        }

        if (label == null || !Plugin.GameGui.WorldToScreen(position + new Vector3(0, 1.25f * scale, 0), out var badge)) return;
        var textSize = ImGui.CalcTextSize(label);
        var radius = MathF.Max(10f, textSize.X * 0.5f + 4f);
        drawList.AddCircleFilled(badge, radius, 0xFF000000);
        drawList.AddText(badge - textSize * 0.5f, 0xFFFFFFFF, label);
    }
}
