using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Saru;

public sealed record ScriptConfigScanResult(IReadOnlyList<ScriptConfigEntry> Entries, string Error);

public static class ScriptConfiguration
{
    public static ScriptConfigScanResult Scan(string source, IReadOnlyList<ScriptConfigEntry>? currentValues)
    {
        var api = new ScriptConfigApi(currentValues, collectDefinitions: true);
        try
        {
            var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(2)));
            engine.SetValue("saru", api);
            engine.SetValue("addEventListener", new Action<string, JsValue>((_, _) => { }));
            engine.SetValue("setTimeout", new Func<JsValue, double, int>((_, _) => 0));
            engine.SetValue("setInterval", new Func<JsValue, double, int>((_, _) => 0));
            engine.SetValue("clearTimeout", new Action<JsValue>(_ => { }));
            engine.SetValue("clearInterval", new Action<JsValue>(_ => { }));
            engine.Execute("const FFEV = Object.freeze({ message: 'message', dialog: 'dialog', arrived: 'arrived', time: 'time', reach: 'reach', onMapChange: 'mapChange', onZoneChanged: 'zoneChanged', onZoneChangeStart: 'zoneChangeStart', onDutyEnd: 'dutyEnd', onEventDone: 'eventDone' });");
            engine.Execute(source);
            return new ScriptConfigScanResult(api.Definitions, "");
        }
        catch (Exception ex)
        {
            return new ScriptConfigScanResult([], ex.Message);
        }
    }

    public static bool Refresh(ScriptEntry script)
    {
        var result = Scan(script.Source, script.Config);
        if (!string.IsNullOrEmpty(result.Error))
        {
            script.ConfigError = result.Error;
            return false;
        }

        script.Config = result.Entries.Select(Clone).ToList();
        script.ConfigError = "";
        return true;
    }

    public static ScriptConfigApi CreateRuntimeApi(IReadOnlyList<ScriptConfigEntry>? values) => new(values, collectDefinitions: false);

    public static ScriptConfigEntry Clone(ScriptConfigEntry value) => new()
    {
        Key = value.Key,
        Label = value.Label,
        Type = value.Type,
        Options = [.. value.Options],
        BoolValue = value.BoolValue,
        ComboValue = value.ComboValue,
        TextValue = value.TextValue,
        NumberMinimum = value.NumberMinimum,
        NumberMaximum = value.NumberMaximum,
        NumberValue = value.NumberValue,
    };

    public static void Normalize(ScriptConfigEntry value)
    {
        switch (value.Type)
        {
            case ScriptConfigType.Combo:
                value.ComboValue = value.Options.Count == 0 ? 0 : Math.Clamp(value.ComboValue, 0, value.Options.Count - 1);
                break;
            case ScriptConfigType.Number:
                if (value.NumberMaximum < value.NumberMinimum) (value.NumberMinimum, value.NumberMaximum) = (value.NumberMaximum, value.NumberMinimum);
                value.NumberValue = Math.Clamp(value.NumberValue, value.NumberMinimum, value.NumberMaximum);
                break;
        }
    }
}

public sealed class ScriptConfigApi
{
    private readonly IReadOnlyDictionary<string, ScriptConfigEntry> currentValues;
    private readonly bool collectDefinitions;
    private readonly Dictionary<string, ScriptConfigEntry> definitionsByKey = new(StringComparer.Ordinal);
    public ConfigTypeApi configType { get; } = new();
    public IReadOnlyList<ScriptConfigEntry> Definitions => definitionsByKey.Values.ToList();

    public ScriptConfigApi(IReadOnlyList<ScriptConfigEntry>? values, bool collectDefinitions)
    {
        currentValues = (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value.Key)).ToDictionary(value => value.Key, StringComparer.Ordinal);
        this.collectDefinitions = collectDefinitions;
    }

    public object SetConfig(string variable, string label, int typeValue, JsValue options, JsValue defaultValue)
    {
        if (!Enum.IsDefined(typeof(ScriptConfigType), typeValue)) throw new ArgumentException("Unknown saru.configType value.");
        if (string.IsNullOrWhiteSpace(variable)) throw new ArgumentException("The configuration variable name must not be empty.");
        var type = (ScriptConfigType)typeValue;
        var definition = CreateDefinition(variable, label, type, options, defaultValue);
        if (collectDefinitions)
        {
            if (!definitionsByKey.TryAdd(variable, definition)) throw new ArgumentException($"Configuration variable '{variable}' was declared more than once.");
        }
        if (currentValues.TryGetValue(variable, out var saved) && saved.Type == type)
        {
            CopyValue(saved, definition);
            ScriptConfiguration.Normalize(definition);
        }
        return ValueOf(definition);
    }

    public object SetConfig(string variable, string label, int typeValue, JsValue defaultValue) => SetConfig(variable, label, typeValue, JsValue.Undefined, defaultValue);

    private static ScriptConfigEntry CreateDefinition(string key, string label, ScriptConfigType type, JsValue options, JsValue defaultValue)
    {
        var entry = new ScriptConfigEntry { Key = key, Label = string.IsNullOrWhiteSpace(label) ? key : label, Type = type };
        switch (type)
        {
            case ScriptConfigType.Checkbox:
                entry.BoolValue = defaultValue.IsBoolean() && defaultValue.AsBoolean();
                break;
            case ScriptConfigType.Combo:
                entry.Options = ReadValues(options).Select(ValueAsText).ToList();
                entry.ComboValue = defaultValue.IsNumber() ? (int)defaultValue.AsNumber() : 0;
                break;
            case ScriptConfigType.Input:
                entry.TextValue = defaultValue.IsUndefined() || defaultValue.IsNull() ? "" : ValueAsText(defaultValue);
                break;
            case ScriptConfigType.Number:
                var range = ReadValues(options);
                if (range.Count != 2 || !range[0].IsNumber() || !range[1].IsNumber()) throw new ArgumentException($"Number configuration '{key}' requires [minimum, maximum].");
                entry.NumberMinimum = (float)range[0].AsNumber();
                entry.NumberMaximum = (float)range[1].AsNumber();
                entry.NumberValue = defaultValue.IsNumber() ? (float)defaultValue.AsNumber() : entry.NumberMinimum;
                break;
        }
        ScriptConfiguration.Normalize(entry);
        return entry;
    }

    private static List<JsValue> ReadValues(JsValue value)
    {
        if (!value.IsObject()) return [];
        var array = value.AsObject();
        var length = array.Get("length");
        if (!length.IsNumber()) return [];
        var result = new List<JsValue>();
        for (var i = 0; i < Math.Max(0, (int)length.AsNumber()); i++) result.Add(array.Get(i.ToString(CultureInfo.InvariantCulture)));
        return result;
    }

    private static string ValueAsText(JsValue value) => value.IsString() ? value.AsString() : value.ToString();
    private static object ValueOf(ScriptConfigEntry value) => value.Type switch
    {
        ScriptConfigType.Checkbox => value.BoolValue,
        ScriptConfigType.Combo => value.ComboValue,
        ScriptConfigType.Input => value.TextValue,
        ScriptConfigType.Number => value.NumberValue,
        _ => throw new InvalidOperationException(),
    };
    private static void CopyValue(ScriptConfigEntry source, ScriptConfigEntry destination)
    {
        switch (destination.Type)
        {
            case ScriptConfigType.Checkbox: destination.BoolValue = source.BoolValue; break;
            case ScriptConfigType.Combo: destination.ComboValue = source.ComboValue; break;
            case ScriptConfigType.Input: destination.TextValue = source.TextValue; break;
            case ScriptConfigType.Number: destination.NumberValue = source.NumberValue; break;
        }
    }

    public sealed class ConfigTypeApi
    {
        public int checkbox => (int)ScriptConfigType.Checkbox;
        public int combo => (int)ScriptConfigType.Combo;
        public int input => (int)ScriptConfigType.Input;
        public int number => (int)ScriptConfigType.Number;
    }
}
