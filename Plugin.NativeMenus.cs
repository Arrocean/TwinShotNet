using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private static bool TryReadLobbyValue(object value, out int result)
    {
        // Native state/skin fields may be integer-backed enums; null is not slot/state zero.
        if (value is int integer)
        {
            result = integer;
            return true;
        }

        if (value is Enum enumeration)
        {
            result = Convert.ToInt32(enumeration);
            return true;
        }

        result = 0;
        return false;
    }

    private void SyncHostLocalState()
    {
        if (!_host) return;
        // Readiness must be confirmed on every sync, never retained after a failed read.
        for (var i = 0; i < _hostLocalPlayers; i++) _ready[i] = false;
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType()
                .GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(menu) as Array;
            if (boxes != null)
                foreach (var box in boxes)
                {
                    if (box == null) continue;
                    var type = box.GetType();
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var numberField = type.GetField("number", flags);
                    if (numberField == null) continue;
                    if (numberField.GetValue(box) is not int number) continue;
                    if (number < 1 || number > _hostLocalPlayers) continue;
                    var stateField = type.GetField("state", flags);
                    if (stateField == null) continue;
                    if (!TryReadLobbyValue(stateField.GetValue(box), out var state)) continue;
                    var skinField = type.GetField("selectedSkin", flags);
                    if (skinField == null) continue;
                    if (!TryReadLobbyValue(skinField.GetValue(box), out var skin)) continue;
                    if (state < 0 || state > 2 || skin < 0 || skin > 3) continue;
                    var slot = number - 1;
                    _occupied[slot] = state != 0;
                    _skins[slot] = skin;
                    _ready[slot] = state == 2;
                }
        }
        catch (Exception ex)
        {
            for (var i = 0; i < _hostLocalPlayers; i++) _ready[i] = false;
            Logger.LogDebug("Native host lobby state unavailable: " + ex.Message);
        }

        for (int i = 0; i < 4; i++)
            if (!_peers.Any(pair => pair.Value == i))
            {
                if (!_occupied[i]) _ready[i] = false;
            }

        BroadcastLobby();
    }

    private static int HostLocalPlayerCount()
    {
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType()
                .GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(menu) as Array;
            if (boxes == null) return 1;
            int count = 0;
            foreach (var box in boxes)
            {
                var state = box?.GetType()
                    .GetField("state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(box);
                if (state != null && Convert.ToInt32(state) != 0) count++;
            }

            return Mathf.Clamp(count, 1, 4);
        }
        catch
        {
            return 1;
        }
    }

    private static void ShowNativeThemeSelect()
    {
        var menuScene = MenuScene.instance;
        var menu = menuScene?.GetType()
            .GetField("themeSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(menuScene);
        menu?.GetType().GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(menu, null);
    }

    private void InvokeClientJoin()
    {
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType()
                .GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(menu) as Array;
            if (boxes == null || _assigned > boxes.Length) return;
            var box = boxes.GetValue(_assigned - 1);
            var join = box?.GetType()
                .GetMethod("Join", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (join != null)
                join.Invoke(box, new[] { Activator.CreateInstance(join.GetParameters()[0].ParameterType) });
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Client native join unavailable: " + ex.Message);
        }
    }

    private void SyncNativeLobby()
    {
        try
        {
            var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object menuScene = typeof(MenuScene).GetField("instance", flags)?.GetValue(null);
            object menu = menuScene?.GetType().GetField("characterSelectMenu", flags)?.GetValue(menuScene);
            if (menu == null) return;
            var boxes = menu.GetType().GetField("boxes", flags)?.GetValue(menu) as Array;
            if (boxes == null) return;
            for (int i = 0; i < Math.Min(4, boxes.Length); i++)
            {
                object box = boxes.GetValue(i);
                if (box == null) continue;
                var type = box.GetType();
                type.GetField("selectedSkin", flags)?.SetValue(box, _skins[i]);
                type.GetField("state", flags)?.SetValue(box, _occupied[i] ? (_ready[i] ? 2 : 1) : 0);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native lobby sync unavailable: " + ex.Message);
        }
    }

    internal bool HandleNativeJoin(object box, bool back, int direction)
    {
        // The host/offline game uses native input; do not reflect when this plugin is inapplicable.
        if (_started || !isActiveAndEnabled || !Client || _assigned <= 0) return true;
        // Fail closed for an active client: neither run native input nor send a lobby change
        // unless the join box is known to belong to our valid assigned slot.
        if (box == null || _assigned > _skins.Length) return false;
        var numberField = box.GetType().GetField("number",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (numberField == null) return false;
        if (numberField.GetValue(box) is not int number) return false;
        if (number != _assigned) return false;
        int slot = _assigned - 1;
        if (back) ChangeLobby(_skins[slot], false);
        else if (direction != 0) ChangeLobby(Mathf.Clamp(_skins[slot] + direction, 0, 3), false);
        else ChangeLobby(_skins[slot], !_ready[slot]);
        return false;
    }
}