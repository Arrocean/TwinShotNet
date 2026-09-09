using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Replica
{
    private Dictionary<string, Sprite> _sprites;
    private Dictionary<Sprite, string> _spriteIds = new();
    private readonly Dictionary<Type, FieldInfo[]> _serializedFields = new();
    private Assets _indexedAssets;
    private int _indexedLevel = -1;

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
}