using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Data.Files;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Saru;

public sealed class InfoTracker
{
    private readonly Dictionary<ulong, InfoObject> objects = new();
    private readonly HashSet<ulong> reportedUpdateFailures = new();

    public bool IsActive { get; private set; }
    public IReadOnlyCollection<InfoObject> Objects => objects.Values;

    public void SetActive(bool active) => IsActive = active;

    public void Update(uint mapId, uint territoryId, uint instance)
    {
        if (!IsActive) return;
        var targetId = Plugin.TargetManager.Target?.GameObjectId ?? 0;
        foreach (var obj in Plugin.ObjectTable)
        {
            var key = unchecked((ulong)obj.Address);
            try
            {
                if (obj.GameObjectId != 0) key = obj.GameObjectId;
                UpdateObject(obj, targetId, mapId, territoryId, instance);
                reportedUpdateFailures.Remove(key);
            }
            catch (Exception ex)
            {
                if (reportedUpdateFailures.Add(key)) Plugin.Log.Warning($"Could not collect information for object {key}: {ex}");
            }
        }
    }

    public string Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Saru-Information-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        RefreshModelPaths();
        var export = objects.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).Select(value => new
        {
            value.Key, value.GameObjectId, value.Name, value.Kind, value.SubKind,
            value.EntityId, value.BaseId, value.OwnerId, value.ObjectIndex,
            value.Targetable, value.Targeted, value.IsDead, value.HitboxRadius, value.TargetObjectId,
            value.Rotation, value.NameId, value.Level, value.ClassJobId,
            value.CurrentHp, value.MaxHp, value.CurrentMp, value.MaxMp,
            value.CurrentGp, value.MaxGp, value.CurrentCp, value.MaxCp,
            value.StatusFlags,
            value.ModelPaths, value.LastSeenUtc,
            Position = new { x = value.Position.X, y = value.Position.Y, z = value.Position.Z, mapId = value.MapId, territoryId = value.TerritoryId, instance = value.Instance }
        });
        File.WriteAllText(path, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public int ExportModels(InfoObject item, string directory)
    {
        if (!item.Kind.Equals("BattleNpc", StringComparison.Ordinal)) return 0;
        RefreshModelPaths();
        if (item.ModelPaths.Count == 0) return 0;
        var modelDirectory = Path.Combine(directory, "Models", item.Key.ToString());
        var count = 0;
        foreach (var model in item.ModelPaths)
        {
            try
            {
                var file = Plugin.DataManager.GetFile<MdlFile>(model.Path);
                if (file == null) continue;
                Directory.CreateDirectory(modelDirectory);
                file.SaveFile(Path.Combine(modelDirectory, $"{model.Slot:D2}-{Path.GetFileName(model.Path.Replace('\\', '/'))}"));
                count++;
            }
            catch (Exception ex) { Plugin.Log.Warning($"Could not export model '{model.Path}': {ex.Message}"); }
        }
        return count;
    }

    private void RefreshModelPaths()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleNpc) continue;
            var key = obj.GameObjectId != 0 ? obj.GameObjectId : unchecked((ulong)obj.Address);
            if (!objects.TryGetValue(key, out var item)) continue;
            try { item.ModelPaths = ResolveModelPaths(obj); }
            catch (Exception ex) { Plugin.Log.Warning($"Could not resolve model paths for object {key}: {ex.Message}"); }
        }
    }

    private static unsafe List<ModelPath> ResolveModelPaths(IGameObject obj)
    {
        var result = new List<ModelPath>();
        if (obj is not IBattleNpc || obj.Address == nint.Zero) return result;
        var gameObject = (ClientGameObject*)obj.Address;
        if (gameObject->DrawObject == null) return result;
        var model = gameObject->GetCharacterBase();
        if (model == null || model->SlotCount is < 1 or > 32) return result;
        for (var slot = 0; slot < model->SlotCount; slot++)
        {
            var path = model->ResolveMdlPath((uint)slot);
            if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                result.Add(new ModelPath(slot, path));
        }
        return result;
    }

    private void UpdateObject(IGameObject obj, ulong targetId, uint mapId, uint territoryId, uint instance)
    {
        var key = obj.GameObjectId != 0 ? obj.GameObjectId : unchecked((ulong)obj.Address);
        if (!objects.TryGetValue(key, out var state)) objects[key] = state = new InfoObject(key);
        state.Update(obj, obj.GameObjectId != 0 && obj.GameObjectId == targetId, mapId, territoryId, instance);
    }

    public sealed class InfoObject(ulong key)
    {
        public ulong Key { get; } = key;
        public string Name { get; private set; } = "";
        public string Kind { get; private set; } = "";
        public ulong GameObjectId { get; private set; }
        public byte SubKind { get; private set; }
        public uint EntityId { get; private set; }
        public uint BaseId { get; private set; }
        public uint OwnerId { get; private set; }
        public ushort ObjectIndex { get; private set; }
        public bool Targetable { get; private set; }
        public bool Targeted { get; private set; }
        public bool IsDead { get; private set; }
        public float HitboxRadius { get; private set; }
        public ulong TargetObjectId { get; private set; }
        public Vector3 Position { get; private set; }
        public float Rotation { get; private set; }
        public uint MapId { get; private set; }
        public uint TerritoryId { get; private set; }
        public uint Instance { get; private set; }
        public DateTime LastSeenUtc { get; private set; }
        public uint? NameId { get; private set; }
        public byte? Level { get; private set; }
        public uint? ClassJobId { get; private set; }
        public uint? CurrentHp { get; private set; }
        public uint? MaxHp { get; private set; }
        public uint? CurrentMp { get; private set; }
        public uint? MaxMp { get; private set; }
        public uint? CurrentGp { get; private set; }
        public uint? MaxGp { get; private set; }
        public uint? CurrentCp { get; private set; }
        public uint? MaxCp { get; private set; }
        public string? StatusFlags { get; private set; }
        public List<ModelPath> ModelPaths { get; internal set; } = new();

        public void Update(IGameObject obj, bool targeted, uint mapId, uint territoryId, uint instance)
        {
            if (Kind.Length > 0 && (BaseId != obj.BaseId || Kind != obj.ObjectKind.ToString())) ModelPaths.Clear();
            GameObjectId = obj.GameObjectId;
            Name = obj.Name.TextValue;
            Kind = obj.ObjectKind.ToString();
            SubKind = obj.SubKind;
            EntityId = obj.EntityId;
            BaseId = obj.BaseId;
            OwnerId = obj.OwnerId;
            ObjectIndex = obj.ObjectIndex;
            Targetable = obj.IsTargetable;
            Targeted = targeted;
            IsDead = obj.IsDead;
            HitboxRadius = obj.HitboxRadius;
            TargetObjectId = obj.TargetObjectId;
            Position = obj.Position;
            Rotation = obj.Rotation;
            MapId = mapId;
            TerritoryId = territoryId;
            Instance = instance;
            LastSeenUtc = DateTime.UtcNow;
            if (obj is ICharacter character)
            {
                NameId = character.NameId;
                Level = character.Level;
                ClassJobId = character.ClassJob.RowId;
                CurrentHp = character.CurrentHp;
                MaxHp = character.MaxHp;
                CurrentMp = character.CurrentMp;
                MaxMp = character.MaxMp;
                CurrentGp = character.CurrentGp;
                MaxGp = character.MaxGp;
                CurrentCp = character.CurrentCp;
                MaxCp = character.MaxCp;
                StatusFlags = character.StatusFlags.ToString();
            }
            else
            {
                NameId = null; Level = null; ClassJobId = null;
                CurrentHp = null; MaxHp = null; CurrentMp = null; MaxMp = null;
                CurrentGp = null; MaxGp = null; CurrentCp = null; MaxCp = null;
                StatusFlags = null;
            }
        }
    }

    public sealed record ModelPath(int Slot, string Path);
}
