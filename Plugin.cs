using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;
using ClientUtf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;
using ClientUIModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule;
namespace Saru;
public sealed class Plugin : IDalamudPlugin
{
    internal static Plugin Instance { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    private readonly WindowSystem windows = new("Saru");
    private readonly MainWindow mainWindow;
    private readonly DependencyWindow dependencyWindow;
    private readonly Dictionary<Guid, ScriptRuntime> runtimes = new();
    private readonly CardSourceNpcsApi cardSourceNpcs;
    private readonly ScriptRepository scriptRepository = new();
    private readonly string scriptsPath;
    private readonly HashSet<string> loadingRemoteScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RemoteScriptVersion> latestRemoteVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> echoes = new();
    private readonly ConcurrentQueue<string> outgoingMessages = new();
    private readonly ConcurrentQueue<ChatMessageRecord> incomingMessages = new();
    private readonly ConcurrentQueue<ActivationRequest> activationRequests = new();
    private readonly ConcurrentQueue<Action> mainThreadActions = new();
    private readonly List<PendingActivation> pendingActivations = new();
    private MoveRequest? pendingMove;
    private DateTime nextMoveAttempt;
    private DateTime lastScriptFileCheck = DateTime.MinValue;
    private DateTime lastRemoteCatalogCheck = DateTime.MinValue;
    private bool checkingRemoteCatalog;
    private GameStateSnapshot? previousGameState;
    public Configuration Configuration { get; }
    public InfoTracker InfoTracker { get; } = new();
    public string ScriptsPath => scriptsPath;
    public ScriptRepository ScriptRepository => scriptRepository;
    public uint CurrentMapId => ResolveCurrentMapId();
    public IReadOnlyList<PluginDependencyStatus> Dependencies => GetDependencies();
    public bool RequiredDependenciesAvailable => Dependencies.Where(value => value.IsRequired).All(value => value.IsAvailable);
    public Plugin()
    {
        Instance = this;
        Configuration = Configuration.Load();
        scriptsPath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "Scripts");
        InitializeScriptFiles();
        cardSourceNpcs = new CardSourceNpcsApi(DataManager, Write);
        mainWindow = new MainWindow(this);
        dependencyWindow = new DependencyWindow(this);
        windows.AddWindow(mainWindow);
        windows.AddWindow(dependencyWindow);
        RestoreMissingRemoteScripts();
        CommandManager.AddHandler("/saru", new CommandInfo(OnCommand) { HelpMessage = "/saru opens Saru. /saru start <scriptname> starts a script. /saru stop stops all scripts." });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainOrDependencies;
        PluginInterface.UiBuilder.OpenConfigUi += dependencyWindow.Open;
        Framework.Update += OnUpdate;
        ChatGui.ChatMessage += OnChatMessage;
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnDialogState);
        Write(LogLevel.Verbose, "Saru loaded.");
        if (!RequiredDependenciesAvailable) dependencyWindow.Open();
    }
    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            if (!RequiredDependenciesAvailable) { dependencyWindow.Open(); return; }
            var script = Configuration.Scripts.Find(value => value.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (script == null) Echo($"[Saru] Script not found: {parts[1]}");
            else Run(script);
            return;
        }
        if (parts.Length == 1 && parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) { StopAll(); return; }
        OpenMainOrDependencies();
    }
    public void OpenMainWindow() => mainWindow.Open();
    private void OpenMainOrDependencies()
    {
        if (RequiredDependenciesAvailable) mainWindow.Open();
        else dependencyWindow.Open();
    }
    private static IReadOnlyList<PluginDependencyStatus> GetDependencies()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(value => value.GetName().Name ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            new("BossMod or BossModReborn", true, assemblies.Contains("BossMod") || assemblies.Contains("BossModReborn")),
            new("vNavMesh", true, assemblies.Contains("vnavmesh")),
            new("TextAdvance", true, assemblies.Contains("TextAdvance")),
            new("Saucy", true, assemblies.Contains("Saucy")),
            new("Cammy", false, assemblies.Contains("Cammy")),
            new("Chocoholic", false, assemblies.Contains("Chocoholic")),
        ];
    }
    private unsafe void OnDialogState(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostSetup && args.AddonName.Equals("RideShootingResult", StringComparison.OrdinalIgnoreCase))
            foreach (var runtime in runtimes.Values) runtime.NotifyEvent("eventDone");
    }
    private void OnUpdate(IFramework framework)
    {
        while (mainThreadActions.TryDequeue(out var action))
            try { action(); }
            catch (Exception ex) { Write(LogLevel.Error, $"Main-thread action failed: {ex.Message}"); }
        SaucyRuntime.RefreshMachineStates();
        var gameState = new GameStateSnapshot(
            Condition[ConditionFlag.BetweenAreas],
            Condition[ConditionFlag.WaitingForDuty],
            Condition[ConditionFlag.BoundByDuty],
            Condition[ConditionFlag.OccupiedInQuestEvent],
            Condition[ConditionFlag.InDutyQueue],
            Condition[ConditionFlag.InCombat],
            Condition[ConditionFlag.Mounted],
            Condition[ConditionFlag.Jumping],
            CurrentMapId,
            ClientState.TerritoryType,
            ClientState.Instance,
            ClientState.IsLoggedIn,
            ClientState.IsPvP,
            IsAddonVisible("SelectYesno"),
            IsAddonVisible("RideShootingResult"));
        foreach (var runtime in runtimes.Values) runtime.UpdatePosition(ObjectTable.LocalPlayer?.Position ?? Vector3.Zero, gameState.MapId);
        foreach (var runtime in runtimes.Values) runtime.UpdateGameState(gameState);
        if (previousGameState is { } previous)
        {
            if (previous.MapId != gameState.MapId) foreach (var runtime in runtimes.Values) runtime.NotifyEvent("mapChange");
            if (!previous.InZoneChange && gameState.InZoneChange) foreach (var runtime in runtimes.Values) runtime.NotifyEvent("zoneChangeStart");
            if (previous.InZoneChange && !gameState.InZoneChange) foreach (var runtime in runtimes.Values) runtime.NotifyEvent("zoneChanged");
            if (previous.BoundByDuty && !gameState.BoundByDuty) foreach (var runtime in runtimes.Values) runtime.NotifyEvent("dutyEnd");
        }
        previousGameState = gameState;
        foreach (var runtime in runtimes.Values) runtime.UpdateObjectPositions();
        InfoTracker.Update(gameState.MapId);
        ProcessActivations();
        while (outgoingMessages.TryDequeue(out var outgoing)) ProcessChatMessage(outgoing);
        while (echoes.TryDequeue(out var message))
            try { ChatGui.Print(new XivChatEntry { Type = XivChatType.Echo, Message = new SeStringBuilder().AddText(message).Build() }); }
            catch (Exception ex) { Write(LogLevel.Error, $"Echo failed: {ex.Message}"); }
        if (pendingMove.HasValue && DateTime.UtcNow >= nextMoveAttempt) TryMove(pendingMove.Value, true);
        foreach (var runtime in runtimes.Values) runtime.Update();
        ReloadChangedScriptsIfDue();
        CheckRemoteScriptUpdatesIfDue();
        while (incomingMessages.TryDequeue(out var incoming)) foreach (var runtime in runtimes.Values) runtime.ReceiveMessage(incoming.Message);
    }
    private uint ResolveCurrentMapId()
    {
        var reportedMapId = ClientState.MapId;
        if (reportedMapId != 0) return reportedMapId;

        var territoryId = ClientState.TerritoryType;
        if (territoryId == 0) return 0;
        var territories = DataManager.GetExcelSheet<TerritoryTypeSheet>();
        return territories == null ? 0 : territories.GetRow(territoryId).Map.RowId;
    }
    public string PositionText()
    {
        var p = ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"X: {p.X:F4}, Y: {p.Y:F4}, Z: {p.Z:F4}, MapId: {CurrentMapId}");
    }
    public string DescribeTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject? target)
    {
        if (target == null) return "None";
        var p = target.Position;
        return string.Create(CultureInfo.InvariantCulture, $"Name=\"{target.Name}\" | GameObjectId={target.GameObjectId} | EntityId={target.EntityId} | BaseId={target.BaseId} | Kind={target.ObjectKind} | Targetable={target.IsTargetable} | Position=({p.X:F4}, {p.Y:F4}, {p.Z:F4}, MapId={CurrentMapId})");
    }
    public string CurrentTargetId() => TargetManager.Target?.BaseId.ToString(CultureInfo.InvariantCulture) ?? "null";
    public void Echo(string message) => echoes.Enqueue(message.Replace('\r', ' ').Replace('\n', ' '));
    public void QueueMainThread(Action action) => mainThreadActions.Enqueue(action);
    public void SendMessage(string message) => outgoingMessages.Enqueue(message);
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        incomingMessages.Enqueue(new ChatMessageRecord(text));
    }
    private unsafe void ProcessChatMessage(string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length is < 1 or > 500) throw new ArgumentException("Message must contain 1 to 500 UTF-8 bytes.");
            var text = ClientUtf8String.FromSequence(bytes);
            ClientUIModule.Instance()->ProcessChatBoxEntry(text);
            text->Dtor(true);
        }
        catch (Exception ex) { Write(LogLevel.Error, $"Chat send failed: {ex.Message}"); }
    }
    public bool SelectTarget(uint id)
    {
        var target = FindTarget(id);
        if (target == null) return false;
        TargetManager.Target = target;
        return true;
    }
    public bool SelectTargetByKey(ulong key)
    {
        var target = FindTargetByKey(key);
        if (target == null) return false;
        TargetManager.Target = target;
        return true;
    }
    public bool ActivateTarget(uint? id = null)
    {
        activationRequests.Enqueue(new ActivationRequest(id, null));
        return true;
    }
    public bool ActivateTargetByKey(ulong key)
    {
        var target = FindTargetByKey(key);
        activationRequests.Enqueue(new ActivationRequest(null, key));
        return target != null;
    }
    private void ProcessActivations()
    {
        if (pendingActivations.Count > 0)
        {
            var pending = pendingActivations[0];
            if (pending.Due <= DateTime.UtcNow) { pendingActivations.Clear(); ActivateCurrentTarget(pending.Key); }
            return;
        }
        if (!activationRequests.TryDequeue(out var request)) return;
        var target = request.Key.HasValue ? FindTargetByKey(request.Key.Value) : request.Id.HasValue ? FindTarget(request.Id.Value) : TargetManager.Target;
        if (target == null || !target.IsTargetable) { Write(LogLevel.Error, "Target activation failed: target is unavailable."); return; }
        DisableCammyNoClippy();
        TargetManager.Target = target;
        pendingActivations.Add(new PendingActivation(ObjectKey(target), DateTime.UtcNow.AddMilliseconds(300)));
    }
    private unsafe void ActivateCurrentTarget(ulong key)
    {
        var target = FindTargetByKey(key);
        if (target == null || !target.IsTargetable) { Write(LogLevel.Error, "Target activation failed: target changed or is unavailable."); return; }
        try
        {
            TargetManager.Target = target;
            var system = TargetSystem.Instance();
            var result = system == null ? 0 : system->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address);
            if (result == 0) Write(LogLevel.Error, "Target activation failed: interaction was rejected.");
        }
        catch (Exception ex) { Write(LogLevel.Error, $"Target activation failed: {ex.Message}"); }
    }
    private bool IsAddonVisible(string name)
    {
        var addon = GameGui.GetAddonByName(name, 1);
        return addon.Address != nint.Zero && addon.IsVisible;
    }
    private void DisableCammyNoClippy()
    {
        TrySetCammyCameraClipping(true, out _);
    }
    public bool TrySetCammyCameraClipping(bool clippingEnabled, out string status)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => value.GetName().Name == "Cammy");
            if (assembly == null) { status = "Cammy is not loaded."; return false; }
            var game = assembly?.GetType("Cammy.Game", throwOnError: false);
            var patch = game?.GetField("cameraNoClippyReplacer", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var method = patch?.GetType().GetMethod(clippingEnabled ? "Disable" : "Enable", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) { status = "Cammy camera clipping is not controllable."; return false; }
            method.Invoke(patch, null);
            var enabled = patch!.GetType().GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance)?.GetValue(patch) as bool?;
            if (enabled != !clippingEnabled) { status = "Cammy camera clipping state could not be confirmed."; return false; }
            status = clippingEnabled ? "Cammy camera clipping enabled." : "Cammy camera clipping disabled.";
            return true;
        }
        catch (Exception ex) { status = $"Cammy camera clipping failed: {ex.Message}"; return false; }
    }
    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindTarget(uint id)
    {
        foreach (var obj in ObjectTable) if (obj.BaseId == id && obj.IsTargetable) return obj;
        return null;
    }
    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindTargetByKey(ulong key)
    {
        foreach (var obj in ObjectTable)
            if ((obj.GameObjectId != 0 ? obj.GameObjectId : unchecked((ulong)obj.Address)) == key && obj.IsTargetable) return obj;
        return null;
    }
    private static ulong ObjectKey(Dalamud.Game.ClientState.Objects.Types.IGameObject obj) => obj.GameObjectId != 0 ? obj.GameObjectId : unchecked((ulong)obj.Address);
    public unsafe bool SelectDialog(bool yes)
    {
        var addon = GameGui.GetAddonByName("SelectYesno", 1);
        if (addon.Address == nint.Zero || !addon.IsVisible) return false;
        try { ((AtkUnitBase*)addon.Address)->FireCallbackInt(yes ? 0 : 1); return true; }
        catch (Exception ex) { Write(LogLevel.Error, $"Dialog selection failed: {ex.Message}"); return false; }
    }
    public bool QueueSelectDialog(bool yes) { QueueMainThread(() => SelectDialog(yes)); return true; }
    public bool CloseRideShootingResult()
    {
        QueueMainThread(() => CloseRideShootingResultOnMainThread());
        return true;
    }
    private unsafe void CloseRideShootingResultOnMainThread()
    {
        var addon = GameGui.GetAddonByName("RideShootingResult", 1);
        if (addon.Address == nint.Zero || !addon.IsVisible) return;
        try { ((AtkUnitBase*)addon.Address)->FireCallbackInt(0); }
        catch (Exception ex) { Write(LogLevel.Error, $"Could not close RideShootingResult: {ex.Message}"); }
    }
    public bool MoveTo(float x, float y, float z, float buffer, uint? mapId)
    {
        if (mapId.HasValue && CurrentMapId != mapId.Value)
        {
            Write(LogLevel.Verbose, $"MoveTo ignored: target MapId {mapId.Value} does not match current MapId {CurrentMapId}.");
            return false;
        }
        var request = new MoveRequest(new Vector3(x, y, z), Math.Max(0, buffer), mapId);
        foreach (var runtime in runtimes.Values) runtime.TrackMove(request.Target, request.Buffer, request.MapId);
        pendingMove = request;
        nextMoveAttempt = DateTime.UtcNow;
        return true;
    }
    public bool IsNavmeshPathRunning()
    {
        try { return PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc(); }
        catch { return false; }
    }
    public bool ToggleChocoholic(bool enabled)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => (value.GetName().Name ?? "").Equals("Chocoholic", StringComparison.OrdinalIgnoreCase));
        var service = assembly?.GetType("Chocoholic.Services.Service", throwOnError: false);
        var dutyRestart = service?.GetField("DutyRestart", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var enabledField = dutyRestart?.GetType().GetField("Enabled", BindingFlags.Public | BindingFlags.Instance);
        if (enabledField?.FieldType != typeof(bool))
            return false;
        QueueMainThread(() =>
        {
            try
            {
                if (enabled) ResetChocoholicRegistrationDelay();
                enabledField.SetValue(dutyRestart, enabled);
            }
            catch { }
            finally
            {
                if (!enabled) ReleaseChocoholicForwardInput();
            }
        });
        return true;
    }
    private static void ReleaseChocoholicForwardInput()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => (value.GetName().Name ?? "").Equals("Chocoholic", StringComparison.OrdinalIgnoreCase));
            var commandType = assembly?.GetType("Chocoholic.Data.ChocoCommand", throwOnError: false);
            if (commandType != null)
            {
                var release = assembly?.GetType("ChocoboRacer.Utility.Utils", throwOnError: false)?.GetMethod("SetKeyState", BindingFlags.NonPublic | BindingFlags.Static, binder: null, types: [commandType, typeof(int)], modifiers: null);
                if (release != null)
                {
                    release.Invoke(null, [Enum.Parse(commandType, "Forward"), 0]);
                    return;
                }
            }

            var ecommons = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => (value.GetName().Name ?? "").Equals("ECommons", StringComparison.OrdinalIgnoreCase));
            var fallback = ecommons?.GetType("ECommons.Reflection.DalamudReflector", throwOnError: false)?.GetMethod("SetKeyState", BindingFlags.Public | BindingFlags.Static, binder: null, types: [typeof(VirtualKey), typeof(int)], modifiers: null);
            fallback?.Invoke(null, [VirtualKey.W, 0]);
        }
        catch { }
    }
    public bool SetChocoholicNumberOfRaces(int numberOfRaces)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => (value.GetName().Name ?? "").Equals("Chocoholic", StringComparison.OrdinalIgnoreCase));
        var config = assembly?.GetType("Chocoholic.ChocoboRacer", throwOnError: false)?.GetField("C", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var numberField = config?.GetType().GetField("NumRaces", BindingFlags.Public | BindingFlags.Instance);
        if (numberField?.FieldType != typeof(int))
            return false;

        var clampedValue = Math.Clamp(numberOfRaces, 0, 999);
        QueueMainThread(() =>
        {
            try { numberField.SetValue(config, clampedValue); }
            catch { }
        });
        return true;
    }
    private static void ResetChocoholicRegistrationDelay()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => (value.GetName().Name ?? "").Equals("ECommons", StringComparison.OrdinalIgnoreCase));
        var reset = assembly?.GetType("ECommons.Throttlers.EzThrottler", throwOnError: false)?.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static, binder: null, types: [typeof(string)], modifiers: null);
        reset?.Invoke(null, ["RegDelay"]);
    }
    private bool TryMove(MoveRequest request, bool retry)
    {
        if (request.MapId.HasValue && CurrentMapId != request.MapId.Value)
        {
            pendingMove = null;
            foreach (var runtime in runtimes.Values) runtime.CancelMove();
            Write(LogLevel.Verbose, $"MoveTo cancelled: target MapId {request.MapId.Value} no longer matches current MapId {CurrentMapId}.");
            return false;
        }
        try
        {
            if (IsNavmeshPathRunning())
            {
                pendingMove = null;
                return true;
            }
            nextMoveAttempt = DateTime.UtcNow.AddSeconds(5);
            if (!PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc(request.Target, false)) return QueueMove(request, retry);
            StartMove(request);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("was not registered yet", StringComparison.OrdinalIgnoreCase))
        {
            return QueueMove(request, retry);
        }
        catch (Exception ex) { pendingMove = null; Write(LogLevel.Error, $"vNavMesh MoveTo failed: {ex.GetBaseException().Message}"); return false; }
    }
    private bool QueueMove(MoveRequest request, bool retry)
    {
        nextMoveAttempt = DateTime.UtcNow.AddMilliseconds(500);
        if (!retry) { pendingMove = request; Write(LogLevel.Verbose, "vNavMesh is not ready; MoveTo was queued."); }
        return true;
    }
    private void StartMove(MoveRequest request) { pendingMove = null; }
    private void InitializeScriptFiles()
    {
        Directory.CreateDirectory(scriptsPath);
        var changed = Configuration.Version != 4;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in Configuration.Scripts.ToArray())
        {
            if (script.Name.Equals("Script 1", StringComparison.OrdinalIgnoreCase)) { script.Name = "GSR"; changed = true; }
            var expectedName = ScriptFileName(script.Name);
            if (script.Id == Guid.Empty || !IsSafeScriptFileName(script.FileName) || !usedNames.Add(expectedName))
            {
                script.Id = script.Id == Guid.Empty ? Guid.NewGuid() : script.Id;
                script.Name = UniqueScriptName(script.Name, script.Id);
                expectedName = ScriptFileName(script.Name);
                usedNames.Add(expectedName);
                changed = true;
            }
            if (!script.FileName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = ScriptPath(script);
                script.FileName = expectedName;
                var newPath = ScriptPath(script);
                if (File.Exists(oldPath) && !File.Exists(newPath)) File.Move(oldPath, newPath);
                changed = true;
            }

            var path = ScriptPath(script);
            if (!File.Exists(path))
            {
                continue;
            }

            var content = ReadScriptFile(path);
            script.Source = content.Source;
            script.ContentHash = content.Hash;
            script.Revision++;
            ScriptConfiguration.Refresh(script);
            changed = true;
        }

        changed |= RemoveMissingLocalScripts();
        SynchronizeScriptDirectory();
        Configuration.Version = 4;
        if (changed) Configuration.Save();
    }

    private void ReloadChangedScriptsIfDue()
    {
        if (DateTime.UtcNow - lastScriptFileCheck < TimeSpan.FromSeconds(2)) return;
        lastScriptFileCheck = DateTime.UtcNow;
        SynchronizeScriptDirectory();
        var changed = RemoveMissingLocalScripts();
        RestoreMissingRemoteScripts();
        foreach (var script in Configuration.Scripts)
        {
            var path = ScriptPath(script);
            if (!File.Exists(path))
            {
                continue;
            }
            script.MissingFileReported = false;

            try
            {
                var content = ReadScriptFile(path);
                if (string.Equals(content.Hash, script.ContentHash, StringComparison.Ordinal)) continue;
                script.Source = content.Source;
                script.ContentHash = content.Hash;
                script.MissingFileReported = false;
                script.Revision++;
                ScriptConfiguration.Refresh(script);
                changed = true;
                Write(LogLevel.Verbose, $"Reloaded externally modified script '{script.Name}'.");
                Echo($"[Saru] Script '{script.Name}' was reloaded.");
            }
            catch (Exception ex) { Write(LogLevel.Error, $"Unable to reload script '{script.Name}': {ex.Message}"); }
        }
        if (changed) Configuration.Save();
    }

    private string ScriptPath(ScriptEntry script) => Path.Combine(scriptsPath, script.FileName);
    public string ScriptFullPath(ScriptEntry script) => Path.GetFullPath(ScriptPath(script));
    private bool IsRemotelyManaged(ScriptEntry script) => Configuration.InstalledRemoteScripts.Exists(value => value.FileName.Equals(script.FileName, StringComparison.OrdinalIgnoreCase));
    public bool IsRemotelyManagedScript(ScriptEntry script) => IsRemotelyManaged(script);
    private bool RemoveMissingLocalScripts()
    {
        var removed = false;
        foreach (var script in Configuration.Scripts.Where(value => !File.Exists(ScriptPath(value)) && !IsRemotelyManaged(value)).ToArray())
        {
            Stop(script);
            Configuration.Scripts.Remove(script);
            removed = true;
            Write(LogLevel.Verbose, $"Removed missing local script '{script.Name}'.");
            Echo($"[Saru] Removed missing local script '{script.Name}'.");
        }
        return removed;
    }
    public bool IsRemoteScriptInstalled(string name) => Configuration.InstalledRemoteScripts.Exists(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(scriptsPath, value.FileName)));
    public bool IsRemoteScriptLoading(string name) => loadingRemoteScripts.Contains(name);
    public InstalledRemoteScript? InstalledRemoteScriptVersion(string name) => Configuration.InstalledRemoteScripts.Find(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public bool HasRemoteUpdate(ScriptEntry script)
    {
        var installed = Configuration.InstalledRemoteScripts.Find(value => value.FileName.Equals(script.FileName, StringComparison.OrdinalIgnoreCase));
        return installed != null && HasRemoteUpdate(installed.Name);
    }
    public bool HasRemoteUpdate(string name) =>
        InstalledRemoteScriptVersion(name) is { } installed &&
        latestRemoteVersions.TryGetValue(name, out var latest) &&
        !latest.Sha.Equals(installed.VersionSha, StringComparison.OrdinalIgnoreCase);
    public void UpdateRemoteCatalog(IReadOnlyList<RemoteScript> catalog)
    {
        latestRemoteVersions.Clear();
        foreach (var script in catalog)
            if (script.Versions.Count > 0) latestRemoteVersions[script.Name] = script.Versions[0];
        lastRemoteCatalogCheck = DateTime.UtcNow;
    }
    private void CheckRemoteScriptUpdatesIfDue()
    {
        if (checkingRemoteCatalog || DateTime.UtcNow - lastRemoteCatalogCheck < TimeSpan.FromMinutes(30)) return;
        checkingRemoteCatalog = true;
        Task.Run(async () =>
        {
            try
            {
                var catalog = await scriptRepository.GetCatalogAsync();
                QueueMainThread(() => { UpdateRemoteCatalog(catalog); checkingRemoteCatalog = false; });
            }
            catch { QueueMainThread(() => checkingRemoteCatalog = false); }
        });
    }
    public void InstallRemoteScript(RemoteScript script, RemoteScriptVersion version)
    {
        if (!loadingRemoteScripts.Add(script.Name)) return;
        Task.Run(async () =>
        {
            try
            {
                var source = await scriptRepository.DownloadScriptAsync(script.Name, version.Sha);
                QueueMainThread(() => ApplyRemoteScript(script, version, source));
            }
            catch (Exception ex)
            {
                QueueMainThread(() => { loadingRemoteScripts.Remove(script.Name); Echo($"[Saru] Failed to download '{script.Name}': {ex.Message}"); });
            }
        });
    }
    private void ApplyRemoteScript(RemoteScript script, RemoteScriptVersion version, string source)
    {
        try
        {
            var fileName = ScriptFileName(script.Name);
            var entry = Configuration.Scripts.Find(value => value.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new ScriptEntry { Name = script.Name, FileName = fileName };
                Configuration.Scripts.Add(entry);
            }
            if (IsRunning(entry)) Stop(entry);
            WriteScriptFile(entry, source);
            var installed = InstalledRemoteScriptVersion(script.Name);
            if (installed == null)
            {
                installed = new InstalledRemoteScript { Name = script.Name, FileName = fileName };
                Configuration.InstalledRemoteScripts.Add(installed);
            }
            installed.FileName = fileName;
            installed.VersionSha = version.Sha;
            installed.VersionTimestamp = version.Timestamp;
            Configuration.Save();
            Echo($"[Saru] Installed '{script.Name}'.");
        }
        finally { loadingRemoteScripts.Remove(script.Name); }
    }
    public void UninstallRemoteScript(string name)
    {
        var installed = InstalledRemoteScriptVersion(name);
        if (installed == null) return;
        var entry = Configuration.Scripts.Find(value => value.FileName.Equals(installed.FileName, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            Stop(entry);
            var path = ScriptPath(entry);
            if (File.Exists(path)) File.Delete(path);
            Configuration.Scripts.Remove(entry);
        }
        Configuration.InstalledRemoteScripts.Remove(installed);
        Configuration.Save();
    }
    private void RestoreMissingRemoteScripts()
    {
        foreach (var installed in Configuration.InstalledRemoteScripts.Where(value => !string.IsNullOrWhiteSpace(value.VersionSha) && !File.Exists(Path.Combine(scriptsPath, value.FileName))).ToArray())
            InstallRemoteScript(new RemoteScript(installed.Name, "", []), new RemoteScriptVersion(installed.VersionSha, installed.VersionTimestamp));
    }
    private static string ScriptFileName(string name) => name + ".js";
    private static bool IsSafeScriptFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) &&
        fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    private static ScriptFileContent ReadScriptFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(MD5.HashData(bytes));
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new ScriptFileContent(reader.ReadToEnd(), hash);
    }
    private void WriteScriptFile(ScriptEntry script, string source)
    {
        try
        {
            Directory.CreateDirectory(scriptsPath);
            var path = ScriptPath(script);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
            script.Source = source;
            script.ContentHash = ReadScriptFile(path).Hash;
            script.Revision++;
            ScriptConfiguration.Refresh(script);
        }
        catch (Exception ex) { Write(LogLevel.Error, $"Unable to save script '{script.Name}': {ex.Message}"); }
    }

    public void Run(ScriptEntry script)
    {
        if (runtimes.TryGetValue(script.Id, out var existing) && existing.IsRunning) return;
        existing?.Dispose();
        Write(LogLevel.Verbose, $"Running script '{script.Name}'.");
        var runtime = new ScriptRuntime(Write, cardSourceNpcs, script.Config);
        runtimes[script.Id] = runtime;
        runtime.Run(script.Source);
    }
    public void Stop(ScriptEntry script)
    {
        if (!runtimes.Remove(script.Id, out var runtime)) return;
        runtime.Stop();
        runtime.Dispose();
    }
    public void StopAll()
    {
        foreach (var runtime in runtimes.Values) { runtime.Stop(); runtime.Dispose(); }
        runtimes.Clear();
    }
    public bool IsRunning(ScriptEntry script) => runtimes.TryGetValue(script.Id, out var runtime) && runtime.IsRunning;
    public void SaveConfiguration() => Configuration.Save();
    public bool RenameScript(ScriptEntry script, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        name = name.Trim();
        if (Configuration.Scripts.Exists(value => value.Id != script.Id && value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        var oldPath = ScriptPath(script);
        var newPath = Path.Combine(scriptsPath, ScriptFileName(name));
        if (File.Exists(newPath) && !oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (File.Exists(oldPath)) File.Move(oldPath, newPath);
            script.Name = name;
            script.FileName = ScriptFileName(name);
            var content = ReadScriptFile(newPath);
            script.Source = content.Source;
            script.ContentHash = content.Hash;
        }
        catch (Exception ex) { Write(LogLevel.Error, $"Unable to rename script '{script.Name}': {ex.Message}"); return false; }
        Configuration.Save();
        return true;
    }
    private void SynchronizeScriptDirectory()
    {
        Directory.CreateDirectory(scriptsPath);
        var changed = false;
        foreach (var path in Directory.EnumerateFiles(scriptsPath, "*.js", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (Configuration.Scripts.Exists(value => value.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))) continue;
            var proposed = string.Concat(Path.GetFileNameWithoutExtension(path).Select(character => char.IsWhiteSpace(character) ? '_' : character));
            var name = UniqueScriptName(proposed, Guid.Empty);
            var normalizedFileName = ScriptFileName(name);
            var normalizedPath = Path.Combine(scriptsPath, normalizedFileName);
            if (!fileName.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase) && !File.Exists(normalizedPath))
            {
                File.Move(path, normalizedPath);
                fileName = normalizedFileName;
            }
            var entry = new ScriptEntry { Name = name, FileName = fileName };
            var content = ReadScriptFile(Path.Combine(scriptsPath, fileName));
            entry.Source = content.Source;
            entry.ContentHash = content.Hash;
            ScriptConfiguration.Refresh(entry);
            Configuration.Scripts.Add(entry);
            changed = true;
            Echo($"[Saru] Script erkannt: {name}");
        }
        if (changed) Configuration.Save();
    }
    private string UniqueScriptName(string proposed, Guid excludedId)
    {
        var basis = string.IsNullOrWhiteSpace(proposed) ? "Script" : proposed.Replace(" ", "_");
        var name = basis;
        var suffix = 2;
        while (Configuration.Scripts.Exists(value => value.Id != excludedId && value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{basis}_{suffix++}";
        return name;
    }
    public string ExportInfos() => InfoTracker.Export(PluginInterface.ConfigDirectory.FullName);
    public void Write(LogLevel level, string message)
    {
        if (level == LogLevel.Error) Log.Error(message); else Log.Information(message);
    }
    public void Dispose()
    {
        Write(LogLevel.Verbose, "Saru unloading.");
        Framework.Update -= OnUpdate;
        ChatGui.ChatMessage -= OnChatMessage;
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, OnDialogState);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainOrDependencies;
        PluginInterface.UiBuilder.OpenConfigUi -= dependencyWindow.Open;
        CommandManager.RemoveHandler("/saru");
        windows.RemoveAllWindows();
        StopAll();
    }
}
public readonly record struct MoveRequest(Vector3 Target, float Buffer, uint? MapId);
public readonly record struct PluginDependencyStatus(string Name, bool IsRequired, bool IsAvailable);
public readonly record struct ChatMessageRecord(string Message);
public readonly record struct ActivationRequest(uint? Id, ulong? Key);
public readonly record struct PendingActivation(ulong Key, DateTime Due);
public readonly record struct ScriptFileContent(string Source, string Hash);
public readonly record struct GameStateSnapshot(bool InZoneChange, bool WaitForDuty, bool BoundByDuty, bool OccupiedInQuestEvent, bool InDutyQueue, bool InCombat, bool Mounted, bool Jumping, uint MapId, uint TerritoryId, uint Instance, bool IsLoggedIn, bool IsPvP, bool SelectYesnoOpen, bool RideShootingResultOpen);
