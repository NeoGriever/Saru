using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;

namespace Saru;

/// <summary>
/// Starts Duty Support duties through AutoDuty and tracks the complete AutoDuty workflow.
/// AutoDuty remains an optional IPC-only integration.
/// </summary>
public sealed class ContentHelperApi
{
    private const string ContentHasPathIpc = "AutoDuty.ContentHasPath";
    private const string IsStoppedIpc = "AutoDuty.IsStopped";
    private const string SetConfigIpc = "AutoDuty.SetConfig";
    private const string RunIpc = "AutoDuty.Run";
    private const string StopIpc = "AutoDuty.Stop";

    private readonly Action<LogLevel, string> write;
    private readonly ICallGateSubscriber<uint, bool> contentHasPath;
    private readonly ICallGateSubscriber<bool> isStopped;
    private readonly ICallGateSubscriber<string, string, object> setConfig;
    private readonly ICallGateSubscriber<uint, int, bool, object> run;
    private readonly ICallGateSubscriber<object> stop;
    private readonly object stateLock = new();
    private uint? activeContentFinderConditionId;
    private uint? activeTerritoryId;

    public ContentHelperApi(Action<LogLevel, string> write)
    {
        this.write = write;
        contentHasPath = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>(ContentHasPathIpc);
        isStopped = Plugin.PluginInterface.GetIpcSubscriber<bool>(IsStoppedIpc);
        setConfig = Plugin.PluginInterface.GetIpcSubscriber<string, string, object>(SetConfigIpc);
        run = Plugin.PluginInterface.GetIpcSubscriber<uint, int, bool, object>(RunIpc);
        stop = Plugin.PluginInterface.GetIpcSubscriber<object>(StopIpc);
    }

    public bool ActivateDungeon(uint contentFinderConditionId)
    {
        if (!TryResolve(contentFinderConditionId, out var dungeon)) return false;
        return ActivateDungeon(dungeon);
    }

    public bool ActivateDungeon(string name)
    {
        if (!TryResolve(name, out var dungeon)) return false;
        return ActivateDungeon(dungeon);
    }

    public bool IsSupported(uint contentFinderConditionId)
    {
        return TryResolve(contentFinderConditionId, out var dungeon) && HasPath(dungeon.TerritoryId);
    }

    public bool IsSupported(string name)
    {
        return TryResolve(name, out var dungeon) && HasPath(dungeon.TerritoryId);
    }

    /// <summary>
    /// Returns true from AutoDuty activation until it reports stopped while the player is outside
    /// the territory that was started. This deliberately covers Duty Support UI and queue time.
    /// </summary>
    public bool IsBusy()
    {
        uint territoryId;
        lock (stateLock)
        {
            if (!activeTerritoryId.HasValue) return false;
            territoryId = activeTerritoryId.Value;
        }

        if (!TryIsStopped(out var stopped)) return true;
        if (Plugin.ClientState.TerritoryType == territoryId || !stopped) return true;

        lock (stateLock)
        {
            // A new activation may have occurred while the IPC call was in progress.
            if (activeTerritoryId == territoryId)
                ClearActiveDungeon();
        }
        return false;
    }

    public bool Stop()
    {
        var succeeded = false;
        try
        {
            if (!stop.HasAction)
            {
                LogUnavailable(StopIpc);
                return false;
            }

            stop.InvokeAction();
            succeeded = true;
        }
        catch (Exception ex)
        {
            LogFailure(StopIpc, ex);
        }
        finally
        {
            lock (stateLock) ClearActiveDungeon();
        }
        return succeeded;
    }

    private bool ActivateDungeon(ResolvedDungeon dungeon)
    {
        // Do not overwrite the state of an AutoDuty workflow started by this runtime.
        if (IsBusy())
        {
            write(LogLevel.Error, "ContentHelper cannot start a dungeon while its previous AutoDuty run is still active.");
            return false;
        }

        if (!HasPath(dungeon.TerritoryId)) return false;
        if (!setConfig.HasAction || !run.HasAction || !isStopped.HasFunction)
        {
            LogUnavailable(!setConfig.HasAction ? SetConfigIpc : !run.HasAction ? RunIpc : IsStoppedIpc);
            return false;
        }

        try
        {
            setConfig.InvokeAction("Meta.Unsynced", "False");
            setConfig.InvokeAction("Meta.DutyModeEnum", "Support");
            lock (stateLock)
            {
                activeContentFinderConditionId = dungeon.ContentFinderConditionId;
                activeTerritoryId = dungeon.TerritoryId;
            }
            run.InvokeAction(dungeon.TerritoryId, 1, true);
            return true;
        }
        catch (Exception ex)
        {
            lock (stateLock) ClearActiveDungeon();
            LogFailure("AutoDuty configuration or run", ex);
            return false;
        }
    }

    private bool HasPath(uint territoryId)
    {
        try
        {
            if (!contentHasPath.HasFunction)
            {
                LogUnavailable(ContentHasPathIpc);
                return false;
            }
            return contentHasPath.InvokeFunc(territoryId);
        }
        catch (Exception ex)
        {
            LogFailure(ContentHasPathIpc, ex);
            return false;
        }
    }

    private bool TryIsStopped(out bool stopped)
    {
        stopped = false;
        try
        {
            if (!isStopped.HasFunction)
            {
                LogUnavailable(IsStoppedIpc);
                return false;
            }
            stopped = isStopped.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(IsStoppedIpc, ex);
            return false;
        }
    }

    private bool TryResolve(uint contentFinderConditionId, out ResolvedDungeon dungeon)
    {
        dungeon = default;
        if (contentFinderConditionId == 0) return false;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
            var condition = sheet.GetRow(contentFinderConditionId);
            return TryCreateResolvedDungeon(condition, out dungeon);
        }
        catch (Exception ex)
        {
            LogFailure("ContentFinderCondition lookup", ex);
            return false;
        }
    }

    private bool TryResolve(string name, out ResolvedDungeon dungeon)
    {
        dungeon = default;
        var normalizedName = NormalizeName(name);
        if (normalizedName.Length == 0) return false;

        try
        {
            var ids = new HashSet<uint>();
            AddMatchingIds(Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(), normalizedName, ids);
            try
            {
                AddMatchingIds(Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English), normalizedName, ids);
            }
            catch (Exception ex)
            {
                // English lookup is a convenience fallback; a localized match remains valid.
                LogFailure("English ContentFinderCondition name lookup", ex);
            }
            if (ids.Count != 1) return false;
            return TryResolve(ids.Single(), out dungeon);
        }
        catch (Exception ex)
        {
            LogFailure("ContentFinderCondition name lookup", ex);
            return false;
        }
    }

    private static void AddMatchingIds(IEnumerable<ContentFinderCondition> sheet, string normalizedName, ISet<uint> ids)
    {
        foreach (var condition in sheet)
            if (condition.RowId != 0 && NormalizeName(condition.Name.ToString()) == normalizedName)
                ids.Add(condition.RowId);
    }

    private static bool TryCreateResolvedDungeon(ContentFinderCondition condition, out ResolvedDungeon dungeon)
    {
        var territoryId = condition.TerritoryType.RowId;
        if (condition.RowId == 0 || territoryId == 0)
        {
            dungeon = default;
            return false;
        }

        dungeon = new ResolvedDungeon(condition.RowId, territoryId);
        return true;
    }

    private static string NormalizeName(string? value) => (value ?? string.Empty).Trim().Replace('\u2013', '-').ToUpperInvariant();

    private void ClearActiveDungeon()
    {
        activeContentFinderConditionId = null;
        activeTerritoryId = null;
    }

    private void LogUnavailable(string endpoint) => write(LogLevel.Error, $"ContentHelper: AutoDuty IPC '{endpoint}' is not available.");
    private void LogFailure(string operation, Exception ex) => write(LogLevel.Error, $"ContentHelper: {operation} failed: {ex.GetBaseException().Message}");

    private readonly record struct ResolvedDungeon(uint ContentFinderConditionId, uint TerritoryId);
}
