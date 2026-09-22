using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Jint;
using Jint.Native;
using Jint.Native.Object;
namespace Saru;
public sealed class ScriptRuntime : IDisposable
{
    private Engine? engine;
    private readonly Action<LogLevel, string> write;
    private readonly Dictionary<string, List<JsValue>> listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<long> timers = new();
    private readonly List<ReachRequest> reaches = new();
    private readonly List<ScheduledCall> scheduledCalls = new();
    private readonly PositionApi currentPosition = new();
    private readonly ZoneChangeApi zoneChange = new();
    private readonly GameStateApi gameState = new();
    private readonly ConcurrentDictionary<uint, Vector3> objectPositions = new();
    private Vector3? moveTarget;
    private float moveBuffer;
    private uint? moveMapId;
    private bool dialogVisible;
    private DateTime lastReachCheck = DateTime.MinValue;
    private CancellationTokenSource? cancellation;
    private int scheduledCallId;
    private int executionGeneration;
    private volatile bool active;
    private volatile bool executing;
    public bool IsRunning => active;
    private readonly CardSourceNpcsApi cardSourceNpcs;
    private readonly ScriptConfigApi configApi;
    public ScriptRuntime(Action<LogLevel, string> write, CardSourceNpcsApi cardSourceNpcs, IReadOnlyList<ScriptConfigEntry> config)
    {
        this.write = write;
        this.cardSourceNpcs = cardSourceNpcs;
        configApi = ScriptConfiguration.CreateRuntimeApi(config.Select(ScriptConfiguration.Clone).ToList());
        Start();
    }
    public void Start()
    {
        cancellation = new CancellationTokenSource();
        engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(5)).CancellationToken(cancellation.Token));
        engine.SetValue("time", new TimeApi());
        engine.SetValue("Timer", new TimerApi(this));
        engine.SetValue("target", new TargetApi(this));
        engine.SetValue("console", new ConsoleApi(write));
        engine.SetValue("WaitForDialog", new DialogApi());
        engine.SetValue("vNavMesh", new NavmeshApi());
        engine.SetValue("CardSourceNPCs", cardSourceNpcs);
        engine.SetValue("Saucy", new SaucyApi(cardSourceNpcs));
        engine.SetValue("chocoholic", new ChocoholicApi());
        engine.SetValue("Plugin", new Func<string, PluginApi>(name => new PluginApi(name, write)));
        engine.SetValue("saru", configApi);
        engine.SetValue("addEventListener", new Action<string, JsValue>(AddEventListener));
        engine.SetValue("Exit", new Action(Exit));
        engine.SetValue("sendMsg", new Action<string>(Plugin.Instance.SendMessage));
        engine.SetValue("curPos", currentPosition);
        engine.SetValue("__zoneChange", zoneChange);
        engine.SetValue("FFXIV", gameState);
        engine.SetValue("__distToId", new Func<uint, double>(DistanceToId));
        engine.SetValue("setTimeout", new Func<JsValue, double, int>(SetTimeout));
        engine.SetValue("setInterval", new Func<JsValue, double, int>(SetInterval));
        engine.SetValue("clearTimeout", new Action<JsValue>(ClearScheduledValue));
        engine.SetValue("clearInterval", new Action<JsValue>(ClearScheduledValue));
        engine.Execute("const FFEV = Object.freeze({ message: 'message', dialog: 'dialog', arrived: 'arrived', time: 'time', reach: 'reach', onMapChange: 'mapChange', onZoneChanged: 'zoneChanged', onZoneChangeStart: 'zoneChangeStart', onDutyEnd: 'dutyEnd', onEventDone: 'eventDone' }); Object.defineProperty(globalThis, 'curPos', { writable: false, configurable: false }); Object.defineProperty(globalThis, 'inZoneChange', { get: () => __zoneChange.value, configurable: false }); function dist(pos1, pos2) { if (arguments.length === 1 && typeof pos1 === 'number') return __distToId(pos1); const dx = Number(pos1.x) - Number(pos2.x); const dy = Number(pos1.y) - Number(pos2.y); const dz = Number(pos1.z) - Number(pos2.z); return Math.sqrt(dx * dx + dy * dy + dz * dz); }");
        engine.SetValue("True", true);
        engine.SetValue("False", false);
        write(LogLevel.Verbose, "JavaScript runtime started.");
    }
    public void Run(string source)
    {
        if (active) return;
        if (engine == null || cancellation?.IsCancellationRequested == true) Start();
        active = true;
        executing = true;
        var current = engine!;
        var generation = Interlocked.Increment(ref executionGeneration);
        Task.Run(() =>
        {
            try { current.Execute(source); InvokeLifecycle("Start"); write(LogLevel.Verbose, "Script started."); }
            catch (OperationCanceledException) { write(LogLevel.Verbose, "Script stopped."); }
            catch (Exception ex)
            {
                if (generation == Volatile.Read(ref executionGeneration))
                {
                    active = false;
                    ClearState();
                }
                write(LogLevel.Error, $"JavaScript error: {ex.Message}");
            }
            finally { if (generation == Volatile.Read(ref executionGeneration)) executing = false; }
        });
    }
    public void Update()
    {
        if (engine == null || !active || executing) return;
        UpdateDialog();
        UpdateMove();
        UpdateTimers();
        UpdateScheduledCalls();
        UpdateReaches();
    }
    public void UpdatePosition(Vector3 position, uint mapId) => currentPosition.Update(position, mapId);
    public void UpdateGameState(GameStateSnapshot value) { zoneChange.Update(value.InZoneChange); gameState.Update(value); }
    public void NotifyEvent(string name) { if (engine != null && active && !executing) Dispatch(name); }
    public void UpdateObjectPositions()
    {
        objectPositions.Clear();
        var player = currentPosition.Snapshot;
        foreach (var obj in Plugin.ObjectTable)
            if (obj.BaseId != 0 && obj.IsTargetable) objectPositions.AddOrUpdate(obj.BaseId, obj.Position, (_, previous) => Vector3.DistanceSquared(player, obj.Position) < Vector3.DistanceSquared(player, previous) ? obj.Position : previous);
    }
    private double DistanceToId(uint id) => objectPositions.TryGetValue(id, out var position) ? Vector3.Distance(currentPosition.Snapshot, position) : double.NaN;
    public void ReceiveMessage(string message)
    {
        if (engine == null || !active || executing || !listeners.TryGetValue("message", out var list)) return;
        executing = true;
        try
        {
            foreach (var callback in list.ToArray())
                try { engine.Invoke(callback, message); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { write(LogLevel.Error, $"Event 'message' failed: {ex.Message}"); }
        }
        finally { executing = false; }
    }
    public void TrackMove(Vector3 target, float buffer, uint? mapId) { moveTarget = target; moveBuffer = buffer; moveMapId = mapId; }
    public void CancelMove() { moveTarget = null; moveMapId = null; }
    public void AddTimer(long timestamp) => timers.Add(timestamp);
    public bool RemoveTimer(long timestamp) => timers.Remove(timestamp);
    public bool RemoveTimerAt(int index) { if (index < 0 || index >= timers.Count) return false; timers.RemoveAt(index); return true; }
    public void AddReach(uint id, float radius) => reaches.Add(new ReachRequest(id, Math.Max(0, radius)));
    public bool RemoveReach(uint id) => reaches.RemoveAll(x => x.Id == id) > 0;
    private int SetTimeout(JsValue callback, double delay) => AddScheduled(callback, delay, false);
    private int SetInterval(JsValue callback, double delay) => AddScheduled(callback, delay, true);
    private int AddScheduled(JsValue callback, double delay, bool repeat)
    {
        if (!callback.IsObject()) return 0;
        var id = Interlocked.Increment(ref scheduledCallId);
        scheduledCalls.Add(new ScheduledCall(id, callback, Math.Max(1, delay), repeat));
        return id;
    }
    private void ClearScheduled(int id) => scheduledCalls.RemoveAll(x => x.Id == id);
    private void ClearScheduledValue(JsValue value)
    {
        if (value.IsNumber()) ClearScheduled((int)value.AsNumber());
    }
    private void AddEventListener(string name, JsValue callback)
    {
        if (!listeners.TryGetValue(name, out var list)) listeners[name] = list = new();
        list.Add(callback);
    }
    private void UpdateDialog()
    {
        var addon = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
        var visible = addon.Address != nint.Zero && addon.IsVisible;
        if (visible && !dialogVisible) { dialogVisible = true; var choice = Dispatch("dialog"); if (choice.HasValue) Plugin.Instance.SelectDialog(choice.Value); }
        else if (!visible) dialogVisible = false;
    }
    private void UpdateMove()
    {
        if (moveTarget == null) return;
        if (moveMapId.HasValue && currentPosition.mapId != moveMapId.Value) { CancelMove(); return; }
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Vector3.Distance(player.Position, moveTarget.Value) > moveBuffer) return;
        CancelMove();
        Dispatch("arrived");
    }
    private void UpdateTimers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = timers.Count - 1; i >= 0; i--) if (timers[i] <= now) { timers.RemoveAt(i); Dispatch("time"); }
    }
    private void UpdateScheduledCalls()
    {
        var now = DateTime.UtcNow;
        for (var i = scheduledCalls.Count - 1; i >= 0; i--)
        {
            var call = scheduledCalls[i];
            if (call.Next > now) continue;
            if (call.Repeat) call.Next = now.AddMilliseconds(call.Delay); else scheduledCalls.RemoveAt(i);
            try { engine!.Invoke(call.Callback); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { write(LogLevel.Error, $"Timer callback failed: {ex.Message}"); }
        }
    }
    private void UpdateReaches()
    {
        if (DateTime.UtcNow - lastReachCheck < TimeSpan.FromSeconds(1)) return;
        lastReachCheck = DateTime.UtcNow;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        for (var i = reaches.Count - 1; i >= 0; i--)
        {
            var request = reaches[i];
            foreach (var obj in Plugin.ObjectTable)
                if (obj.BaseId == request.Id && obj.IsTargetable && Vector3.Distance(player.Position, obj.Position) <= request.Radius) { reaches.RemoveAt(i); Dispatch("reach"); break; }
        }
    }
    private bool? Dispatch(string name)
    {
        if (engine == null || !listeners.TryGetValue(name, out var list)) return null;
        bool? result = null;
        executing = true;
        try
        {
            foreach (var callback in list.ToArray())
                try { var value = engine.Invoke(callback); if (name.Equals("dialog", StringComparison.OrdinalIgnoreCase) && value.IsBoolean()) result = value.AsBoolean(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { write(LogLevel.Error, $"Event '{name}' failed: {ex.Message}"); }
        }
        finally { executing = false; }
        return result;
    }
    private void InvokeLifecycle(string name)
    {
        if (engine == null) return;
        var callback = engine.GetValue(name);
        if (!callback.IsUndefined()) engine.Invoke(callback);
    }
    public void Stop()
    {
        if (!active) return;
        if (executing) { active = false; cancellation?.Cancel(); ClearState(); write(LogLevel.Verbose, "Stop requested."); return; }
        executing = true;
        try { if (InvokeStop()) write(LogLevel.Verbose, "Script stopped."); else write(LogLevel.Verbose, "Script hard terminated."); }
        catch (Exception ex) { write(LogLevel.Error, $"Stop failed: {ex.Message}"); }
        finally { active = false; cancellation?.Cancel(); ClearState(); executing = false; }
    }
    public void Reset()
    {
        Interlocked.Increment(ref executionGeneration);
        active = false;
        executing = false;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        engine = null;
        ClearState();
        Start();
        write(LogLevel.Verbose, "JavaScript runtime context refreshed.");
    }
    public void Exit() { if (!active) return; active = false; cancellation?.Cancel(); ClearState(); write(LogLevel.Verbose, "Script exited."); }
    private bool InvokeStop()
    {
        if (engine == null) return false;
        var callback = engine.GetValue("Stop");
        if (callback.IsUndefined()) return true;
        var result = engine.Invoke(callback);
        return result.IsBoolean() && result.AsBoolean();
    }
    private void ClearState() { listeners.Clear(); timers.Clear(); reaches.Clear(); scheduledCalls.Clear(); moveTarget = null; }
    public void Dispose() { active = false; cancellation?.Cancel(); cancellation?.Dispose(); engine = null; ClearState(); write(LogLevel.Verbose, "JavaScript runtime stopped."); }
    private readonly record struct ReachRequest(uint Id, float Radius);
    private sealed class ScheduledCall(int id, JsValue callback, double delay, bool repeat) { public int Id { get; } = id; public JsValue Callback { get; } = callback; public double Delay { get; } = delay; public bool Repeat { get; } = repeat; public DateTime Next { get; set; } = DateTime.UtcNow.AddMilliseconds(delay); }
    private sealed class TimeApi { public long getTime() => DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
    private sealed class PositionApi
    {
        private float currentX;
        private float currentY;
        private float currentZ;
        private int currentMapId;
        public Vector3 Snapshot => new(Volatile.Read(ref currentX), Volatile.Read(ref currentY), Volatile.Read(ref currentZ));
        public void Update(Vector3 value, uint mapId)
        {
            Volatile.Write(ref currentX, value.X);
            Volatile.Write(ref currentY, value.Y);
            Volatile.Write(ref currentZ, value.Z);
            Volatile.Write(ref currentMapId, unchecked((int)mapId));
        }
        public float x => Volatile.Read(ref currentX);
        public float y => Volatile.Read(ref currentY);
        public float z => Volatile.Read(ref currentZ);
        public uint mapId => unchecked((uint)Volatile.Read(ref currentMapId));
    }
    private sealed class ZoneChangeApi { private int changing; public bool value => Volatile.Read(ref changing) != 0; public void Update(bool value) => Volatile.Write(ref changing, value ? 1 : 0); }
    private sealed class GameStateApi
    {
        private int zoneChanging, waitForDutyValue, boundByDutyValue, occupiedInQuestEventValue, inDutyQueueValue, inCombatValue, mountedValue, jumpingValue, isLoggedInValue, isPvPValue, selectYesnoOpenValue, rideShootingResultOpenValue;
        private int currentMapValue, territoryIdValue, instanceValue;
        private readonly MapApi currentMapApi = new();
        public bool inZoneChange => Volatile.Read(ref zoneChanging) != 0;
        public bool waitForDuty => Volatile.Read(ref waitForDutyValue) != 0;
        public bool boundByDuty => Volatile.Read(ref boundByDutyValue) != 0;
        public bool occupiedInQuestEvent => Volatile.Read(ref occupiedInQuestEventValue) != 0;
        public bool inDutyQueue => Volatile.Read(ref inDutyQueueValue) != 0;
        public bool inCombat => Volatile.Read(ref inCombatValue) != 0;
        public bool mounted => Volatile.Read(ref mountedValue) != 0;
        public bool jumping => Volatile.Read(ref jumpingValue) != 0;
        public bool isLoggedIn => Volatile.Read(ref isLoggedInValue) != 0;
        public bool isPvP => Volatile.Read(ref isPvPValue) != 0;
        public bool selectYesnoOpen => Volatile.Read(ref selectYesnoOpenValue) != 0;
        public bool rideShootingResultOpen => Volatile.Read(ref rideShootingResultOpenValue) != 0;
        public int currentMap => Volatile.Read(ref currentMapValue);
        public MapApi map => currentMapApi;
        public int territoryId => Volatile.Read(ref territoryIdValue);
        public int instance => Volatile.Read(ref instanceValue);
        public bool closeRideShootingResult() => Plugin.Instance.CloseRideShootingResult();
        public bool answerYes() => Plugin.Instance.QueueSelectDialog(true);
        public void Update(GameStateSnapshot value)
        {
            Volatile.Write(ref zoneChanging, value.InZoneChange ? 1 : 0); Volatile.Write(ref waitForDutyValue, value.WaitForDuty ? 1 : 0); Volatile.Write(ref boundByDutyValue, value.BoundByDuty ? 1 : 0); Volatile.Write(ref occupiedInQuestEventValue, value.OccupiedInQuestEvent ? 1 : 0); Volatile.Write(ref inDutyQueueValue, value.InDutyQueue ? 1 : 0); Volatile.Write(ref inCombatValue, value.InCombat ? 1 : 0); Volatile.Write(ref mountedValue, value.Mounted ? 1 : 0); Volatile.Write(ref jumpingValue, value.Jumping ? 1 : 0); Volatile.Write(ref isLoggedInValue, value.IsLoggedIn ? 1 : 0); Volatile.Write(ref isPvPValue, value.IsPvP ? 1 : 0); Volatile.Write(ref selectYesnoOpenValue, value.SelectYesnoOpen ? 1 : 0); Volatile.Write(ref rideShootingResultOpenValue, value.RideShootingResultOpen ? 1 : 0); Volatile.Write(ref currentMapValue, unchecked((int)value.MapId)); currentMapApi.Update(value.MapId); Volatile.Write(ref territoryIdValue, unchecked((int)value.TerritoryId)); Volatile.Write(ref instanceValue, unchecked((int)value.Instance));
        }
    }
    private sealed class MapApi { private int currentId; public int id => Volatile.Read(ref currentId); public void Update(uint value) => Volatile.Write(ref currentId, unchecked((int)value)); }
    private sealed class TimerApi(ScriptRuntime runtime) { public void At(long timestamp) => runtime.AddTimer(timestamp); public bool Un(long timestamp) => runtime.RemoveTimer(timestamp); public bool UnI(int index) => runtime.RemoveTimerAt(index); }
    private sealed class TargetApi(ScriptRuntime runtime) { public void At(uint id, float radius = 3) => runtime.AddReach(id, radius); public bool Un(uint id) => runtime.RemoveReach(id); public bool Select(uint id) => Plugin.Instance.SelectTarget(id); public bool Activate(uint? id = null) => Plugin.Instance.ActivateTarget(id); }
    private sealed class ConsoleApi(Action<LogLevel, string> write) { public void log(object value) { var message = value?.ToString() ?? "null"; write(LogLevel.Verbose, message); Plugin.Instance.Echo(message); } public void info(object value) => write(LogLevel.Verbose, value?.ToString() ?? "null"); public void error(object value) => write(LogLevel.Error, value?.ToString() ?? "null"); }
    private sealed class DialogApi { public bool Select(JsValue value) => value.IsBoolean() ? Plugin.Instance.SelectDialog(value.AsBoolean()) : value.IsNumber() && value.AsNumber() == 1 ? Plugin.Instance.SelectDialog(true) : value.IsNumber() && value.AsNumber() == 0 ? Plugin.Instance.SelectDialog(false) : false; public bool Y() => Plugin.Instance.SelectDialog(true); public bool N() => Plugin.Instance.SelectDialog(false); }
    private sealed class ChocoholicApi
    {
        public bool Toggle(bool enabled) => Plugin.Instance.ToggleChocoholic(enabled);
        public bool SetNumberOfRaces(int numberOfRaces) => Plugin.Instance.SetChocoholicNumberOfRaces(numberOfRaces);
    }
    private sealed class PluginApi(string name, Action<LogLevel, string> write)
    {
        private readonly string name = name;
        private readonly Action<LogLevel, string> write = write;
        public object? IPC(string ipcName, ObjectInstance? arguments = null)
        {
            var assembly = FindAssembly();
            if (assembly == null) return null;
            var values = ReadArguments(arguments);
            try
            {
                var getter = typeof(IDalamudPluginInterface).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "GetIpcSubscriber" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == values.Length + 1);
                if (getter == null) return IpcUnavailable(ipcName);
                var subscriber = getter.MakeGenericMethod(Enumerable.Repeat(typeof(object), values.Length + 1).ToArray()).Invoke(Plugin.PluginInterface, [ipcName]);
                if (subscriber == null) return IpcUnavailable(ipcName);
                var subscriberType = subscriber.GetType();
                var hasAction = subscriberType.GetProperty("HasAction")?.GetValue(subscriber) as bool? == true;
                var hasFunction = subscriberType.GetProperty("HasFunction")?.GetValue(subscriber) as bool? == true;
                if (!hasAction && !hasFunction) return IpcUnavailable(ipcName);
                var methodName = hasFunction ? "InvokeFunc" : "InvokeAction";
                var invoke = subscriberType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == values.Length);
                if (invoke == null) return IpcUnavailable(ipcName);
                return invoke.Invoke(subscriber, values);
            }
            catch (Exception ex)
            {
                write(LogLevel.Error, $"IPC '{ipcName}' is not available for plugin '{name}': {ex.GetBaseException().Message}");
                return null;
            }
        }
        public object? Reflect(string target, ObjectInstance? arguments = null)
        {
            var assembly = FindAssembly();
            if (assembly == null) return null;
            var values = ReadArguments(arguments);
            try
            {
                var parts = target.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var type = default(Type);
                var memberStart = 0;
                for (var count = parts.Length - 1; count > 0; count--)
                {
                    type = assembly.GetType(string.Join('.', parts.Take(count)), throwOnError: false, ignoreCase: false);
                    if (type != null) { memberStart = count; break; }
                }
                if (type == null || memberStart >= parts.Length) return ReflectionUnavailable(target);
                object? instance = null;
                for (var index = memberStart; index < parts.Length - 1; index++)
                {
                    var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
                    var field = type.GetField(parts[index], flags);
                    if (field != null) instance = field.GetValue(instance);
                    else
                    {
                        var property = type.GetProperty(parts[index], flags);
                        if (property == null) return ReflectionUnavailable(target);
                        instance = property.GetValue(instance);
                    }
                    if (instance == null) return ReflectionUnavailable(target);
                    type = instance.GetType();
                }
                var methodFlags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
                foreach (var method in type.GetMethods(methodFlags).Where(method => method.Name == parts[^1] && method.GetParameters().Length == values.Length))
                {
                    if (!TryConvertArguments(method.GetParameters(), values, out var converted)) continue;
                    return method.Invoke(instance, converted);
                }
                return ReflectionUnavailable(target);
            }
            catch (Exception ex)
            {
                write(LogLevel.Error, $"Reflection target '{target}' is not available for plugin '{name}': {ex.GetBaseException().Message}");
                return null;
            }
        }
        private Assembly? FindAssembly()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => string.Equals(value.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (assembly == null) write(LogLevel.Error, $"Plugin not found: '{name}'.");
            return assembly;
        }
        private object? IpcUnavailable(string ipcName) { write(LogLevel.Error, $"IPC not available: '{ipcName}' for plugin '{name}'."); return null; }
        private object? ReflectionUnavailable(string target) { write(LogLevel.Error, $"Reflection target not available: '{target}' for plugin '{name}'."); return null; }
        private static object?[] ReadArguments(ObjectInstance? arguments)
        {
            if (arguments == null) return [];
            var length = arguments.Get("length");
            if (!length.IsNumber()) return [];
            var result = new object?[(int)length.AsNumber()];
            for (var index = 0; index < result.Length; index++) result[index] = ToHostValue(arguments.Get(index.ToString(CultureInfo.InvariantCulture)));
            return result;
        }
        private static object? ToHostValue(JsValue value)
        {
            if (value.IsNull() || value.IsUndefined()) return null;
            if (value.IsBoolean()) return value.AsBoolean();
            if (value.IsNumber()) return value.AsNumber();
            if (value.IsString()) return value.AsString();
            return value.ToObject();
        }
        private static bool TryConvertArguments(ParameterInfo[] parameters, object?[] values, out object?[] converted)
        {
            converted = new object?[values.Length];
            for (var index = 0; index < values.Length; index++)
                if (!TryConvertArgument(values[index], parameters[index].ParameterType, out converted[index])) return false;
            return true;
        }
        private static bool TryConvertArgument(object? value, Type destination, out object? converted)
        {
            var target = Nullable.GetUnderlyingType(destination) ?? destination;
            if (value == null) { converted = target.IsValueType && Nullable.GetUnderlyingType(destination) == null ? null : null; return !target.IsValueType || Nullable.GetUnderlyingType(destination) != null; }
            if (target.IsInstanceOfType(value)) { converted = value; return true; }
            try
            {
                if (target.IsEnum)
                {
                    converted = value is string text ? Enum.Parse(target, text, ignoreCase: true) : Enum.ToObject(target, Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture)!);
                    return true;
                }
                if (value is double number && target != typeof(double) && Math.Abs(number % 1) > double.Epsilon && target != typeof(float) && target != typeof(decimal)) { converted = null; return false; }
                converted = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
                return true;
            }
            catch { converted = null; return false; }
        }
    }
    private sealed class NavmeshApi
    {
        public bool IsRunning() => Plugin.Instance.IsNavmeshPathRunning();
        public bool MoveTo(float x, float y, float z, float buffer) => Plugin.Instance.MoveTo(x, y, z, buffer, null);
        public bool MoveTo(float x, float y, float z, float buffer, uint mapId) => Plugin.Instance.MoveTo(x, y, z, buffer, mapId);
        public bool MoveTo(ObjectInstance position, float buffer = 0)
        {
            var mapId = position.Get("mapId");
            return Plugin.Instance.MoveTo((float)position.Get("x").AsNumber(), (float)position.Get("y").AsNumber(), (float)position.Get("z").AsNumber(), buffer, mapId.IsNumber() ? (uint)mapId.AsNumber() : null);
        }
    }
}
