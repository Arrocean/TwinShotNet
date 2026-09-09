using System;
using HarmonyLib;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private Harmony _harmony;
    private Harmony _startupHarmony;

    private void InitializePatching()
    {
        _harmony = new Harmony("local.twinshot.net");
        _harmony.PatchAll(typeof(Plugin).Assembly);
        _startupHarmony = new Harmony("local.twinshot.net.startup");
        if (Config.Bind("Startup", "DisableSteamInitialization", false,
                "Experimental: disable managed Steam initialization and restart requests. Steam features become unavailable. Restart required; does not affect native launch checks.")
            .Value)
        {
            try
            {
                _startupHarmony.Patch(AccessTools.Method(typeof(SteamManager), "Awake"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch),
                        nameof(SteamRestartPatch.TranspileOffline)));
                _startupHarmony.Patch(AccessTools.Method(typeof(LoadingScreen), "Start"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch),
                        nameof(SteamRestartPatch.TranspileOffline)));
                _startupHarmony.Patch(
                    AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LoadingScreen), "Sequence")),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch),
                        nameof(SteamRestartPatch.TranspileDeckQuery)));
                Logger.LogWarning(
                    "DisableSteamInitialization enabled: managed restart and Init calls disabled. Steam features unavailable.");
            }
            catch (Exception ex)
            {
                _startupHarmony.UnpatchSelf();
                Logger.LogError("Optional Steam initialization patches rolled back: " + ex);
            }
        }
        else if (Config.Bind("Startup", "SkipSteamRestart", false,
                     "Experimental: skip the game's Steam automatic restart request. Restart required. Steam initialization and other checks remain unchanged.")
                 .Value)
        {
            try
            {
                _startupHarmony.Patch(AccessTools.Method(typeof(SteamManager), "Awake"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.Transpile)));
                _startupHarmony.Patch(AccessTools.Method(typeof(LoadingScreen), "Start"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.Transpile)));
                Logger.LogWarning(
                    "SkipSteamRestart enabled. Steam initialization is unchanged; standalone startup is not guaranteed.");
            }
            catch (Exception ex)
            {
                _startupHarmony.UnpatchSelf();
                Logger.LogError("Optional Steam restart patches rolled back: " + ex);
            }
        }
    }
}