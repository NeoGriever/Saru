using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    private static readonly Vector4 OwnScriptColor = SaruTheme.Blue;
    private static readonly Vector4 LoadedScriptColor = SaruTheme.Success;
    private static readonly Vector4 UpdateAvailableColor = SaruTheme.Warning;
    private static readonly Vector4 RequirementWarningColor = SaruTheme.Warning;
    private static readonly Vector4 RequirementAvailableColor = SaruTheme.Success;

    public MainWindow(Plugin plugin) : base("Saru###Saru", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        Size = new Vector2(820, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(540, 420), MaximumSize = new Vector2(1600, 1200) };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Coffee,
            IconColor = SaruTheme.Warning,
            Priority = 90,
            Click = _ => Dalamud.Utility.Util.OpenLink("https://buymeacoffee.com/mindconstructor"),
            ShowTooltip = () =>
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Support Saru · Buy me a coffee");
            }
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.CodeBranch,
            IconColor = SaruTheme.Blue,
            Priority = 100,
            Click = _ => Dalamud.Utility.Util.OpenLink("https://github.com/NeoGriever/SaruScripts/"),
            ShowTooltip = () =>
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Open SaruScripts on GitHub");
            }
        });
    }

    public void Open() => IsOpen = true;

    public override void PreDraw() => SaruTheme.Push();

    public override void PostDraw() => SaruTheme.Pop();

    public override void Draw()
    {
        DrawBrand();
        ImGui.Spacing();
        if (!ImGui.BeginTabBar("Workspace")) return;
        if (ImGui.BeginTabItem("Scripts")) { DrawScripts(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Load scripts")) { DrawLoadScripts(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Information")) { DrawInfos(); ImGui.EndTabItem(); }
        DrawRequirementsTab();
        ImGui.EndTabBar();
    }

    private static void DrawBrand()
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var end = start + new Vector2(width, 70);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, end, ImGui.ColorConvertFloat4ToU32(SaruTheme.Raised), 8f);
        drawList.AddRectFilledMultiColor(start + new Vector2(6, 6), end - new Vector2(6, 6),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.19f, 0.24f, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.13f, 0.16f, 0.28f, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.09f, 0.13f, 0.23f, 1f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.16f, 0.21f, 1f)));
        drawList.AddRectFilledMultiColor(start + new Vector2(8, 0), new Vector2(end.X - 8, start.Y + 3),
            ImGui.ColorConvertFloat4ToU32(SaruTheme.Accent), ImGui.ColorConvertFloat4ToU32(SaruTheme.Blue),
            ImGui.ColorConvertFloat4ToU32(SaruTheme.Blue), ImGui.ColorConvertFloat4ToU32(SaruTheme.Accent));
        drawList.AddRect(start, end, ImGui.ColorConvertFloat4ToU32(new Vector4(0.28f, 0.39f, 0.50f, 1f)), 8f);
        ImGui.Dummy(new Vector2(width, 8));
        ImGui.Indent(16);
        ImGui.TextColored(SaruTheme.Text, "SARU  /  SCRIPT WORKSPACE");
        ImGui.TextColored(SaruTheme.Muted, "Automation, world data and plugin integrations");
        ImGui.Unindent(16);
        var used = ImGui.GetCursorScreenPos().Y - start.Y;
        if (used < 70) ImGui.Dummy(new Vector2(0, 70 - used));
    }

    private static void Section(string title, string description)
    {
        ImGui.Spacing();
        ImGui.TextColored(SaruTheme.Accent, title);
        ImGui.TextColored(SaruTheme.Muted, description);
        var position = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilledMultiColor(position, position + new Vector2(110, 2),
            ImGui.ColorConvertFloat4ToU32(SaruTheme.Accent), ImGui.ColorConvertFloat4ToU32(new Vector4(SaruTheme.Blue.X, SaruTheme.Blue.Y, SaruTheme.Blue.Z, 0f)),
            ImGui.ColorConvertFloat4ToU32(new Vector4(SaruTheme.Blue.X, SaruTheme.Blue.Y, SaruTheme.Blue.Z, 0f)), ImGui.ColorConvertFloat4ToU32(SaruTheme.Accent));
        ImGui.Dummy(new Vector2(0, 5));
    }

    private static bool ActionButton(string label, bool destructive = false, float width = 0)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, destructive ? new Vector4(0.37f, 0.16f, 0.22f, 1f) : new Vector4(0.10f, 0.34f, 0.36f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, destructive ? new Vector4(0.54f, 0.22f, 0.28f, 1f) : new Vector4(0.14f, 0.46f, 0.47f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, destructive ? new Vector4(0.65f, 0.28f, 0.34f, 1f) : new Vector4(0.17f, 0.54f, 0.54f, 1f));
        var pressed = ImGui.Button(label, new Vector2(width, 0));
        ImGui.PopStyleColor(3);
        return pressed;
    }

    private void DrawRequirementsTab()
    {
        var requirementsAvailable = plugin.RequiredDependenciesAvailable;
        if (!requirementsAvailable) ImGui.PushStyleColor(ImGuiCol.Text, RequirementWarningColor);
        var tabOpen = ImGui.BeginTabItem("Requirements");
        if (!requirementsAvailable) ImGui.PopStyleColor();

        if (!tabOpen) return;

        Section("INTEGRATIONS", "Plugin availability and optional features");
        ImGui.TextWrapped("Missing integrations only affect features that use them.");
        ImGui.Spacing();
        foreach (var dependency in plugin.Dependencies)
        {
            var color = dependency.IsAvailable
                ? RequirementAvailableColor
                : dependency.IsRequired ? RequirementWarningColor : SaruTheme.Muted;
            if (ImGui.BeginTable($"##dependency_{dependency.Name}", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 98);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(dependency.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(color, dependency.IsAvailable ? "AVAILABLE" : dependency.IsRequired ? "MISSING" : "OPTIONAL");
                ImGui.EndTable();
            }
            ImGui.Separator();
        }
        ImGui.EndTabItem();
    }

    private void DrawScripts()
    {
        Section("YOUR SCRIPTS", "Local and installed JavaScript automations");
        ImGui.TextColored(SaruTheme.Muted, "New .js files in this folder are detected automatically.");
        ImGui.TextWrapped(plugin.ScriptsPath);
        ImGui.Spacing();
        ImGui.BeginChild("ScriptList", new Vector2(0, 0), true);
        foreach (var script in plugin.Configuration.Scripts)
        {
            ImGui.PushID(script.Id.ToString());
            var running = plugin.IsRunning(script);
            var hasUpdate = plugin.HasRemoteUpdate(script);
            var scriptColor = hasUpdate ? UpdateAvailableColor : plugin.IsRemotelyManagedScript(script) ? LoadedScriptColor : OwnScriptColor;
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
                if (ImGui.BeginTable("##script_header", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 72);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextColored(scriptColor, script.Name);
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { renamingId = script.Id; renameText = script.Name; }
                    if (hasUpdate) { ImGui.SameLine(); DrawUpdateIcon(); }
                    ImGui.TableSetColumnIndex(1);
                    if (running) { if (ActionButton("Stop", destructive: true, width: -1)) plugin.Stop(script); }
                    else if (ActionButton("Play", width: -1)) plugin.Run(script);
                    ImGui.EndTable();
                }
                ImGui.TextColored(SaruTheme.Muted, Path.GetFileName(plugin.ScriptFullPath(script)));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(plugin.ScriptFullPath(script));
            }
            DrawScriptConfiguration(script);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PopID();
        }
        if (plugin.Configuration.Scripts.Count == 0) ImGui.TextDisabled("No .js files found.");
        ImGui.EndChild();
    }

    private void DrawScriptConfiguration(ScriptEntry script)
    {
        if (!string.IsNullOrWhiteSpace(script.ConfigError))
        {
            ImGui.TextColored(SaruTheme.Danger, "Configuration could not be read");
            ImGui.TextWrapped(script.ConfigError);
            return;
        }
        if (script.Config.Count == 0) return;
        if (!ImGui.CollapsingHeader($"Configuration ({script.Config.Count})")) return;
        var changed = false;
        foreach (var entry in script.Config)
        {
            ImGui.PushID(entry.Key);
            switch (entry.Type)
            {
                case ScriptConfigType.Checkbox:
                    var boolean = entry.BoolValue;
                    if (ImGui.Checkbox("##value", ref boolean)) { entry.BoolValue = boolean; changed = true; }
                    break;
                case ScriptConfigType.Combo:
                    if (entry.Options.Count == 0)
                    {
                        ImGui.TextDisabled("No options declared");
                        break;
                    }
                    var selection = Math.Clamp(entry.ComboValue, 0, entry.Options.Count - 1);
                    var options = string.Join("\0", entry.Options) + "\0";
                    ImGui.SetNextItemWidth(220);
                    if (ImGui.Combo("##value", ref selection, options)) { entry.ComboValue = selection; changed = true; }
                    break;
                case ScriptConfigType.Input:
                    var text = entry.TextValue;
                    ImGui.SetNextItemWidth(220);
                    if (ImGui.InputText("##value", ref text, 512)) { entry.TextValue = text; changed = true; }
                    break;
                case ScriptConfigType.Number:
                    var minimum = (int)MathF.Ceiling(entry.NumberMinimum);
                    var maximum = (int)MathF.Floor(entry.NumberMaximum);
                    if (maximum < minimum) maximum = minimum;
                    var number = Math.Clamp((int)MathF.Round(entry.NumberValue), minimum, maximum);
                    ImGui.SetNextItemWidth(160);
                    if (StepInput.InputInt("##value", ref number, 1))
                    {
                        entry.NumberValue = Math.Clamp(number, minimum, maximum);
                        changed = true;
                    }
                    break;
            }
            ImGui.SameLine(0, 12);
            ImGui.AlignTextToFramePadding();
            ImGui.TextWrapped(entry.Label);
            ImGui.PopID();
        }
        if (changed) plugin.SaveConfiguration();
    }

    private void DrawLoadScripts()
    {
        Section("SCRIPT CATALOG", "Browse and install shared automations");
        if (remoteScripts == null && !loadingRemoteScripts) RefreshRemoteScripts();
        ImGui.BeginDisabled(loadingRemoteScripts);
        if (ImGui.Button("Refresh")) RefreshRemoteScripts();
        ImGui.EndDisabled();
        if (loadingRemoteScripts) ImGui.SameLine();
        if (loadingRemoteScripts) ImGui.TextDisabled("Loading...");
        if (remoteScripts == null) return;
        ImGui.Spacing();
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
                plugin.QueueMainThread(() => { plugin.UpdateRemoteCatalog(catalog); remoteScripts = catalog; loadingRemoteScripts = false; });
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
        var installed = plugin.InstalledRemoteScriptVersion(script.Name);
        var hasUpdate = plugin.HasRemoteUpdate(script.Name);
        var scriptColor = hasUpdate ? UpdateAvailableColor : installed != null ? LoadedScriptColor : OwnScriptColor;
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, scriptColor);
        var expanded = ImGui.CollapsingHeader(script.Name);
        ImGui.PopStyleColor();
        if (hasUpdate) { ImGui.SameLine(); DrawUpdateIcon(); }
        if (!expanded) ImGui.TextDisabled(Preview(script.Description, 70));
        else
        {
            ImGui.BeginChild("Description", new Vector2(0, 180), true);
            DrawMarkdown(script.Description);
            ImGui.EndChild();
        }
        if (script.Versions.Count == 0)
        {
            ImGui.TextColored(SaruTheme.Warning, "No versions are available for this script.");
            ImGui.Separator();
            ImGui.PopID();
            return;
        }
        var selected = selectedVersions.TryGetValue(script.Name, out var index) ? index : 0;
        selected = Math.Clamp(selected, 0, script.Versions.Count - 1);
        selectedVersions[script.Name] = selected;
        var labels = string.Join("\0", script.Versions.Select(version => version.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))) + "\0";
        ImGui.SetNextItemWidth(190);
        if (ImGui.Combo("Version", ref selected, labels)) selectedVersions[script.Name] = selected;
        var isInstalled = plugin.IsRemoteScriptInstalled(script.Name);
        var active = isInstalled && installed != null && plugin.Configuration.Scripts.Find(value => value.FileName.Equals(installed.FileName, StringComparison.OrdinalIgnoreCase)) is { } local && plugin.IsRunning(local);
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
        else if (ActionButton("Install")) plugin.InstallRemoteScript(script, script.Versions[selected]);
        if (isInstalled && installed != null && !installed.VersionSha.Equals(script.Versions[selected].Sha, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SameLine();
            if (ActionButton("Install selected version")) plugin.InstallRemoteScript(script, script.Versions[selected]);
        }
        if (isInstalled)
        {
            ImGui.SameLine();
            if (ActionButton("Uninstall", destructive: true)) plugin.UninstallRemoteScript(script.Name);
        }
        ImGui.Separator();
        ImGui.PopID();
    }

    private static void DrawUpdateIcon()
    {
        ImGui.TextColored(UpdateAvailableColor, "UPDATE");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Update available");
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
            if (text.StartsWith("### ", StringComparison.Ordinal)) ImGui.TextColored(SaruTheme.Blue, text[4..]);
            else if (text.StartsWith("## ", StringComparison.Ordinal)) ImGui.TextColored(SaruTheme.Accent, text[3..]);
            else if (text.StartsWith("# ", StringComparison.Ordinal)) ImGui.TextColored(SaruTheme.Accent, text[2..]);
            else if (text.StartsWith("- ", StringComparison.Ordinal)) { ImGui.TextColored(SaruTheme.Accent, "•"); ImGui.SameLine(); ImGui.TextWrapped(text[2..]); }
            else if (code) ImGui.TextColored(SaruTheme.Blue, line);
            else if (!string.IsNullOrWhiteSpace(text)) ImGui.TextWrapped(text);
        }
    }

    private void DrawInfos()
    {
        Section("WORLD INFORMATION", "Collect positions and object details while developing scripts");
        var active = plugin.InfoTracker.IsActive;
        if (ImGui.Checkbox("Collect information", ref active)) plugin.InfoTracker.SetActive(active);
        ImGui.SameLine();
        ImGui.TextColored(active ? SaruTheme.Success : SaruTheme.Muted, active ? "LIVE" : "PAUSED");
        if (!active) ImGui.TextColored(SaruTheme.Muted, "Collected information remains available while paused.");
        if (ActionButton("Export JSON")) plugin.Echo($"[Saru] Information exported: {plugin.ExportInfos()}");
        ImGui.SameLine();
        ImGui.TextColored(SaruTheme.Muted, $"{plugin.InfoTracker.Objects.Count} SAVED");
        ImGui.Spacing();
        if (ImGui.BeginTable("##info_filters", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(SaruTheme.Muted, "FILTER BY NAME");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##Name", ref objectNameFilter, 128);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextColored(SaruTheme.Muted, "FILTER BY ID");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ID", ref objectIdFilter, 64);
            ImGui.EndTable();
        }
        ImGui.Spacing();
        var shown = 0;
        ImGui.BeginChild("CollectedObjects", new Vector2(0, 0), true);
        var ownCharacterId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        foreach (var (index, item) in VisibleInfoObjects(ownCharacterId))
        {
            shown++;
            DrawObject(item, index, item.Key == ownCharacterId);
            ImGui.Separator();
        }
        if (shown == 0) ImGui.TextDisabled("No matching information has been collected.");
        ImGui.EndChild();
    }

    internal void DrawInfoMarkers()
    {
        if (!plugin.InfoTracker.IsActive) return;
        var liveKeys = new HashSet<ulong>();
        foreach (var obj in Plugin.ObjectTable)
            liveKeys.Add(obj.GameObjectId != 0 ? obj.GameObjectId : unchecked((ulong)obj.Address));

        var ownCharacterId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        var mapId = plugin.CurrentMapId;
        foreach (var (index, item) in VisibleInfoObjects(ownCharacterId))
        {
            if (item.MapId != mapId || !liveKeys.Contains(item.Key)) continue;
            var green = item.Targetable ? (byte)150 : (byte)255;
            var blue = item.Targetable ? (byte)0 : (byte)255;
            WorldMarkerRenderer.Draw(new WorldMarker(item.Position, 0.6f, 255, green, blue, Plugin.ClientState.TerritoryType, Plugin.ClientState.Instance), true, index.ToString(CultureInfo.InvariantCulture));
        }
    }

    private IEnumerable<(int Index, InfoTracker.InfoObject Item)> VisibleInfoObjects(ulong ownCharacterId)
    {
        var index = 0;
        foreach (var item in plugin.InfoTracker.Objects)
            if (Matches(item, ownCharacterId)) yield return (index++, item);
    }

    private bool Matches(InfoTracker.InfoObject item, ulong ownCharacterId)
    {
        if (item.Kind.Equals("Pc", StringComparison.OrdinalIgnoreCase) && item.Key != ownCharacterId) return false;
        if (!string.IsNullOrWhiteSpace(objectNameFilter) && !item.Name.Contains(objectNameFilter, StringComparison.OrdinalIgnoreCase)) return false;
        var ids = $"{item.BaseId} {item.EntityId} {item.Key}";
        return string.IsNullOrWhiteSpace(objectIdFilter) || ids.Contains(objectIdFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawObject(InfoTracker.InfoObject item, int index, bool isOwnCharacter)
    {
        var position = string.Create(CultureInfo.InvariantCulture, $"{{ x: {item.Position.X:F4}, y: {item.Position.Y:F4}, z: {item.Position.Z:F4}, mapId: {item.MapId} }}");
        ImGui.PushID(item.Key.ToString(CultureInfo.InvariantCulture));
        ImGui.TextColored(SaruTheme.Accent, $"{index:D2}");
        ImGui.SameLine();
        ImGui.TextColored(SaruTheme.Text, string.IsNullOrWhiteSpace(item.Name) ? "Unnamed object" : item.Name);
        var status = isOwnCharacter ? "PLAYER" : item.Targeted ? "TARGETED" : item.Targetable ? "TARGETABLE" : "NOT TARGETABLE";
        var statusColor = isOwnCharacter ? SaruTheme.Blue : item.Targeted ? SaruTheme.Success : item.Targetable ? SaruTheme.Warning : SaruTheme.Muted;
        ImGui.TextColored(statusColor, status);
        ImGui.TextColored(SaruTheme.Muted, $"{item.Kind}   ·   Base {item.BaseId}   ·   Entity {item.EntityId}");
        ImGui.TextWrapped($"Position  {position}");
        ImGui.TextColored(SaruTheme.Muted, $"Rotation  {item.Rotation.ToString("F4", CultureInfo.InvariantCulture)} rad    ·    Hitbox  {item.HitboxRadius.ToString("F2", CultureInfo.InvariantCulture)}");
        if (item.ModelPaths.Count > 0) ImGui.TextWrapped($"Models  {string.Join(", ", item.ModelPaths.Select(model => model.Path))}");
        ImGui.Spacing();
        if (ImGui.SmallButton("Copy name")) ImGui.SetClipboardText(item.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy ID")) ImGui.SetClipboardText(item.BaseId.ToString(CultureInfo.InvariantCulture));
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy coordinates")) ImGui.SetClipboardText(position);
        if (item.Kind.Equals("BattleNpc", StringComparison.Ordinal))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Export mdl"))
            {
                var count = plugin.ExportModels(item);
                plugin.Echo(count > 0 ? $"[Saru] Exported {count} model file(s) to {plugin.ModelExportDirectory(item)}" : $"[Saru] No extractable model found for {item.Name}.");
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Export available BattleNpc model files to Saru's Models folder.");
        }
        ImGui.PopID();
    }
}
