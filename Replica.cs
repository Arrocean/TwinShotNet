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

    private readonly Dictionary<int, Visual> _visuals = new();
    private readonly Dictionary<SpriteRenderer, bool> _hiddenRenderers = new();
    private Dictionary<string, Sprite> _sprites;
    private Dictionary<Sprite, string> _spriteIds = new();
    private readonly Dictionary<Type, FieldInfo[]> _serializedFields = new();
    private readonly List<int> _staleVisualIds = [];
    private readonly List<SpriteRenderer> _destroyedRenderers = [];
    private readonly List<SpriteRenderer> _captureRenderers = [];
    private readonly HashSet<int> _captureVisualIds = [];
    private readonly Dictionary<Type, MethodInfo> _advanceMethods = new();
    private readonly Dictionary<Type, FieldInfo> _scoreFields = new();
    private readonly HashSet<int> _failedPlayerHud = [];
    private readonly HashSet<int> _failedScoreUi = [];
    private Assets _indexedAssets;
    private int _indexedLevel = -1;
    private int _generation;
    private int _currentLevel = -1;
    private readonly Dictionary<int, int> _displayedScores = new();
    public int MissingSprites { get; private set; }
    public string Hud { get; private set; } = "Waiting for state";

    public byte[] Capture()
    {
        EnsureSpriteIndex();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Game.levelId.uniqueLevelNumber);
        var camera = Camera.main;
        var cameraPosition = Vector3.zero;
        var cameraSize = 300f;
        if (camera != null)
        {
            cameraPosition = camera.transform.position;
            cameraSize = camera.orthographicSize;
        }

        writer.Write(cameraPosition.x);
        writer.Write(cameraPosition.y);
        writer.Write(cameraSize);

        Player[] players = Game.instance.level.players;
        writer.Write((byte)players.Length);
        foreach (Player player in players)
        {
            writer.Write(player.number);
            writer.Write(player.hits);
            writer.Write(player.score);
            writer.Write(player.alive);
            writer.Write((byte)player.powerup);
        }

        SpriteRenderer[] found = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);
        var renderers = _captureRenderers;
        var visualIds = _captureVisualIds;
        renderers.Clear();
        visualIds.Clear();
        int uiLayer = LayerMask.NameToLayer("UI");
        foreach (SpriteRenderer renderer in found)
        {
            if (renderer == null || !renderer.enabled) continue;
            GameObject gameObject = renderer.gameObject;
            if (!gameObject.activeInHierarchy || gameObject.isStatic || gameObject.layer == uiLayer ||
                gameObject.name.StartsWith("TwinShotNet Replica ", StringComparison.Ordinal) ||
                renderer.sprite == null || IsLocalMapTile(renderer)) continue;
            Transform transform = renderer.transform;
            if (!ValidVector(transform.position) || !ValidVector(transform.lossyScale) ||
                !ValidFloat(transform.eulerAngles.z)) continue;
            if (!visualIds.Add(StableVisualId(renderer))) continue;
            renderers.Add(renderer);
            if (renderers.Count >= SnapshotCodec.MaxVisuals) break;
        }

        writer.Write(renderers.Count);
        foreach (SpriteRenderer renderer in renderers)
        {
            Transform transform = renderer.transform;
            Vector3 position = transform.position;
            Vector3 scale = transform.lossyScale;
            Vector3 rotation = transform.eulerAngles;
            writer.Write(StableVisualId(renderer));
            writer.Write(_spriteIds.TryGetValue(renderer.sprite, out string key) ? key : "missing");
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            writer.Write(scale.x);
            writer.Write(scale.y);
            writer.Write(scale.z);
            writer.Write(rotation.z);
            Color32 color = renderer.color;
            writer.Write(color.r);
            writer.Write(color.g);
            writer.Write(color.b);
            writer.Write(color.a);
            writer.Write(renderer.sortingLayerName ?? "Default");
            writer.Write(renderer.sortingOrder);
            writer.Write(renderer.flipX);
            writer.Write(renderer.flipY);
        }

        return stream.ToArray();
    }

    public void InitializeClient()
    {
        BuildSpriteIndex();
        HideLocalDynamicRenderers();
        _currentLevel = Game.levelId.uniqueLevelNumber;
    }

    public void Apply(byte[] raw)
    {
        Snapshot snapshot = SnapshotCodec.Decode(raw);
        int level = snapshot.Level;
        if (level != _currentLevel)
        {
            LevelId id = LevelId.ByUniqueNumber(level);
            if (!id.DoesExist()) throw new InvalidDataException("Host selected an unavailable level");
            ClearVisuals();
            RestoreLocalRenderers();
            Game.instance.LoadAndStartLevel(id, null, false);
            _currentLevel = level;
            BuildSpriteIndex();
            HideLocalDynamicRenderers();
            return;
        }

        EnsureSpriteIndex();
        var camera = Camera.main;
        if (camera != null)
        {
            var transform = camera.transform;
            var position = transform.position;
            position.x = snapshot.CameraX;
            position.y = snapshot.CameraY;
            transform.position = position;
            camera.orthographicSize = snapshot.CameraSize;
        }

        var hud = new List<string>(snapshot.Players.Length);
        foreach (SnapshotPlayer player in snapshot.Players)
        {
            int number = player.Number;
            int hits = player.Hits;
            int score = player.Score;
            bool alive = player.Alive;
            byte powerup = player.Powerup;
            Player local = Game.instance.level.players.FirstOrDefault(p => p.number == number);
            if (local != null)
            {
                local.hits = hits;
                local.score = score;
                local.alive = alive;
                local.powerup = (Player.PowerupType)powerup;
                UpdateScoreUi(local, score);
                UpdatePlayerHud(local);
            }

            hud.Add($"P{number} {(alive ? hits + " HP" : "OUT")} {score} pts" +
                    (powerup == 0 ? "" : $" power {powerup}"));
        }

        Hud = string.Join("   ", hud);
        _generation++;
        MissingSprites = 0;
        foreach (SnapshotVisual visual in snapshot.Visuals) Apply(visual);
        _staleVisualIds.Clear();
        foreach (var pair in _visuals)
            if (pair.Value.Seen != _generation)
                _staleVisualIds.Add(pair.Key);
        foreach (int id in _staleVisualIds)
        {
            if (_visuals[id].GameObject != null) UnityEngine.Object.Destroy(_visuals[id].GameObject);
            _visuals.Remove(id);
        }
    }

    private void Apply(SnapshotVisual state)
    {
        var position = new Vector3(state.X, state.Y, state.Z);
        var scale = new Vector3(state.ScaleX, state.ScaleY, state.ScaleZ);
        if (!_visuals.TryGetValue(state.Id, out Visual visual) || visual.GameObject == null || visual.Renderer == null)
        {
            if (visual != null && visual.GameObject != null) UnityEngine.Object.Destroy(visual.GameObject);
            var gameObject = new GameObject("TwinShotNet Replica " + state.Id);
            visual = new Visual { GameObject = gameObject, Renderer = gameObject.AddComponent<SpriteRenderer>() };
            var transform = gameObject.transform;
            transform.position = position;
            transform.localScale = scale;
            transform.eulerAngles = new Vector3(0, 0, state.Rotation);
            _visuals[state.Id] = visual;
        }

        visual.Seen = _generation;
        visual.TargetPosition = position;
        visual.TargetScale = scale;
        visual.TargetRotation = state.Rotation;
        if (_sprites != null && _sprites.TryGetValue(state.Sprite, out Sprite sprite) && sprite != null)
            visual.Renderer.sprite = sprite;
        else
        {
            visual.Renderer.sprite = null;
            MissingSprites++;
        }

        visual.Renderer.color = new Color32(state.R, state.G, state.B, state.A);
        visual.Renderer.sortingLayerName = state.Layer;
        visual.Renderer.sortingOrder = state.Order;
        visual.Renderer.flipX = state.FlipX;
        visual.Renderer.flipY = state.FlipY;
    }

    public void Render()
    {
        // Animation components can re-enable original renderers after initial scene setup.
        HideLocalDynamicRenderers();
        float amount = 1 - Mathf.Exp(-Time.unscaledDeltaTime * 28);
        foreach (Visual visual in _visuals.Values)
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
        _sprites = null;
        _spriteIds.Clear();
        _captureRenderers.Clear();
        _captureVisualIds.Clear();
        _advanceMethods.Clear();
        _scoreFields.Clear();
        _indexedAssets = null;
        _indexedLevel = -1;
        _generation = 0;
        _currentLevel = -1;
        MissingSprites = 0;
        Hud = "Waiting for state";
    }

    private void ClearVisuals()
    {
        foreach (Visual visual in _visuals.Values)
            if (visual.GameObject != null)
                UnityEngine.Object.Destroy(visual.GameObject);
        _visuals.Clear();
        _displayedScores.Clear();
        _failedPlayerHud.Clear();
        _failedScoreUi.Clear();
    }

    private MethodInfo GetAdvanceMethod(Type type)
    {
        if (_advanceMethods.TryGetValue(type, out var method)) return method;
        // Cache absent members and lookup failures as well as successful metadata.
        _advanceMethods.Add(type, null);
        method = type.GetMethod("Advance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _advanceMethods[type] = method;
        return method;
    }

    private FieldInfo GetScoreField(Type type)
    {
        if (_scoreFields.TryGetValue(type, out var field)) return field;
        _scoreFields.Add(type, null);
        field = type.GetField("score", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _scoreFields[type] = field;
        return field;
    }

    private void UpdatePlayerHud(Player player)
    {
        if (_failedPlayerHud.Contains(player.number)) return;
        try
        {
            object ui = Game.instance.ui.Player(player);
            if (ui != null) GetAdvanceMethod(ui.GetType())?.Invoke(ui, null);
        }
        catch (Exception ex)
        {
            // Do not repeatedly invoke a broken HUD until the level/session is cleared.
            _failedPlayerHud.Add(player.number);
            Debug.LogWarning("[TwinShotNet] Player HUD update unavailable: " + ex.Message);
        }
    }

    private void UpdateScoreUi(Player player, int score)
    {
        _displayedScores.TryGetValue(player.number, out int oldScore);
        // Commit before invoking: Advance may mutate the score and then throw.
        // Never replay a failed delta, even when later snapshots change the score.
        _displayedScores[player.number] = score;
        if (oldScore == score || _failedScoreUi.Contains(player.number)) return;
        try
        {
            object playerUi = Game.instance.ui.Player(player);
            var scoreUi = playerUi == null ? null : GetScoreField(playerUi.GetType())?.GetValue(playerUi);
            var advance = scoreUi == null ? null : GetAdvanceMethod(scoreUi.GetType());
            if (advance != null) advance.Invoke(scoreUi, new object[] { (float)(score - oldScore) });
        }
        catch (Exception ex)
        {
            _failedScoreUi.Add(player.number);
            Debug.LogWarning("[TwinShotNet] Score UI update unavailable: " + ex.Message);
        }
    }

    private static int StableVisualId(SpriteRenderer renderer)
    {
        // Unity instance IDs are unique for live objects and avoid path-hash collisions
        // when generated enemies contain identical transform hierarchies.
        int id = renderer.GetInstanceID();
        return id == 0 ? 1 : id;
    }

    private static bool ValidFloat(float value) => !float.IsNaN(value) && !float.IsInfinity(value) &&
                                                   Mathf.Abs(value) < SnapshotCodec.CoordinateLimit;

    private static bool ValidVector(Vector3 value) => ValidFloat(value.x) && ValidFloat(value.y) && ValidFloat(value.z);

    private void RestoreLocalRenderers()
    {
        foreach (var pair in _hiddenRenderers)
            if (pair.Key != null)
                pair.Key.enabled = pair.Value;
        _hiddenRenderers.Clear();
    }

    private void EnsureSpriteIndex()
    {
        if (_sprites == null || _indexedAssets != Game.instance.assets ||
            _indexedLevel != Game.levelId.uniqueLevelNumber)
            BuildSpriteIndex();
    }

    private void BuildSpriteIndex()
    {
        var nextSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        var nextSpriteIds = new Dictionary<Sprite, string>();
        Assets assets = Game.instance.assets;
        int level = Game.levelId.uniqueLevelNumber;
        using var hash = SHA256.Create();
        var visitedObjects = new HashSet<object>();
        Visit(assets, "Assets", visitedObjects, 0);
        // Only accept unambiguous fallback names; never silently overwrite a different sprite.
        foreach (var group in Resources.FindObjectsOfTypeAll<Sprite>().Where(s => s != null).GroupBy(SpriteKey))
        {
            if (group.Count() != 1) continue;
            Sprite sprite = group.First();
            if (!nextSpriteIds.ContainsKey(sprite)) Register(sprite, "fallback/" + group.Key);
        }

        _sprites = nextSprites;
        _spriteIds = nextSpriteIds;
        _indexedAssets = assets;
        _indexedLevel = level;
        Debug.Log($"[TwinShotNet] Indexed {_spriteIds.Count} sprites from serialized asset references.");

        void Register(Sprite sprite, string path)
        {
            if (nextSpriteIds.ContainsKey(sprite)) return;
            string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
            nextSpriteIds[sprite] = key;
            nextSprites[key] = sprite;
        }

        void Visit(object value, string path, HashSet<object> visited, int depth)
        {
            if (value == null || depth > 32) return;
            if (value is Sprite sprite)
            {
                Register(sprite, path);
                return;
            }

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
            if (!_serializedFields.TryGetValue(type, out FieldInfo[] fields))
            {
                fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(field =>
                        !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), false)))
                    .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
                _serializedFields.Add(type, fields);
            }

            foreach (FieldInfo field in fields)
                Visit(field.GetValue(value), path + "/" + field.Name, visited, depth + 1);
        }
    }

    private static string SpriteKey(Sprite sprite)
    {
        Rect rect = sprite.rect;
        return (sprite.texture != null ? sprite.texture.name : "") + "|" + sprite.name + "|" + (int)rect.x + "," +
               (int)rect.y + "," + (int)rect.width + "," + (int)rect.height;
    }

    private void HideLocalDynamicRenderers()
    {
        _destroyedRenderers.Clear();
        foreach (SpriteRenderer renderer in _hiddenRenderers.Keys)
            if (renderer == null)
                _destroyedRenderers.Add(renderer);
        foreach (SpriteRenderer destroyed in _destroyedRenderers)
            _hiddenRenderers.Remove(destroyed);
        int uiLayer = LayerMask.NameToLayer("UI");
        foreach (SpriteRenderer renderer in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode
                     .None))
        {
            if (renderer == null) continue;
            if (_hiddenRenderers.ContainsKey(renderer))
            {
                if (renderer.enabled) renderer.enabled = false;
                continue;
            }

            GameObject gameObject = renderer.gameObject;
            if (gameObject.isStatic || gameObject.layer == uiLayer ||
                gameObject.name.StartsWith("TwinShotNet Replica ", StringComparison.Ordinal) ||
                IsLocalMapTile(renderer)) continue;
            // Preserve the first observed state even if animation re-enables it later.
            var wasEnabled = renderer.enabled;
            _hiddenRenderers.Add(renderer, wasEnabled);
            if (wasEnabled) renderer.enabled = false;
        }
    }

    private static bool IsLocalMapTile(SpriteRenderer renderer)
    {
        if (renderer.GetComponent<Tile>() != null) return true;
        for (Transform parent = renderer.transform.parent; parent != null; parent = parent.parent)
            if (parent.name.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }
}