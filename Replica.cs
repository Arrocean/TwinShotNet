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

    private readonly Dictionary<int, Visual> visuals = new Dictionary<int, Visual>();
    private readonly Dictionary<SpriteRenderer, bool> hiddenRenderers = new Dictionary<SpriteRenderer, bool>();
    private Dictionary<string, Sprite> sprites;
    private readonly Dictionary<Sprite, string> spriteIds = new Dictionary<Sprite, string>();
    private Assets indexedAssets;
    private int generation;
    private int currentLevel = -1;
    private readonly Dictionary<int, int> displayedScores = new Dictionary<int, int>();
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
            if (renderers.Count >= SnapshotCodec.MaxVisuals) break;
        }
        writer.Write(renderers.Count);
        foreach (SpriteRenderer renderer in renderers)
        {
            Transform transform = renderer.transform;
            writer.Write(StableVisualId(renderer));
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
        Snapshot snapshot = SnapshotCodec.Decode(raw);
        int level = snapshot.Level;
        if (level != currentLevel)
        {
            LevelId id = LevelId.ByUniqueNumber(level);
            if (!id.DoesExist()) throw new InvalidDataException("Host selected an unavailable level");
            ClearVisuals();
            RestoreLocalRenderers();
            Game.instance.LoadAndStartLevel(id, null, false);
            currentLevel = level;
            BuildSpriteIndex();
            HideLocalDynamicRenderers();
        }
        Camera camera = Camera.main;
        if (camera != null)
        {
            Vector3 position = camera.transform.position;
            position.x = snapshot.CameraX; position.y = snapshot.CameraY;
            camera.transform.position = position;
            camera.orthographicSize = snapshot.CameraSize;
        }

        var hud = new List<string>(snapshot.Players.Length);
        foreach (SnapshotPlayer player in snapshot.Players)
        {
            int number = player.Number; int hits = player.Hits; int score = player.Score;
            bool alive = player.Alive; byte powerup = player.Powerup;
            Player local = Game.instance.level.players.FirstOrDefault(p => p.number == number);
            if (local != null)
            {
                local.hits = hits; local.score = score; local.alive = alive;
                local.powerup = (Player.PowerupType)powerup;
                UpdateScoreUi(number, local.score, score);
            }
            hud.Add($"P{number} {(alive ? hits + " HP" : "OUT")} {score} pts" + (powerup == 0 ? "" : $" power {powerup}"));
        }
        Hud = string.Join("   ", hud);
        generation++;
        MissingSprites = 0;
        foreach (SnapshotVisual visual in snapshot.Visuals) Apply(visual);
        var stale = visuals.Where(pair => pair.Value.Seen != generation).Select(pair => pair.Key).ToArray();
        foreach (int id in stale)
        {
            if (visuals[id].GameObject != null) UnityEngine.Object.Destroy(visuals[id].GameObject);
            visuals.Remove(id);
        }
    }

    private void Apply(SnapshotVisual state)
    {
        var position = new Vector3(state.X, state.Y, state.Z);
        var scale = new Vector3(state.ScaleX, state.ScaleY, state.ScaleZ);
        if (!visuals.TryGetValue(state.Id, out Visual visual) || visual.GameObject == null || visual.Renderer == null)
        {
            if (visual != null && visual.GameObject != null) UnityEngine.Object.Destroy(visual.GameObject);
            var gameObject = new GameObject("TwinShotNet Replica " + state.Id);
            visual = new Visual { GameObject = gameObject, Renderer = gameObject.AddComponent<SpriteRenderer>() };
            gameObject.transform.position = position;
            gameObject.transform.localScale = scale;
            gameObject.transform.eulerAngles = new Vector3(0, 0, state.Rotation);
            visuals[state.Id] = visual;
        }
        visual.Seen = generation;
        visual.TargetPosition = position;
        visual.TargetScale = scale;
        visual.TargetRotation = state.Rotation;
        if (sprites != null && sprites.TryGetValue(state.Sprite, out Sprite sprite)) visual.Renderer.sprite = sprite;
        else { visual.Renderer.sprite = null; MissingSprites++; }
        visual.Renderer.color = new Color32(state.R, state.G, state.B, state.A);
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
            if (visual.GameObject == null || visual.Renderer == null) continue;
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
        ClearVisuals();
        RestoreLocalRenderers();
        sprites = null; spriteIds.Clear(); indexedAssets = null; generation = 0; currentLevel = -1; MissingSprites = 0; Hud = "Waiting for state";
    }

    private void ClearVisuals()
    {
        foreach (Visual visual in visuals.Values) if (visual.GameObject != null) UnityEngine.Object.Destroy(visual.GameObject);
        visuals.Clear();
        displayedScores.Clear();
    }

    private void UpdateScoreUi(int number, int score, int previousScore)
    {
        displayedScores.TryGetValue(number, out int oldScore);
        displayedScores[number] = score;
        if (oldScore == score) return;
        try
        {
            object playerUi = Game.instance.ui.Player(Game.instance.level.players.FirstOrDefault(p => p.number == number));
            object scoreUi = playerUi?.GetType().GetField("score", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(playerUi);
            MethodInfo advance = scoreUi?.GetType().GetMethod("Advance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (advance != null) advance.Invoke(scoreUi, new object[] { (float)(score - oldScore) });
        }
        catch (Exception ex) { Debug.LogWarning("[TwinShotNet] Score UI update unavailable: " + ex.Message); }
    }

    private static int StableVisualId(SpriteRenderer renderer)
    {
        string path = renderer.transform.name;
        Transform parent = renderer.transform.parent;
        while (parent != null) { path = parent.name + "/" + path; parent = parent.parent; }
        unchecked
        {
            int hash = 17;
            foreach (char c in path) hash = hash * 31 + c;
            return hash == 0 ? 1 : hash;
        }
    }

    private void RestoreLocalRenderers()
    {
        foreach (var pair in hiddenRenderers)
            if (pair.Key != null) pair.Key.enabled = pair.Value;
        hiddenRenderers.Clear();
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

    private void HideLocalDynamicRenderers()
    {
        foreach (SpriteRenderer destroyed in hiddenRenderers.Keys.Where(renderer => renderer == null).ToArray())
            hiddenRenderers.Remove(destroyed);
        foreach (SpriteRenderer renderer in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
            if (renderer != null && !renderer.gameObject.name.StartsWith("TwinShotNet Replica ", StringComparison.Ordinal))
            {
                // Preserve the first observed state even if animation re-enables it later.
                if (!hiddenRenderers.ContainsKey(renderer)) hiddenRenderers.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
    }
}
