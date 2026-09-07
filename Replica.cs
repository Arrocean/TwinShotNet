using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace TwinShotNet;

public sealed class Replica
{
    private sealed class Visual
    {
        public GameObject GameObject;
        public SpriteRenderer Renderer;
        public Vector3 TargetPosition;
        public Vector3 TargetScale;
        public float TargetRotation;
        public int Seen;
    }

    private struct State
    {
        public int Id;
        public string Sprite;
        public Vector3 Position;
        public Vector3 Scale;
        public float Rotation;
        public Color32 Color;
        public string Layer;
        public int Order;
        public bool FlipX;
        public bool FlipY;
    }

    private readonly Dictionary<int, Visual> visuals = new Dictionary<int, Visual>();
    private Dictionary<string, Sprite> sprites;
    private readonly Dictionary<Sprite, string> spriteIds = new Dictionary<Sprite, string>();
    private Assets indexedAssets;
    private int generation;
    private int currentLevel = -1;
    public int MissingSprites { get; private set; }
    public string Hud { get; private set; } = "Waiting for state";

    public byte[] Capture()
    {
        if (sprites == null || indexedAssets != Game.instance.assets) BuildSpriteIndex();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Game.levelId.uniqueLevelNumber);
        Camera camera = Camera.main;
        writer.Write(camera != null ? camera.transform.position.x : 0);
        writer.Write(camera != null ? camera.transform.position.y : 0);
        writer.Write(camera != null ? camera.orthographicSize : 300);

        Player[] players = Game.instance.level.players;
        writer.Write((byte)players.Length);
        foreach (Player player in players)
        {
            writer.Write(player.number); writer.Write(player.hits); writer.Write(player.score);
            writer.Write(player.alive); writer.Write((byte)player.powerup);
        }

        SpriteRenderer[] found = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);
        var renderers = new List<SpriteRenderer>(found.Length);
        foreach (SpriteRenderer renderer in found)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sprite == null) continue;
            if (renderer.gameObject.name.StartsWith("TwinShotNet Replica ", StringComparison.Ordinal)) continue;
            renderers.Add(renderer);
            if (renderers.Count >= 10000) break;
        }
        writer.Write(renderers.Count);
        foreach (SpriteRenderer renderer in renderers)
        {
            Transform transform = renderer.transform;
            writer.Write(renderer.GetInstanceID());
            writer.Write(spriteIds.TryGetValue(renderer.sprite, out string key) ? key : "missing");
            writer.Write(transform.position.x); writer.Write(transform.position.y); writer.Write(transform.position.z);
            Vector3 scale = transform.lossyScale;
            writer.Write(scale.x); writer.Write(scale.y); writer.Write(scale.z);
            writer.Write(transform.eulerAngles.z);
            Color32 color = renderer.color;
            writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a);
            writer.Write(renderer.sortingLayerName ?? "Default"); writer.Write(renderer.sortingOrder);
            writer.Write(renderer.flipX); writer.Write(renderer.flipY);
        }
        return stream.ToArray();
    }

    public void InitializeClient()
    {
        BuildSpriteIndex();
        HideLocalDynamicRenderers();
        currentLevel = Game.levelId.uniqueLevelNumber;
    }

    public void Apply(byte[] raw)
    {
        using var stream = new MemoryStream(raw, false);
        using var reader = new BinaryReader(stream);
        int level = reader.ReadInt32();
        float cameraX = reader.ReadSingle();
        float cameraY = reader.ReadSingle();
        float cameraSize = reader.ReadSingle();
        if (level != currentLevel)
        {
            LevelId id = LevelId.ByUniqueNumber(level);
            if (!id.DoesExist()) throw new InvalidDataException("Host selected an unavailable level");
            Game.instance.LoadAndStartLevel(id, null, false);
            currentLevel = level;
            BuildSpriteIndex();
            HideLocalDynamicRenderers();
        }
        Camera camera = Camera.main;
        if (camera != null)
        {
            Vector3 position = camera.transform.position;
            position.x = cameraX; position.y = cameraY;
            camera.transform.position = position;
            camera.orthographicSize = cameraSize;
        }

        int playerCount = reader.ReadByte();
        if (playerCount < 1 || playerCount > 4) throw new InvalidDataException("Invalid player count");
        var hud = new List<string>(playerCount);
        for (int i = 0; i < playerCount; i++)
        {
            int number = reader.ReadInt32(); int hits = reader.ReadInt32(); int score = reader.ReadInt32();
            bool alive = reader.ReadBoolean(); byte powerup = reader.ReadByte();
            Player local = Game.instance.level.players.FirstOrDefault(p => p.number == number);
            if (local != null)
            {
                local.hits = hits; local.score = score; local.alive = alive;
                local.powerup = (Player.PowerupType)powerup;
                Game.instance.ui.Player(local).score.Advance(1f);
            }
            hud.Add($"P{number} {(alive ? hits + " HP" : "OUT")} {score} pts" + (powerup == 0 ? "" : $" power {powerup}"));
        }
        Hud = string.Join("   ", hud);
        int count = reader.ReadInt32();
        if (count < 0 || count > 10000) throw new InvalidDataException("Invalid visual count");
        generation++;
        MissingSprites = 0;
        for (int i = 0; i < count; i++) Apply(ReadState(reader));
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing snapshot data");
        var stale = visuals.Where(pair => pair.Value.Seen != generation).Select(pair => pair.Key).ToArray();
        foreach (int id in stale)
        {
            UnityEngine.Object.Destroy(visuals[id].GameObject);
            visuals.Remove(id);
        }
    }

    private void Apply(State state)
    {
        if (!visuals.TryGetValue(state.Id, out Visual visual))
        {
            var gameObject = new GameObject("TwinShotNet Replica " + state.Id);
            visual = new Visual { GameObject = gameObject, Renderer = gameObject.AddComponent<SpriteRenderer>(), TargetPosition = state.Position, TargetScale = state.Scale };
            gameObject.transform.position = state.Position;
            gameObject.transform.localScale = state.Scale;
            visuals.Add(state.Id, visual);
        }
        visual.Seen = generation;
        visual.TargetPosition = state.Position;
        visual.TargetScale = state.Scale;
        visual.TargetRotation = state.Rotation;
        if (sprites != null && sprites.TryGetValue(state.Sprite, out Sprite sprite)) visual.Renderer.sprite = sprite;
        else { visual.Renderer.sprite = null; MissingSprites++; }
        visual.Renderer.color = state.Color;
        visual.Renderer.sortingLayerName = state.Layer;
        visual.Renderer.sortingOrder = state.Order;
        visual.Renderer.flipX = state.FlipX; visual.Renderer.flipY = state.FlipY;
    }

    public void Render()
    {
        // Animation components can re-enable original renderers after initial scene setup.
        HideLocalDynamicRenderers();
        float amount = 1 - Mathf.Exp(-Time.unscaledDeltaTime * 28);
        foreach (Visual visual in visuals.Values)
        {
            Transform transform = visual.GameObject.transform;
            transform.position = Vector3.Lerp(transform.position, visual.TargetPosition, amount);
            transform.localScale = Vector3.Lerp(transform.localScale, visual.TargetScale, amount);
            Vector3 angles = transform.eulerAngles;
            angles.z = Mathf.LerpAngle(angles.z, visual.TargetRotation, amount);
            transform.eulerAngles = angles;
        }
    }

    public void Clear()
    {
        foreach (Visual visual in visuals.Values) if (visual.GameObject != null) UnityEngine.Object.Destroy(visual.GameObject);
        visuals.Clear(); sprites = null; spriteIds.Clear(); indexedAssets = null; generation = 0; currentLevel = -1; MissingSprites = 0; Hud = "Waiting for state";
    }

    private static State ReadState(BinaryReader reader)
    {
        var state = new State
        {
            Id = reader.ReadInt32(), Sprite = reader.ReadString(),
            Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Rotation = reader.ReadSingle(),
            Color = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()),
            Layer = reader.ReadString(), Order = reader.ReadInt32(), FlipX = reader.ReadBoolean(), FlipY = reader.ReadBoolean()
        };
        if (state.Sprite.Length > 512 || state.Layer.Length > 128 || !Finite(state.Position.x) || !Finite(state.Position.y) || !Finite(state.Scale.x) || !Finite(state.Scale.y))
            throw new InvalidDataException("Invalid visual state");
        return state;
    }

    private void BuildSpriteIndex()
    {
        sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        spriteIds.Clear();
        indexedAssets = Game.instance.assets;
        var visited = new HashSet<object>();
        Visit(indexedAssets, "Assets", visited, 0);
        // Only accept unambiguous fallback names; never silently overwrite a different sprite.
        foreach (var group in Resources.FindObjectsOfTypeAll<Sprite>().Where(s => s != null).GroupBy(SpriteKey))
        {
            if (group.Count() != 1) continue;
            Sprite sprite = group.First();
            if (!spriteIds.ContainsKey(sprite)) Register(sprite, "fallback/" + group.Key);
        }
        UnityEngine.Debug.Log($"[TwinShotNet] Indexed {spriteIds.Count} sprites from serialized asset references.");
    }

    private void Register(Sprite sprite, string path)
    {
        if (spriteIds.ContainsKey(sprite)) return;
        using var hash = SHA256.Create();
        string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
        spriteIds[sprite] = key; sprites[key] = sprite;
    }

    private void Visit(object value, string path, HashSet<object> visited, int depth)
    {
        if (value == null || depth > 32) return;
        if (value is Sprite sprite) { Register(sprite, path); return; }
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || !visited.Add(value)) return;
        if (value is GameObject go)
        {
            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
                if (components[i] != null && components[i].GetType().Assembly == typeof(Game).Assembly)
                    Visit(components[i], path + "/component/" + i, visited, depth + 1);
            for (int i = 0; i < go.transform.childCount; i++)
                Visit(go.transform.GetChild(i).gameObject, path + "/child/" + i, visited, depth + 1);
            return;
        }
        if (value is System.Collections.IEnumerable sequence)
        {
            int i = 0;
            foreach (object element in sequence) Visit(element, path + "/" + i++, visited, depth + 1);
            return;
        }
        if (type.Assembly != typeof(Game).Assembly) return;
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (field.IsNotSerialized || (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false))) continue;
            Visit(field.GetValue(value), path + "/" + field.Name, visited, depth + 1);
        }
    }

    private static string SpriteKey(Sprite sprite)
    {
        Rect rect = sprite.rect;
        return (sprite.texture != null ? sprite.texture.name : "") + "|" + sprite.name + "|" + (int)rect.x + "," + (int)rect.y + "," + (int)rect.width + "," + (int)rect.height;
    }

    private static bool IsStaticTile(Transform transform)
    {
        Level level = Game.instance != null ? Game.instance.level : null;
        return level != null && level.tileContainer != null && transform.IsChildOf(level.tileContainer.transform);
    }

    private static void HideLocalDynamicRenderers()
    {
        foreach (SpriteRenderer renderer in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
            if (renderer != null && !renderer.gameObject.name.StartsWith("TwinShotNet Replica ", StringComparison.Ordinal))
                renderer.enabled = false;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < 1000000;
}
