using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Saru;

public sealed class DependencyWindow : Window
{
    private readonly Plugin plugin;

    public DependencyWindow(Plugin plugin) : base("Saru prerequisites###SaruPrerequisites", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        Size = new Vector2(480, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Open() => IsOpen = true;

    public override void Draw()
    {
        ImGui.TextWrapped("Saru requires the following loaded plugins before it can be opened.");
        ImGui.Spacing();
        foreach (var dependency in plugin.Dependencies)
        {
            var color = dependency.IsAvailable
                ? new Vector4(0.35f, 0.85f, 0.45f, 1)
                : dependency.IsRequired ? new Vector4(0.95f, 0.35f, 0.35f, 1) : new Vector4(0.75f, 0.75f, 0.75f, 1);
            ImGui.TextColored(color, dependency.IsAvailable ? "Available" : dependency.IsRequired ? "Missing" : "Optional");
            ImGui.SameLine();
            ImGui.TextUnformatted(dependency.Name);
        }
        ImGui.Spacing();
        var ready = plugin.RequiredDependenciesAvailable;
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("Open Saru"))
        {
            IsOpen = false;
            plugin.OpenMainWindow();
        }
        ImGui.EndDisabled();
        if (!ready) ImGui.TextDisabled("Load the missing required plugins, then return to this window.");
    }
}
