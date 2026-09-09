using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Replica
{
    private readonly Dictionary<Type, MethodInfo> _advanceMethods = new();
    private readonly HashSet<int> _failedPlayerHud = [];
    public string Hud { get; private set; } = "Waiting for state";

    private MethodInfo GetAdvanceMethod(Type type)
    {
        if (_advanceMethods.TryGetValue(type, out var method)) return method;
        // Cache absent members and lookup failures as well as successful metadata.
        _advanceMethods.Add(type, null);
        method = type.GetMethod("Advance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _advanceMethods[type] = method;
        return method;
    }

    // UIForPlayer.Advance() 内部会按 Player.score 重写分数文本与分数条，
    // 因此分数同步不需要（也不应该）单独调用带参数的子控件方法 (F6)。
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
}
