using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
    private bool dialogVisible;
    private DateTime lastReachCheck = DateTime.MinValue;
    private CancellationTokenSource? cancellation;
    private int scheduledCallId;
    private int executionGeneration;
    private volatile bool active;
    private volatile bool executing;
    public bool IsRunning => active;
    private readonly CardSourceNpcsApi cardSourceNpcs;
    public ScriptRuntime(Action<LogLevel, string> write, CardSourceNpcsApi cardSourceNpcs) { this.write = write; this.cardSourceNpcs = cardSourceNpcs; Start(); }
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
        engine.Execute("const FFEV = Object.freeze({ message: 'message', dialog: 'dialog', arrived: 'arrived', time: 'time', reach: 'reach', onZoneChanged: 'zoneChanged', onZoneChangeStart: 'zoneChangeStart', onDutyEnd: 'dutyEnd', onEventDone: 'eventDone' }); Object.defineProperty(globalThis, 'curPos', { writable: false, configurable: false }); Object.defineProperty(globalThis, 'inZoneChange', { get: () => __zoneChange.value, configurable: false }); function dist(pos1, pos2) { if (arguments.length === 1 && typeof pos1 === 'number') return __distToId(pos1); const dx = Number(pos1.x) - Number(pos2.x); const dy = Number(pos1.y) - Number(pos2.y); const dz = Number(pos1.z) - Number(pos2.z); return Math.sqrt(dx * dx + dy * dy + dz * dz); }");
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
    public void UpdatePosition(Vector3 position) => currentPosition.Update(position);
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
    public void TrackMove(Vector3 target, float buffer) { moveTarget = target; moveBuffer = buffer; }
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
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Vector3.Distance(player.Position, moveTarget.Value) > moveBuffer) return;
        moveTarget = null;
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
    private sealed class PositionApi { private float currentX; private float currentY; private float currentZ; public Vector3 Snapshot => new(Volatile.Read(ref currentX), Volatile.Read(ref currentY), Volatile.Read(ref currentZ)); public void Update(Vector3 value) { Volatile.Write(ref currentX, value.X); Volatile.Write(ref currentY, value.Y); Volatile.Write(ref currentZ, value.Z); } public float x => Volatile.Read(ref currentX); public float y => Volatile.Read(ref currentY); public float z => Volatile.Read(ref currentZ); }
    private sealed class ZoneChangeApi { private int changing; public bool value => Volatile.Read(ref changing) != 0; public void Update(bool value) => Volatile.Write(ref changing, value ? 1 : 0); }
    private sealed class GameStateApi
    {
        private int zoneChanging, waitForDutyValue, boundByDutyValue, occupiedInQuestEventValue, inDutyQueueValue, inCombatValue, mountedValue, jumpingValue, isLoggedInValue, isPvPValue, selectYesnoOpenValue, rideShootingResultOpenValue;
        private int currentMapValue, territoryIdValue, instanceValue;
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
        public int territoryId => Volatile.Read(ref territoryIdValue);
        public int instance => Volatile.Read(ref instanceValue);
        public bool closeRideShootingResult() => Plugin.Instance.CloseRideShootingResult();
        public bool answerYes() => Plugin.Instance.QueueSelectDialog(true);
        public void Update(GameStateSnapshot value)
        {
            Volatile.Write(ref zoneChanging, value.InZoneChange ? 1 : 0); Volatile.Write(ref waitForDutyValue, value.WaitForDuty ? 1 : 0); Volatile.Write(ref boundByDutyValue, value.BoundByDuty ? 1 : 0); Volatile.Write(ref occupiedInQuestEventValue, value.OccupiedInQuestEvent ? 1 : 0); Volatile.Write(ref inDutyQueueValue, value.InDutyQueue ? 1 : 0); Volatile.Write(ref inCombatValue, value.InCombat ? 1 : 0); Volatile.Write(ref mountedValue, value.Mounted ? 1 : 0); Volatile.Write(ref jumpingValue, value.Jumping ? 1 : 0); Volatile.Write(ref isLoggedInValue, value.IsLoggedIn ? 1 : 0); Volatile.Write(ref isPvPValue, value.IsPvP ? 1 : 0); Volatile.Write(ref selectYesnoOpenValue, value.SelectYesnoOpen ? 1 : 0); Volatile.Write(ref rideShootingResultOpenValue, value.RideShootingResultOpen ? 1 : 0); Volatile.Write(ref currentMapValue, unchecked((int)value.MapId)); Volatile.Write(ref territoryIdValue, unchecked((int)value.TerritoryId)); Volatile.Write(ref instanceValue, unchecked((int)value.Instance));
        }
    }
    private sealed class TimerApi(ScriptRuntime runtime) { public void At(long timestamp) => runtime.AddTimer(timestamp); public bool Un(long timestamp) => runtime.RemoveTimer(timestamp); public bool UnI(int index) => runtime.RemoveTimerAt(index); }
    private sealed class TargetApi(ScriptRuntime runtime) { public void At(uint id, float radius = 3) => runtime.AddReach(id, radius); public bool Un(uint id) => runtime.RemoveReach(id); public bool Select(uint id) => Plugin.Instance.SelectTarget(id); public bool Activate(uint? id = null) => Plugin.Instance.ActivateTarget(id); }
    private sealed class ConsoleApi(Action<LogLevel, string> write) { public void log(object value) { var message = value?.ToString() ?? "null"; write(LogLevel.Verbose, message); Plugin.Instance.Echo(message); } public void info(object value) => write(LogLevel.Verbose, value?.ToString() ?? "null"); public void error(object value) => write(LogLevel.Error, value?.ToString() ?? "null"); }
    private sealed class DialogApi { public bool Select(JsValue value) => value.IsBoolean() ? Plugin.Instance.SelectDialog(value.AsBoolean()) : value.IsNumber() && value.AsNumber() == 1 ? Plugin.Instance.SelectDialog(true) : value.IsNumber() && value.AsNumber() == 0 ? Plugin.Instance.SelectDialog(false) : false; public bool Y() => Plugin.Instance.SelectDialog(true); public bool N() => Plugin.Instance.SelectDialog(false); }
    private sealed class NavmeshApi
    {
        public bool IsRunning() => Plugin.Instance.IsNavmeshPathRunning();
        public bool MoveTo(float x, float y, float z, float buffer) => Plugin.Instance.MoveTo(x, y, z, buffer);
        public bool MoveTo(ObjectInstance position, float buffer = 0) => Plugin.Instance.MoveTo((float)position.Get("x").AsNumber(), (float)position.Get("y").AsNumber(), (float)position.Get("z").AsNumber(), buffer);
    }
}
