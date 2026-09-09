using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwinShotNet;

[BepInPlugin("local.twinshot.net", "Twin Shot Net (Experimental)", "0.5.1")]
public sealed partial class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    private bool _oldBackground, _backgroundOwned, _quitting;
    private int _snapshotFailures;

    private void Awake()
    {
        Instance = this;
        LoadNetworkConfiguration();
        using var hash = SHA256.Create();
        using var file = File.OpenRead(typeof(Game).Assembly.Location);
        _fingerprint = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        InitializePatching();

        Logger.LogInfo("TwinShotNet 0.5.1 loaded. F8 opens the network panel.");
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _visible = !_visible;
        if (Client && !_started && !_themePhase && _assigned > 0 && Input.GetKeyDown(KeyCode.Space))
            ToggleClientReady();
        // 网络回调与 Unity 状态更新均在主线程，加入输入必须先于事件轮询。
        _network?.PollEvents();
        if (_network == null) return;
        float now = Time.realtimeSinceStartup;
        UpdateTransport();
        if (_host) UpdateHost(now);
        else if (_server != null && _assigned != 0) UpdateClient(now);
    }

    private void LateUpdate()
    {
        if (_network == null || !_started || Game.instance == null || Game.instance.level == null) return;
        try
        {
            if (_host) BroadcastSnapshot();
            else UpdateClientScene();
            _snapshotFailures = 0;
        }
        catch (Exception ex)
        {
            // 单帧采集/打包失败不应终止整场会话；持续失败才收摊 (F7)。
            _snapshotFailures++;
            if (_snapshotFailures == 1 || _snapshotFailures % 60 == 0) Logger.LogError(ex);
            _status = "Snapshot error: " + ex.Message;
            if (_snapshotFailures < 60) return;
            Close(true);
            _status = "Session stopped: " + ex.Message;
        }
    }

    private void Close(bool returnToMenu)
    {
        bool wasStarted = _started;
        // 先停止传输，再清理输入和副本资源；恢复后台运行设置后才切回菜单。
        _network?.Stop();
        _network = null;
        _server = null;
        _peers.Clear();
        _started = false;
        _themePhase = false;
        _host = false;
        _assigned = 0;
        _players = 2;
        _clientSceneReady = false;
        _pendingFrame = null;
        _assembler = new FrameAssembler();
        _frameCache = new FrameCache();
        _sequence = 0;
        _nextInput = _nextSnapshot = _lastFrame = 0;
        _nextLobbySync = 0;
        _lastSent = 0;
        _snapshotFailures = 0;
        foreach (var input in _inputs) input.Clear();
        Array.Clear(_applied, 0, _applied.Length);
        Array.Clear(_occupied, 0, 4);
        Array.Clear(_ready, 0, 4);
        Array.Clear(_localOccupied, 0, 4);
        Array.Clear(_remoteSlot, 0, 4);
        Array.Clear(_lastInput, 0, 4);
        for (int i = 0; i < 4; i++) _skins[i] = i;
        _replica.Clear();
        if (_backgroundOwned)
        {
            Application.runInBackground = _oldBackground;
            _backgroundOwned = false;
        }

        _lastBroadcastCoins = -1;
        _status = "Offline";
        _visible = true;
        if (wasStarted && returnToMenu) SceneManager.LoadScene("Menus");
    }

    private void OnApplicationQuit()
    {
        _quitting = true;
        Close(false);
    }

    private void OnDisable()
    {
        Close(!_quitting);
    }

    private void OnDestroy()
    {
        try
        {
            Close(!_quitting);
        }
        finally
        {
            _startupHarmony?.UnpatchSelf();
            _harmony?.UnpatchSelf();
            Instance = null;
        }
    }
}