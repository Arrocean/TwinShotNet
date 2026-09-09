using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Replica
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
    private readonly List<int> _staleVisualIds = [];
    private readonly List<SpriteRenderer> _destroyedRenderers = [];
    private readonly List<SpriteRenderer> _captureRenderers = [];
    private readonly HashSet<int> _captureVisualIds = [];
    private int _generation;
    public int MissingSprites { get; private set; }

    // Unity 对象销毁后托管引用仍可能存在；保留 == null 假空判断，不能改为引用判空。
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
        // 动态对象会持续生成，动画也可能重新启用渲染器，因此每帧扫描不可只做一次或改为静态缓存。
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

    private void ClearVisuals()
    {
        foreach (Visual visual in _visuals.Values)
            if (visual.GameObject != null)
                UnityEngine.Object.Destroy(visual.GameObject);
        _visuals.Clear();
        // 换关与会话清理共用此入口，HUD 分数及失败记录必须与视觉对象一起重置。
        _displayedScores.Clear();
        _failedPlayerHud.Clear();
        _failedScoreUi.Clear();
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