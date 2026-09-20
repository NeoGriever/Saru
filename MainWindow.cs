using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace Saru;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private Guid renamingId;
    private string renameText = "";
    private string objectNameFilter = "";
    private string objectIdFilter = "";
    private IReadOnlyList<RemoteScript>? remoteScripts;
    private readonly Dictionary<string, int> selectedVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool loadingRemoteScripts;

    public MainWindow(Plugin plugin) : base("Saru###Saru", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(760, 560);
        SizeCondition = ImGuiCond.FirstUseEver;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Heart,
            Priority = 90,
            Click = _ => Dalamud.Utility.Util.OpenLink("https://buymeacoffee.com/mindconstructor"),
            ShowTooltip = () =>
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Buy me a coffee");
            }
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.FileCode,
            Priority = 100,
            Click = _ => Dalamud.Utility.Util.OpenLink("https://github.com/NeoGriever/SaruScripts/"),
            ShowTooltip = () =>
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("SaruScripts");
            }
        });
    }

    public void Open() => IsOpen = true;

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("Workspace")) return;
        if (ImGui.BeginTabItem("Scripts")) { DrawScripts(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Load scripts")) { DrawLoadScripts(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Information")) { DrawInfos(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private void DrawScripts()
    {
        ImGui.TextWrapped("New .js files in the folder below are detected automatically.");
        ImGui.TextDisabled(plugin.ScriptsPath);
        ImGui.Separator();
        ImGui.BeginChild("ScriptList", new Vector2(0, 0), true);
        foreach (var script in plugin.Configuration.Scripts)
        {
            ImGui.PushID(script.Id.ToString());
            var running = plugin.IsRunning(script);
            if (renamingId == script.Id)
            {
                ImGui.SetNextItemWidth(250);
                ImGui.InputText("##Rename", ref renameText, 128);
                ImGui.SameLine();
                if (ImGui.SmallButton("Save"))
                {
                    if (!plugin.RenameScript(script, renameText)) plugin.Echo("[Saru] The script name is invalid or already in use.");
                    else renamingId = Guid.Empty;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel")) renamingId = Guid.Empty;
            }
            else
            {
                if (running) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.85f, 0.45f, 1));
                ImGui.TextUnformatted(script.Name);
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { renamingId = script.Id; renameText = script.Name; }
                if (running) ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - (running ? 38 : 40));
                if (running) { if (ImGui.SmallButton("Stop")) plugin.Stop(script); }
                else if (ImGui.SmallButton("Play")) plugin.Run(script);
                ImGui.TextDisabled(plugin.ScriptFullPath(script));
            }
            ImGui.PopID();
        }
        if (plugin.Configuration.Scripts.Count == 0) ImGui.TextDisabled("No .js files found.");
        ImGui.EndChild();
    }

    private void DrawLoadScripts()
    {
        if (remoteScripts == null && !loadingRemoteScripts) RefreshRemoteScripts();
        ImGui.BeginDisabled(loadingRemoteScripts);
        if (ImGui.Button("Refresh")) RefreshRemoteScripts();
        ImGui.EndDisabled();
        if (loadingRemoteScripts) ImGui.SameLine();
        if (loadingRemoteScripts) ImGui.TextDisabled("Loading...");
        if (remoteScripts == null) return;
        ImGui.Separator();
        foreach (var script in remoteScripts) DrawRemoteScript(script);
        if (remoteScripts.Count == 0) ImGui.TextDisabled("No scripts are available.");
    }

    private void RefreshRemoteScripts()
    {
        loadingRemoteScripts = true;
        Task.Run(async () =>
        {
            try
            {
                var catalog = await plugin.ScriptRepository.GetCatalogAsync();
                plugin.QueueMainThread(() => { remoteScripts = catalog; loadingRemoteScripts = false; });
            }
            catch (Exception ex)
            {
                plugin.QueueMainThread(() => { loadingRemoteScripts = false; plugin.Echo($"[Saru] Failed to load scripts: {ex.Message}"); });
            }
        });
    }

    private void DrawRemoteScript(RemoteScript script)
    {
        ImGui.PushID(script.Name);
        var expanded = ImGui.CollapsingHeader(script.Name);
        if (!expanded) ImGui.TextDisabled(Preview(script.Description, 70));
        else
        {
            ImGui.BeginChild("Description", new Vector2(0, 200), true);
            DrawMarkdown(script.Description);
            ImGui.EndChild();
        }
        var selected = selectedVersions.TryGetValue(script.Name, out var index) ? index : 0;
        selected = Math.Clamp(selected, 0, script.Versions.Count - 1);
        selectedVersions[script.Name] = selected;
        var labels = string.Join("\0", script.Versions.Select(version => version.Timestamp.ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture))) + "\0";
        ImGui.SetNextItemWidth(190);
        if (ImGui.Combo("Version", ref selected, labels)) selectedVersions[script.Name] = selected;
        var installed = plugin.InstalledRemoteScriptVersion(script.Name);
        var isInstalled = plugin.IsRemoteScriptInstalled(script.Name);
        var active = isInstalled && plugin.Configuration.Scripts.Find(value => value.FileName.Equals(script.Name + ".js", StringComparison.OrdinalIgnoreCase)) is { } local && plugin.IsRunning(local);
        ImGui.SameLine();
        if (plugin.IsRemoteScriptLoading(script.Name))
        {
            ImGui.BeginDisabled();
            ImGui.Button("Loading...");
            ImGui.EndDisabled();
        }
        else if (active)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Active");
            ImGui.EndDisabled();
        }
        else if (isInstalled)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Installed");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Install")) plugin.InstallRemoteScript(script, script.Versions[selected]);
        if (isInstalled && installed != null && !installed.VersionSha.Equals(script.Versions[selected].Sha, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SameLine();
            if (ImGui.Button("Install selected version")) plugin.InstallRemoteScript(script, script.Versions[selected]);
        }
        if (isInstalled)
        {
            ImGui.SameLine();
            if (ImGui.Button("Uninstall")) plugin.UninstallRemoteScript(script.Name);
        }
        ImGui.Separator();
        ImGui.PopID();
    }

    private static string Preview(string markdown, int maximumLength)
    {
        var text = markdown.Replace("#", "").Replace("*", "").Replace("`", "").Replace("_", "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= maximumLength ? text : text[..Math.Max(0, maximumLength - 3)] + "...";
    }

    private static void DrawMarkdown(string markdown)
    {
        var code = false;
        foreach (var line in markdown.Replace("\r", "").Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (text.StartsWith("### ", StringComparison.Ordinal)) ImGui.TextColored(new Vector4(0.35f, 0.80f, 1, 1), text[4..]);
            else if (text.StartsWith("## ", StringComparison.Ordinal)) ImGui.TextColored(new Vector4(0.35f, 0.80f, 1, 1), text[3..]);
            else if (text.StartsWith("# ", StringComparison.Ordinal)) ImGui.TextColored(new Vector4(0.35f, 0.80f, 1, 1), text[2..]);
            else if (text.StartsWith("- ", StringComparison.Ordinal)) ImGui.BulletText(text[2..]);
            else if (code) ImGui.TextUnformatted(line);
            else if (!string.IsNullOrWhiteSpace(text)) ImGui.TextWrapped(text);
        }
    }

    private void DrawInfos()
    {
        ImGui.TextWrapped("Use this tab to collect and retain positions and object information while developing scripts.");
        ImGui.Spacing();
        var active = plugin.InfoTracker.IsActive;
        if (ImGui.Checkbox("Active", ref active)) plugin.InfoTracker.SetActive(active);
        ImGui.SameLine();
        ImGui.TextDisabled(active ? "Collecting information." : "Collection is paused; saved information is retained.");
        ImGui.SameLine();
        if (ImGui.Button("Export JSON")) plugin.Echo($"[Saru] Information exported: {plugin.ExportInfos()}");
        ImGui.SetNextItemWidth(220); ImGui.InputText("Name", ref objectNameFilter, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180); ImGui.InputText("ID", ref objectIdFilter, 64);
        ImGui.SameLine();
        ImGui.TextDisabled($"{plugin.InfoTracker.Objects.Count} saved");
        var shown = 0;
        ImGui.BeginChild("CollectedObjects", new Vector2(0, 0), true);
        var ownCharacterId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        foreach (var item in plugin.InfoTracker.Objects)
        {
            if (!Matches(item, ownCharacterId)) continue;
            shown++;
            DrawObject(item, item.Key == ownCharacterId);
            ImGui.Separator();
        }
        if (shown == 0) ImGui.TextDisabled("No matching information has been collected.");
        ImGui.EndChild();
    }

    private bool Matches(InfoTracker.InfoObject item, ulong ownCharacterId)
    {
        if (item.Kind.Equals("Pc", StringComparison.OrdinalIgnoreCase) && item.Key != ownCharacterId) return false;
        if (!string.IsNullOrWhiteSpace(objectNameFilter) && !item.Name.Contains(objectNameFilter, StringComparison.OrdinalIgnoreCase)) return false;
        var ids = $"{item.BaseId} {item.EntityId} {item.Key}";
        return string.IsNullOrWhiteSpace(objectIdFilter) || ids.Contains(objectIdFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawObject(InfoTracker.InfoObject item, bool isOwnCharacter)
    {
        var position = string.Create(CultureInfo.InvariantCulture, $"{{ x: {item.Position.X:F4}, y: {item.Position.Y:F4}, z: {item.Position.Z:F4} }}");
        ImGui.PushID(item.Key.ToString(CultureInfo.InvariantCulture));
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(item.Name) ? "Unnamed object" : item.Name);
        ImGui.SameLine();
        if (isOwnCharacter) ImGui.TextDisabled("Player character");
        else if (item.Targeted) ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.45f, 1), "Targeted");
        else if (item.Targetable) ImGui.TextDisabled("Targetable");
        else ImGui.TextDisabled("Not targetable");
        ImGui.TextDisabled($"Type: {item.Kind}");
        ImGui.TextDisabled($"Base ID: {item.BaseId} · Entity ID: {item.EntityId}");
        ImGui.TextUnformatted($"Position: {position}");
        if (ImGui.SmallButton("Copy name")) ImGui.SetClipboardText(item.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy ID")) ImGui.SetClipboardText(item.BaseId.ToString(CultureInfo.InvariantCulture));
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy coordinates")) ImGui.SetClipboardText(position);
        ImGui.PopID();
    }
}
