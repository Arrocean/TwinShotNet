using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Replica
{
    private int _currentLevel = -1;

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
        // 每条 visual 的编码上限（id + 两个字符串前缀/长度 + 6 float + 颜色 + order + 2 bool）。
        const int maxVisualBytes = 4 + 5 + SnapshotCodec.MaxSpriteLength + 24 + 4 + 5 + SnapshotCodec.MaxLayerLength + 4 +
                                   2;
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
            // 条数和字节数都要设上限：只限条数时大关卡仍会突破 Wire.MaxRaw 让打包抛异常 (F7)。
            if (renderers.Count >= SnapshotCodec.MaxVisuals || stream.Length + maxVisualBytes > Wire.MaxRaw) break;
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
                // 分数文本由 UIForPlayer.Advance 从 Player.score 重新推导，无需单独的分数 UI 调用 (F6)。
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

    public void Clear()
    {
        ClearVisuals();
        RestoreLocalRenderers();
        _sprites = null;
        _spriteIds.Clear();
        _captureRenderers.Clear();
        _captureVisualIds.Clear();
        _advanceMethods.Clear();
        _indexedAssets = null;
        _indexedLevel = -1;
        _generation = 0;
        _currentLevel = -1;
        MissingSprites = 0;
        Hud = "Waiting for state";
    }
}