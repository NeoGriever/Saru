using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Saru;

/// <summary>
/// Late-bound view of Saucy's public Triple Triad data.  Saucy is deliberately
/// not referenced at compile time, so Saru still loads when Saucy is off.
/// </summary>
public sealed class CardSourceNpcsApi(IDataManager data, Action<LogLevel, string> write)
{
    private readonly Action<LogLevel, string> write = write;
    private readonly Dictionary<string, int> cardIdsByName = LoadCardNames(data);
    private bool sourceUnavailableReported;

    /// <summary>Rebuilt from Saucy's current GameNpcDB whenever a script reads it.</summary>
    public IReadOnlyList<CardSourceNpc> All
    {
        get
        {
            try
            {
                var gameNpcDb = GetSingleton("Saucy.TripleTriad.Data.GameNpcDB");
                var triadNpcDb = GetSingleton("Saucy.TripleTriad.Data.TriadNpcDB");
                var triadCardDb = GetSingleton("Saucy.TripleTriad.Data.TriadCardDB");
                if (gameNpcDb == null || triadNpcDb == null || triadCardDb == null)
                {
                    ReportSourceUnavailable();
                    return [];
                }

                var gameNpcs = GetMember(gameNpcDb, "mapNpcs") as IEnumerable;
                var npcNames = GetMember(triadNpcDb, "npcs") as IList;
                var cardNames = GetMember(triadCardDb, "cards") as IList;
                if (gameNpcs == null || npcNames == null || cardNames == null)
                    return [];

                var result = new List<CardSourceNpc>();
                foreach (var entry in gameNpcs)
                {
                    var gameNpc = GetMember(entry!, "Value"); // Dictionary<int, GameNpcInfo>
                    if (gameNpc == null)
                        continue;

                    var npcId = AsInt(GetMember(gameNpc, "npcId"));
                    var name = npcId >= 0 && npcId < npcNames.Count
                        ? GetMember(npcNames[npcId]!, "Name")?.ToString() ?? $"NPC #{npcId}"
                        : $"NPC #{npcId}";
                    var cards = new List<CardSourceCard>();
                    if (GetMember(gameNpc, "rewardCards") is IEnumerable rewardCards)
                    {
                        foreach (var reward in rewardCards)
                        {
                            var cardId = AsInt(reward);
                            var cardName = cardId >= 0 && cardId < cardNames.Count && cardNames[cardId] != null
                                ? GetMember(cardNames[cardId]!, "Name")?.ToString() ?? $"Card #{cardId}"
                                : $"Card #{cardId}";
                            cards.Add(new CardSourceCard(cardId, cardName));
                        }
                    }

                    result.Add(new CardSourceNpc(
                        AsInt(GetMember(gameNpc, "triadId")),
                        AsUInt(GetMember(gameNpc, "ENpcBaseId")),
                        name,
                        GetMember(gameNpc, "Location"),
                        cards));
                }

                sourceUnavailableReported = false;
                return result;
            }
            catch (Exception ex)
            {
                if (!sourceUnavailableReported)
                {
                    sourceUnavailableReported = true;
                    write(LogLevel.Error, $"Saucy GameNpcDB could not be read: {ex.Message}");
                }
                return [];
            }
        }
    }

    private object? GetSingleton(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Reverse())
        {
            var type = assembly.GetType(typeName, throwOnError: false);
            if (type == null)
                continue;
            return type.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
        return null;
    }

    private void ReportSourceUnavailable()
    {
        if (sourceUnavailableReported)
            return;
        sourceUnavailableReported = true;
        write(LogLevel.Verbose, "Saucy is not loaded or its Triple Triad data is not ready; CardSourceNPCs.All is empty.");
    }

    private static object? GetMember(object source, string name)
    {
        var type = source.GetType();
        return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source)
               ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
    }

    private static int AsInt(object? value) => value == null ? 0 : Convert.ToInt32(value);
    private static uint AsUInt(object? value) => value == null ? 0 : Convert.ToUInt32(value);

    public int? FindCardId(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return cardIdsByName.TryGetValue(NormalizeName(name), out var cardId) ? cardId : null;
    }

    private static Dictionary<string, int> LoadCardNames(IDataManager data)
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in new[] { ClientLanguage.English, ClientLanguage.German })
        {
            var sheet = data.GetExcelSheet<TripleTriadCard>(language);
            if (sheet == null)
                continue;
            foreach (var card in sheet)
            {
                var name = card.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.TryAdd(NormalizeName(name), (int)card.RowId);
            }
        }
        return names;
    }

    private static string NormalizeName(string name) => name.Trim();
}

/// <summary>JavaScript facade for Saucy's public runtime data and module configuration.</summary>
public sealed class SaucyApi(CardSourceNpcsApi cardSources)
{
    public IReadOnlyList<CardSourceNpc> npcs => cardSources.All;
    public SaucyCardIndex cards { get; } = new(cardSources);
    public SaucyMachineApi cuffacur { get; } = new("CuffACurModule");
    public SaucyMachineApi outonalimb { get; } = new("OutOnALimbModule");
    public SaucySliceIsRightApi sliceisright { get; } = new();
}

/// <summary>String-keyed card lookup. Names are resolved from both EN and DE sheets.</summary>
public sealed class SaucyCardIndex(CardSourceNpcsApi cardSources)
{
    public SaucyCardReference? this[string name]
    {
        get
        {
            var cardId = cardSources.FindCardId(name);
            if (!cardId.HasValue)
            {
                var match = cardSources.All.SelectMany(npc => npc.Cards, (npc, card) => (npc, card))
                    .FirstOrDefault(entry => string.Equals(entry.card.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
                cardId = match.card?.Id;
            }
            return cardId.HasValue ? new SaucyCardReference(cardSources, cardId.Value) : null;
        }
    }
}

public sealed class SaucyCardReference(CardSourceNpcsApi cardSources, int cardId)
{
    public int Id { get; } = cardId;
    public CardSourceNpc? npc => npcs.FirstOrDefault();
    public IReadOnlyList<CardSourceNpc> npcs => cardSources.All.Where(source => source.Cards.Any(card => card.Id == Id)).ToList();
}

public sealed class SaucyMachineApi(string moduleName)
{
    private readonly string arcadeRunSettingsName = moduleName == "CuffACurModule" ? "CuffArcadeRun" : "LimbArcadeRun";
    public SaucyFixedMatchCountApi fmc => new(arcadeRunSettingsName);

    /// <summary>
    /// Enables or disables Saucy's machine via its public configuration, then
    /// persists that setting on the next framework tick. Returns false when Saucy
    /// is unavailable, otherwise true once the request has been queued.
    /// </summary>
    public bool Toggle(bool enabled)
    {
        if (!SaucyRuntime.IsLoaded())
        {
            Plugin.Instance.Write(LogLevel.Error, $"Saucy {moduleName} toggle failed: Saucy configuration is not available.");
            return false;
        }
        Plugin.Instance.QueueMainThread(() => ToggleOnMainThread(enabled));
        return true;
    }

    public bool getStatus()
    {
        if (!SaucyRuntime.TryGetConfiguration(out var configuration))
            return false;
        try
        {
            var isEnabled = configuration.GetType().GetMethod("IsModuleEnabled", BindingFlags.Public | BindingFlags.Instance);
            return isEnabled?.Invoke(configuration, [moduleName]) as bool? ?? false;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Write(LogLevel.Error, $"Saucy {moduleName} status check failed: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>True only while Saucy exposes an active run state for this machine.</summary>
    public bool isRunning() => SaucyRuntime.GetMachineState(moduleName).Running;
    public bool hasRunState() => SaucyRuntime.GetMachineState(moduleName).Known;
    public string getRunState() => SaucyRuntime.GetMachineState(moduleName).Name;

    private void ToggleOnMainThread(bool enabled)
    {
        try
        {
            if (!SaucyRuntime.TryGetConfiguration(out var configuration, out var error))
            {
                Plugin.Instance.Write(LogLevel.Error, $"Saucy {moduleName} toggle failed: {error}");
                return;
            }

            var configurationType = configuration.GetType();
            var setEnabled = configurationType.GetMethod("SetModuleEnabled", BindingFlags.Public | BindingFlags.Instance, binder: null, types: [typeof(string), typeof(bool)], modifiers: null);
            if (setEnabled == null)
            {
                Plugin.Instance.Write(LogLevel.Error, $"Saucy {moduleName} toggle failed: {configurationType.FullName}.SetModuleEnabled(string, bool) was not found.");
                return;
            }

            setEnabled.Invoke(configuration, [moduleName, enabled]);
            SaucyRuntime.Save(configuration);
            Plugin.Instance.Write(LogLevel.Verbose, $"Saucy {moduleName} {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex) { Plugin.Instance.Write(LogLevel.Error, $"Saucy {moduleName} toggle failed: {ex.GetBaseException().Message}"); }
    }
}

public sealed class SaucyFixedMatchCountApi(string settingsPropertyName)
{
    public bool Toggle(bool enabled) => QueueUpdate(settings =>
    {
        settings.GetType().GetProperty("PlayXTimes", BindingFlags.Public | BindingFlags.Instance)?.SetValue(settings, enabled);
        if (enabled && ReadInt(settings, "MatchCount") <= 0)
            settings.GetType().GetProperty("MatchCount", BindingFlags.Public | BindingFlags.Instance)?.SetValue(settings, 1);
    });

    public bool Set(int count)
    {
        if (count <= 0 || !SaucyRuntime.IsLoaded())
        {
            if (count <= 0) Plugin.Instance.Write(LogLevel.Error, "Saucy fixed match count must be greater than zero.");
            else Plugin.Instance.Write(LogLevel.Error, "Saucy fixed match count failed: Saucy configuration is not available.");
            return false;
        }
        Plugin.Instance.QueueMainThread(() => UpdateOnMainThread(settings =>
            settings.GetType().GetProperty("MatchCount", BindingFlags.Public | BindingFlags.Instance)?.SetValue(settings, count)));
        return true;
    }

    private bool QueueUpdate(Action<object> update)
    {
        if (!SaucyRuntime.IsLoaded())
        {
            Plugin.Instance.Write(LogLevel.Error, "Saucy fixed match count failed: Saucy configuration is not available.");
            return false;
        }
        Plugin.Instance.QueueMainThread(() => UpdateOnMainThread(update));
        return true;
    }

    private void UpdateOnMainThread(Action<object> update)
    {
        if (!SaucyRuntime.TryGetConfiguration(out var configuration, out var error))
        {
            Plugin.Instance.Write(LogLevel.Error, $"Saucy fixed match count failed: {error}");
            return;
        }
        try
        {
            var settings = configuration.GetType().GetProperty(settingsPropertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(configuration);
            if (settings == null)
            {
                Plugin.Instance.Write(LogLevel.Error, $"Saucy fixed match count failed: {configuration.GetType().FullName}.{settingsPropertyName} was not found.");
                return;
            }
            update(settings);
            if (settingsPropertyName is "CuffArcadeRun" or "LimbArcadeRun")
                SyncActiveSession();
            SaucyRuntime.Save(configuration);
            Plugin.Instance.Write(LogLevel.Verbose, $"Saucy fixed match count updated for {settingsPropertyName}.");
        }
        catch (Exception ex) { Plugin.Instance.Write(LogLevel.Error, $"Saucy fixed match count failed: {ex.GetBaseException().Message}"); }
    }

    private static int ReadInt(object source, string property) =>
        source.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source) is int value ? value : 0;

    private void SyncActiveSession()
    {
        try
        {
            var saucyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .LastOrDefault(assembly => assembly.GetType("Saucy.GoldSaucerArcadeRunSession", false) != null);
            var sessionType = saucyAssembly?.GetType("Saucy.GoldSaucerArcadeRunSession", false);
            var machineType = saucyAssembly?.GetType("Saucy.GoldSaucerArcadeMachine", false);
            if (sessionType == null || machineType == null)
                return;
            var machineName = settingsPropertyName == "CuffArcadeRun" ? "Cuff" : "Limb";
            var machine = Enum.Parse(machineType, machineName);
            sessionType.GetMethod("SyncSessionCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, [machine]);
        }
        catch { }
    }
}

public sealed class SaucySliceIsRightApi
{
    public SaucyBooleanSettingApi automove { get; } = new("GoldSaucerGates", "SliceIsRightAutoMovement");
}

public sealed class SaucyBooleanSettingApi(string parentPropertyName, string propertyName)
{
    public bool Toggle(bool enabled)
    {
        if (!SaucyRuntime.IsLoaded())
        {
            Plugin.Instance.Write(LogLevel.Error, $"Saucy setting {propertyName} failed: Saucy configuration is not available.");
            return false;
        }
        Plugin.Instance.QueueMainThread(() =>
        {
            if (!SaucyRuntime.TryGetConfiguration(out var configuration, out var error))
            {
                Plugin.Instance.Write(LogLevel.Error, $"Saucy setting {propertyName} failed: {error}");
                return;
            }
            try
            {
                var parent = configuration.GetType().GetProperty(parentPropertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(configuration);
                var setting = parent?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (setting?.PropertyType != typeof(bool) || !setting.CanWrite)
                {
                    Plugin.Instance.Write(LogLevel.Error, $"Saucy setting {propertyName} failed: {parentPropertyName}.{propertyName} was not found or is not writable.");
                    return;
                }
                setting.SetValue(parent, enabled);
                SaucyRuntime.Save(configuration);
                Plugin.Instance.Write(LogLevel.Verbose, $"Saucy setting {propertyName} set to {enabled}.");
            }
            catch (Exception ex) { Plugin.Instance.Write(LogLevel.Error, $"Saucy setting {propertyName} failed: {ex.GetBaseException().Message}"); }
        });
        return true;
    }
}

internal static class SaucyRuntime
{
    private static readonly ConcurrentDictionary<string, MachineState> MachineStates = new(StringComparer.Ordinal);
    private static DateTime nextMachineStateRefresh;
    public static bool IsLoaded() => TryGetConfiguration(out _);

    public static bool TryGetConfiguration(out object configuration) => TryGetConfiguration(out configuration, out _);

    public static bool TryGetConfiguration(out object configuration, out string error)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Reverse())
        {
            var saucyType = assembly.GetType("Saucy.Saucy", throwOnError: false);
            if (saucyType == null)
                continue;

            var configurationProperty = saucyType.GetProperty("C", BindingFlags.Public | BindingFlags.Static);
            if (configurationProperty == null)
            {
                configuration = null!;
                error = "Saucy.Saucy.C was not found.";
                return false;
            }

            object? value;
            try { value = configurationProperty.GetValue(null); }
            catch (Exception ex)
            {
                configuration = null!;
                error = $"Saucy.Saucy.C could not be read: {ex.GetBaseException().Message}";
                return false;
            }
            if (value != null)
            {
                configuration = value;
                error = "";
                return true;
            }

            configuration = null!;
            error = "Saucy.Saucy.C is not initialized.";
            return false;
        }
        configuration = null!;
        error = "Saucy plugin assembly is not loaded.";
        return false;
    }

    public static void Save(object configuration) =>
        configuration.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance)?.Invoke(configuration, null);

    /// <summary>Must be called from Dalamud's framework thread; JavaScript only reads the cache.</summary>
    public static void RefreshMachineStates()
    {
        if (DateTime.UtcNow < nextMachineStateRefresh) return;
        nextMachineStateRefresh = DateTime.UtcNow.AddMilliseconds(250);
        RefreshMachineState("CuffACurModule");
        RefreshMachineState("OutOnALimbModule");
    }

    public static MachineState GetMachineState(string moduleName) =>
        MachineStates.TryGetValue(moduleName, out var state) ? state : MachineState.Unknown;

    private static void RefreshMachineState(string moduleName)
    {
        try
        {
            var module = FindModule(moduleName);
            if (module == null) { MachineStates[moduleName] = MachineState.Unknown; return; }
            var rawState = GetMember(module, "State");
            if (rawState == null) { MachineStates[moduleName] = MachineState.Unknown; return; }
            var name = rawState.ToString() ?? "Unknown";
            var running = rawState is bool value ? value : !IsIdleState(name);
            MachineStates[moduleName] = new MachineState(true, running, name);
        }
        catch { MachineStates[moduleName] = MachineState.Unknown; }
    }

    private static bool IsIdleState(string value) => value.Equals("None", StringComparison.OrdinalIgnoreCase) || value.Equals("Idle", StringComparison.OrdinalIgnoreCase) || value.Equals("Inactive", StringComparison.OrdinalIgnoreCase) || value.Equals("Stopped", StringComparison.OrdinalIgnoreCase) || value.Equals("Complete", StringComparison.OrdinalIgnoreCase) || value.Equals("Completed", StringComparison.OrdinalIgnoreCase) || value.Equals("Finished", StringComparison.OrdinalIgnoreCase);

    private static object? FindModule(string moduleName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Reverse())
        {
            var saucyType = assembly.GetType("Saucy.Saucy", throwOnError: false);
            if (saucyType == null) continue;
            foreach (var property in saucyType.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                var found = FindModuleIn(property.GetValue(null), moduleName, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                if (found != null) return found;
            }
            foreach (var field in saucyType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var found = FindModuleIn(field.GetValue(null), moduleName, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                if (found != null) return found;
            }
        }
        return null;
    }

    private static object? FindModuleIn(object? value, string moduleName, int depth, HashSet<object> visited)
    {
        if (value == null || depth > 2 || !visited.Add(value)) return null;
        if (value.GetType().Name.Equals(moduleName, StringComparison.Ordinal)) return value;
        if (value is IEnumerable values)
            foreach (var item in values) { var found = FindModuleIn(item, moduleName, depth + 1, visited); if (found != null) return found; }
        foreach (var memberName in new[] { "Modules", "ModuleManager", "M" })
        {
            var found = FindModuleIn(GetMember(value, memberName), moduleName, depth + 1, visited);
            if (found != null) return found;
        }
        return null;
    }

    private static object? GetMember(object source, string name) => source.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source) ?? source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
}

public readonly record struct MachineState(bool Known, bool Running, string Name)
{
    public static MachineState Unknown { get; } = new(false, false, "Unknown");
}

public sealed class CardSourceNpc(int triadId, uint enpcBaseId, string name, object? location, IReadOnlyList<CardSourceCard> cards)
{
    public int TriadId { get; } = triadId;
    public uint ENpcBaseId { get; } = enpcBaseId;
    public string Name { get; } = name;

    /// <summary>Saucy's public MapLinkPayload, or null when Saucy has no map location.</summary>
    public object? Location { get; } = location;
    public object? location => Location;
    public IReadOnlyList<CardSourceCard> Cards { get; } = cards;
    public IReadOnlyList<CardSourceCard> cards => Cards;
}

public sealed class CardSourceCard(int id, string name)
{
    public int Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>Reads the game collection only when the script explicitly asks.</summary>
    public unsafe bool Obtained()
    {
        if (Id is <= 0 or > ushort.MaxValue)
            return false;

        try
        {
            var uiState = UIState.Instance();
            return uiState != null && uiState->IsTripleTriadCardUnlocked((ushort)Id);
        }
        catch
        {
            return false;
        }
    }

    public bool obtained() => Obtained();
}
