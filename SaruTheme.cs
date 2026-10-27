using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Saru;

internal static class SaruTheme
{
    public static readonly Vector4 Text = new(0.94f, 0.97f, 1.00f, 1f);
    public static readonly Vector4 Muted = new(0.68f, 0.75f, 0.84f, 1f);
    public static readonly Vector4 Accent = new(0.42f, 0.91f, 0.81f, 1f);
    public static readonly Vector4 Blue = new(0.55f, 0.76f, 1.00f, 1f);
    public static readonly Vector4 Success = new(0.48f, 0.90f, 0.69f, 1f);
    public static readonly Vector4 Warning = new(1.00f, 0.76f, 0.43f, 1f);
    public static readonly Vector4 Danger = new(1.00f, 0.54f, 0.59f, 1f);
    public static readonly Vector4 Surface = new(0.08f, 0.12f, 0.18f, 1f);
    public static readonly Vector4 Raised = new(0.11f, 0.17f, 0.25f, 1f);

    private static readonly (ImGuiCol Color, Vector4 Value)[] Colors =
    [
        (ImGuiCol.Text, Text),
        (ImGuiCol.TextDisabled, Muted),
        (ImGuiCol.WindowBg, new Vector4(0.045f, 0.069f, 0.105f, 0.98f)),
        (ImGuiCol.ChildBg, Surface),
        (ImGuiCol.PopupBg, new Vector4(0.075f, 0.11f, 0.17f, 0.99f)),
        (ImGuiCol.Border, new Vector4(0.25f, 0.34f, 0.45f, 0.85f)),
        (ImGuiCol.FrameBg, new Vector4(0.10f, 0.15f, 0.22f, 1f)),
        (ImGuiCol.FrameBgHovered, new Vector4(0.15f, 0.23f, 0.32f, 1f)),
        (ImGuiCol.FrameBgActive, new Vector4(0.18f, 0.29f, 0.39f, 1f)),
        (ImGuiCol.TitleBg, new Vector4(0.055f, 0.09f, 0.14f, 1f)),
        (ImGuiCol.TitleBgActive, new Vector4(0.09f, 0.15f, 0.22f, 1f)),
        (ImGuiCol.Button, new Vector4(0.14f, 0.24f, 0.34f, 1f)),
        (ImGuiCol.ButtonHovered, new Vector4(0.19f, 0.36f, 0.47f, 1f)),
        (ImGuiCol.ButtonActive, new Vector4(0.23f, 0.45f, 0.55f, 1f)),
        (ImGuiCol.Header, new Vector4(0.13f, 0.21f, 0.30f, 1f)),
        (ImGuiCol.HeaderHovered, new Vector4(0.18f, 0.31f, 0.42f, 1f)),
        (ImGuiCol.HeaderActive, new Vector4(0.21f, 0.39f, 0.48f, 1f)),
        (ImGuiCol.CheckMark, Accent),
        (ImGuiCol.SliderGrab, Accent),
        (ImGuiCol.SliderGrabActive, Blue),
        (ImGuiCol.Separator, new Vector4(0.25f, 0.34f, 0.45f, 0.8f)),
        (ImGuiCol.ScrollbarBg, new Vector4(0.05f, 0.08f, 0.12f, 1f)),
        (ImGuiCol.ScrollbarGrab, new Vector4(0.27f, 0.38f, 0.48f, 1f)),
        (ImGuiCol.ScrollbarGrabHovered, new Vector4(0.34f, 0.50f, 0.60f, 1f)),
        (ImGuiCol.ScrollbarGrabActive, new Vector4(0.42f, 0.65f, 0.72f, 1f)),
        (ImGuiCol.Tab, new Vector4(0.10f, 0.16f, 0.23f, 1f)),
        (ImGuiCol.TabHovered, new Vector4(0.20f, 0.35f, 0.44f, 1f)),
        (ImGuiCol.TabActive, new Vector4(0.16f, 0.31f, 0.39f, 1f)),
        (ImGuiCol.TabUnfocused, new Vector4(0.08f, 0.12f, 0.18f, 1f)),
        (ImGuiCol.TabUnfocusedActive, new Vector4(0.13f, 0.23f, 0.30f, 1f)),
        (ImGuiCol.TableRowBg, new Vector4(0.08f, 0.12f, 0.18f, 0.32f)),
        (ImGuiCol.ResizeGrip, new Vector4(0.30f, 0.51f, 0.58f, 0.55f)),
        (ImGuiCol.ResizeGripHovered, Accent),
        (ImGuiCol.ResizeGripActive, Blue),
        (ImGuiCol.TextSelectedBg, new Vector4(0.23f, 0.49f, 0.56f, 0.72f)),
        (ImGuiCol.NavHighlight, Accent),
    ];

    public static void Push()
    {
        foreach (var (color, value) in Colors) ImGui.PushStyleColor(color, value);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(9, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 11f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(12);
        ImGui.PopStyleColor(Colors.Length);
    }
}
