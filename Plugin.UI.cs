using System;
using System.Reflection;
using UnityEngine;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private bool _visible = true;
    internal bool PanelVisible => _visible;
    private string _status = "Offline";
    private Rect _window = new(20, 40, 430, 360);

    private void ApplyCompletionCoins(int coins)
    {
        try
        {
            var panel = Game.instance?.ui?.levelCompletePanel;
            var type = panel?.GetType();
            type?.GetField("lifetimeCoins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(panel, coins);
            var text = type?.GetField("textLifetimeCoins",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(panel);
            text?.GetType().GetProperty("text")?.SetValue(text, coins.ToString());
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Completion coin UI update unavailable: " + ex.Message);
        }
    }

    private void OnGUI()
    {
        if (Client && _clientSceneReady)
            GUI.Label(new Rect(12, Screen.height - 55, Screen.width - 24, 50), _replica.Hud);
        GUI.Label(new Rect(12, 8, Screen.width - 24, 25), "TwinShotNet EXPERIMENTAL | F8 | " + _status);
        if (!_visible) return;
        Cursor.visible = true;
        _window.width = Mathf.Min(430, Screen.width - 20);
        _window.x = Mathf.Clamp(_window.x, 0, Mathf.Max(0, Screen.width - _window.width));
        _window.y = Mathf.Clamp(_window.y, 0, Mathf.Max(0, Screen.height - _window.height));
        _window = GUILayout.Window(98241, _window, DrawWindow, "Twin Shot Net - Experimental");
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label(_status);
        if (_network == null)
        {
            GUILayout.Label("Host IP / DNS");
            _address = GUILayout.TextField(_address, 253);
            GUILayout.Label("UDP port");
            _port = GUILayout.TextField(_port, 5);
            GUILayout.Label("Room key (12+ characters)");
            _password = GUILayout.PasswordField(_password, '*', 128);
            if (GUILayout.Button("Host")) Open(true);
            if (GUILayout.Button("Join")) Open(false);
        }
        else
        {
            if (!_started)
            {
                GUILayout.Label("Use the original character screen to join, change skin and ready.");
                GUILayout.Label(_status);
            }

            if (_host && _started && Game.instance != null && GUILayout.Button("Restart level"))
                Game.instance.LoadAndStartLevel(Game.levelId, Game.instance.nextLevelAfterBonusRound, false);
            if (GUILayout.Button("Stop / Disconnect")) Close(true);
        }

        if (GUILayout.Button("Close panel")) _visible = false;
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
    }
}