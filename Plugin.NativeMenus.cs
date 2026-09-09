using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private const BindingFlags MenuFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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

    private static Array FindLobbyBoxes()
    {
        var scene = MenuScene.instance;
        var menu = scene?.GetType().GetField("characterSelectMenu", MenuFlags)?.GetValue(scene);
        return menu?.GetType().GetField("boxes", MenuFlags)?.GetValue(menu) as Array;
    }

    private static bool TryBoxNumber(object box, out int number)
    {
        number = 0;
        if (box == null) return false;
        if (box.GetType().GetField("number", MenuFlags)?.GetValue(box) is not int value) return false;
        if (value < 1 || value > 4) return false;
        number = value;
        return true;
    }

    private void SyncHostLocalState()
    {
        if (!_host) return;
        // 先写入临时数组再整体提交：读取失败时只撤销 ready，不破坏已确认的占用状态。
        var occupied = new bool[4];
        var ready = new bool[4];
        var skins = new int[4];
        Array.Copy(_skins, skins, 4);
        bool confirmed = false;
        try
        {
            var boxes = FindLobbyBoxes();
            if (boxes != null)
            {
                confirmed = true;
                foreach (var box in boxes)
                {
                    if (box == null) continue;
                    var type = box.GetType();
                    if (type.GetField("number", MenuFlags)?.GetValue(box) is not int number) continue;
                    if (number < 1 || number > 4) continue;
                    int slot = number - 1;
                    // 远端槽位由网络拥有：原生箱子既不参与读取也不参与写入 (F5)。
                    if (_remoteSlot[slot]) continue;
                    if (!TryReadLobbyValue(type.GetField("state", MenuFlags)?.GetValue(box), out var state)) continue;
                    if (!TryReadLobbyValue(type.GetField("selectedSkin", MenuFlags)?.GetValue(box), out var skin))
                        continue;
                    if (state < 0 || state > 2) continue;
                    occupied[slot] = state != 0;
                    ready[slot] = state == 2;
                    // 原生界面允许解锁皮肤（>3），协议只承载基础四色；
                    // 越界时保留上一轮基础色，而不是丢掉整只箱子的占用/就绪状态 (F4)。
                    if (skin >= 0 && skin <= 3) skins[slot] = skin;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native host lobby state unavailable: " + ex.Message);
        }

        for (int i = 0; i < 4; i++)
        {
            if (_remoteSlot[i])
            {
                // 远端槽位永远不是本地槽位；显式清掉，避免槽位易主后残留。
                _localOccupied[i] = false;
                continue;
            }

            if (confirmed)
            {
                _occupied[i] = occupied[i];
                _ready[i] = ready[i];
                _skins[i] = skins[i];
            }
            else
            {
                // 读取失败不能保留未经本轮确认的 ready。
                _ready[i] = false;
            }

            _localOccupied[i] = _occupied[i];
        }

        RepackRemoteSlots();
        BroadcastLobby();
    }

    // 主机侧把远端槽位的占用/就绪写进原生箱子，否则主机看不到谁在等待 (F5)。
    private void SyncHostRemoteBoxes()
    {
        try
        {
            var boxes = FindLobbyBoxes();
            if (boxes == null) return;
            for (int i = 0; i < Math.Min(4, boxes.Length); i++)
            {
                if (!_remoteSlot[i]) continue;
                object box = boxes.GetValue(i);
                if (box == null) continue;
                var type = box.GetType();
                type.GetField("selectedSkin", MenuFlags)?.SetValue(box, _skins[i]);
                type.GetField("state", MenuFlags)?.SetValue(box, _occupied[i] ? (_ready[i] ? 2 : 1) : 0);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native host lobby sync unavailable: " + ex.Message);
        }
    }

    private void ShowNativeThemeSelect()
    {
        try
        {
            var menuScene = MenuScene.instance;
            // 原生 ContinueToNextScreen 在显示主题菜单前会先隐藏角色选择菜单，
            // 而该补丁接管后原生方法不再执行；不复刻这一步两个菜单会叠加显示 (F14)。
            var characterSelect = menuScene?.GetType().GetField("characterSelectMenu", MenuFlags)?.GetValue(menuScene);
            if (characterSelect is Component component) component.gameObject.SetActive(false);
            var menu = menuScene?.GetType().GetField("themeSelectMenu", MenuFlags)?.GetValue(menuScene);
            menu?.GetType().GetMethod("Show", MenuFlags)?.Invoke(menu, null);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native theme menu unavailable: " + ex.Message);
        }
    }

    // 客户端按加入键只切换自己的就绪状态。原生 PlayerJoinBox.Join 已被补丁恒定抑制，
    // 原先的反射调用是死代码，而且会构造一个非法 ControllerId (F9)。
    private void ToggleClientReady()
    {
        if (_assigned < 2 || _assigned > 4) return;
        int slot = _assigned - 1;
        SendLobbyChange(_skins[slot], !_ready[slot]);
    }

    private void SyncNativeLobby()
    {
        try
        {
            var boxes = FindLobbyBoxes();
            if (boxes == null) return;
            for (int i = 0; i < Math.Min(4, boxes.Length); i++)
            {
                object box = boxes.GetValue(i);
                if (box == null) continue;
                var type = box.GetType();
                type.GetField("selectedSkin", MenuFlags)?.SetValue(box, _skins[i]);
                type.GetField("state", MenuFlags)?.SetValue(box, _occupied[i] ? (_ready[i] ? 2 : 1) : 0);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native lobby sync unavailable: " + ex.Message);
        }
    }

    internal bool HandleNativeJoin(object box, bool back, int direction)
    {
        // 远端槽位由网络拥有：主机原生输入不得修改它们的皮肤或状态 (F5)。
        if (Hosting && TryBoxNumber(box, out int remoteNumber) && _remoteSlot[remoteNumber - 1]) return false;
        // The host/offline game uses native input; do not reflect when this plugin is inapplicable.
        if (_started || !isActiveAndEnabled || !Client || _assigned <= 0) return true;
        // Fail closed for an active client: neither run native input nor send a lobby change
        // unless the join box is known to belong to our valid assigned slot.
        if (!TryBoxNumber(box, out int number) || number != _assigned) return false;
        int slot = _assigned - 1;
        if (back) SendLobbyChange(_skins[slot], false);
        else if (direction != 0) SendLobbyChange(Mathf.Clamp(_skins[slot] + direction, 0, 3), false);
        else SendLobbyChange(_skins[slot], !_ready[slot]);
        return false;
    }
}
