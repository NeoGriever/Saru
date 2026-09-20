using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;

namespace Saru;

public sealed class InfoTracker
{
    private readonly Dictionary<ulong, InfoObject> objects = new();

    public bool IsActive { get; private set; }
    public IReadOnlyCollection<InfoObject> Objects => objects.Values;

    public void SetActive(bool active) => IsActive = active;

    public void Update()
    {
        if (!IsActive) return;
        var targetId = Plugin.TargetManager.Target?.GameObjectId ?? 0;
        foreach (var obj in Plugin.ObjectTable) UpdateObject(obj, targetId);
    }

    public string Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Saru-Information-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var export = objects.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).Select(value => new
        {
            value.Key, value.Name, value.Kind, value.EntityId, value.BaseId, value.Targetable, value.Targeted,
            Position = new { x = value.Position.X, y = value.Position.Y, z = value.Position.Z }
        });
        File.WriteAllText(path, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private void UpdateObject(IGameObject obj, ulong targetId)
    {
        var key = obj.GameObjectId != 0 ? obj.GameObjectId : unchecked((ulong)obj.Address);
        if (!objects.TryGetValue(key, out var state)) objects[key] = state = new InfoObject(key);
        state.Update(obj, obj.GameObjectId != 0 && obj.GameObjectId == targetId);
    }

    public sealed class InfoObject(ulong key)
    {
        public ulong Key { get; } = key;
        public string Name { get; private set; } = "";
        public string Kind { get; private set; } = "";
        public uint EntityId { get; private set; }
        public uint BaseId { get; private set; }
        public bool Targetable { get; private set; }
        public bool Targeted { get; private set; }
        public Vector3 Position { get; private set; }

        public void Update(IGameObject obj, bool targeted)
        {
            Name = obj.Name.TextValue;
            Kind = obj.ObjectKind.ToString();
            EntityId = obj.EntityId;
            BaseId = obj.BaseId;
            Targetable = obj.IsTargetable;
            Targeted = targeted;
            Position = obj.Position;
        }
    }
}
