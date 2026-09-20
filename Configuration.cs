using System;
using System.Collections.Generic;
using Dalamud.Configuration;
namespace Saru;
[Serializable]
public sealed class ScriptEntry
{
    public Guid Id = Guid.NewGuid();
    public string Name = "Untitled";
    public string FileName = "";
    public string ContentHash = "";
    [NonSerialized] public string Source = "";
    [NonSerialized] public int Revision;
    [NonSerialized] public bool MissingFileReported;
}
[Serializable]
public sealed class InstalledRemoteScript
{
    public string Name = "";
    public string FileName = "";
    public string VersionSha = "";
    public DateTime VersionTimestamp;
}
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public List<ScriptEntry> Scripts = new();
    public List<InstalledRemoteScript> InstalledRemoteScripts = new();
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
    public static Configuration Load() => Plugin.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
}
public enum LogLevel { Error, Verbose, Dev }
