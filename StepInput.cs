using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Saru;

/// <summary>Integer input with visually joined decrement and increment buttons.</summary>
internal static class StepInput
{
    private const float PreferredStepButtonWidth = 20f;
    private const float MinimumStepButtonWidth = 14f;
    private const float MinimumInputWidth = 20f;

    public static bool InputInt(string label, ref int value, int step)
    {
        var totalWidth = Math.Max(MinimumInputWidth + MinimumStepButtonWidth * 2f, ImGui.CalcItemWidth());
        var stepButtonWidth = Math.Max(MinimumStepButtonWidth,
            Math.Min(PreferredStepButtonWidth, (totalWidth - MinimumInputWidth) * 0.5f));
        var inputWidth = Math.Max(MinimumInputWidth, totalWidth - stepButtonWidth * 2f);

        var adjustment = DrawStepButton(label, decrement: true, stepButtonWidth);
        ImGui.SameLine(0f, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.SetNextItemWidth(inputWidth);
        var changed = ImGui.InputInt(label, ref value, 0, 0);
        ImGui.PopStyleVar(2);
        ImGui.SameLine(0f, 0f);
        adjustment += DrawStepButton(label, decrement: false, stepButtonWidth);
        if (adjustment == 0) return changed;

        value += adjustment * step;
        return true;
    }

    private static int DrawStepButton(string inputLabel, bool decrement, float width)
    {
        var style = ImGui.GetStyle();
        var size = new Vector2(width, ImGui.GetFrameHeight());
        var position = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(decrement ? $"##step_minus_{inputLabel}" : $"##step_plus_{inputLabel}", size);
        var color = style.Colors[(int)(ImGui.IsItemActive() ? ImGuiCol.ButtonActive : ImGui.IsItemHovered() ? ImGuiCol.ButtonHovered : ImGuiCol.Button)];
        var rounding = Math.Min(Math.Max(4f, style.FrameRounding), Math.Min(size.X, size.Y) * 0.5f);
        DrawButtonHalf(ImGui.GetWindowDrawList(), position, size, decrement, color, rounding);

        var text = decrement ? "-" : "+";
        var textSize = ImGui.CalcTextSize(text);
        ImGui.GetWindowDrawList().AddText(position + (size - textSize) * 0.5f, ImGui.GetColorU32(style.Colors[(int)ImGuiCol.Text]), text);
        return clicked ? decrement ? -1 : 1 : 0;
    }

    private static void DrawButtonHalf(ImDrawListPtr drawList, Vector2 position, Vector2 size, bool leftHalf, Vector4 color, float rounding)
    {
        var maximum = position + size;
        var colorU32 = ImGui.GetColorU32(color);
        drawList.AddRectFilled(position, maximum, colorU32, rounding);
        if (rounding <= 0f) return;

        var squareMinimum = leftHalf ? new Vector2(maximum.X - rounding, position.Y) : position;
        var squareMaximum = leftHalf ? maximum : new Vector2(position.X + rounding, maximum.Y);
        drawList.AddRectFilled(squareMinimum, squareMaximum, colorU32);
    }
}
