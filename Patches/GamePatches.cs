using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TwinShotNet;

[HarmonyPatch(typeof(GameInput), "AdvancePlayer")]
internal static class InputPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix(int number)
    {
        if (Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Hosting ||
            Plugin.Instance.InLobby || Game.instance == null) return true;
        // 远端槽位始终由网络队列驱动；本地槽位（含主机 P2-P4）在面板关闭且窗口聚焦时走原生输入 (F13)。
        if (!Plugin.Instance.IsRemoteSlot(number - 1) && !Plugin.Instance.PanelVisible && Application.isFocused)
            return true;
        Plugin.Instance.ApplyInput(number);
        return false;
    }
}

[HarmonyPatch(typeof(Game), "Update")]
internal static class ClientGamePatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() =>
        Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Client;
}

[HarmonyPatch(typeof(LevelCompletePanel), "Advance")]
internal static class CompletionCoinsPatch
{
    [HarmonyPostfix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static void Postfix(LevelCompletePanel __instance)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.Hosting) return;
        var field = __instance.GetType().GetField("lifetimeCoins",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(__instance) is int coins) plugin.BroadcastCompletionCoins(coins);
    }
}