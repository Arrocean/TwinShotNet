using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Replica
{
    private readonly Dictionary<Type, MethodInfo> _advanceMethods = new();
    private readonly Dictionary<Type, FieldInfo> _scoreFields = new();
    private readonly HashSet<int> _failedPlayerHud = [];
    private readonly HashSet<int> _failedScoreUi = [];
    private readonly Dictionary<int, int> _displayedScores = new();
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
        // Advance 可能先改变分数再抛异常，所以必须先记账；后续快照也不得重放失败的分数增量。
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
}