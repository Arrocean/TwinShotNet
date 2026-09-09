using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;

namespace TwinShotNet;

[HarmonyPatch(typeof(CharacterSelectMenu), "ContinueToNextScreen")]
internal static class CharacterSelectContinuePatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(CharacterSelectMenu __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.InLobby) return true;
        if (plugin.Client) return false;
        if (!plugin.Hosting) return true;
        var method = __instance.GetType().GetMethod("IsReadyToContinue",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) return false;
        if (method.Invoke(__instance, null) is not bool ready) return false;
        if (!ready) return true;
        return plugin.AllowNativeContinue();
    }
}

[HarmonyPatch(typeof(CharacterSelectMenu), "GoBackToPreviousMenu")]
internal static class ClientCharacterSelectBackPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() =>
        Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Client;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "StartGame")]
internal static class ThemeSelectStartPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix(Theme themeChoice, LevelId levelToStartOn)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.Hosting) return true;
        plugin.StartSelectedMatch(themeChoice, levelToStartOn);
        return false;
    }
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressContinue")]
internal static class ClientThemeContinuePatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressLevelSelect")]
internal static class ClientThemeLevelSelectPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressThemeLeft")]
internal static class ClientThemeLeftPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressThemeRight")]
internal static class ClientThemeRightPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressLevelButton")]
internal static class ClientThemeLevelButtonPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressBack")]
internal static class ClientThemeBackPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(PlayerJoinBox), "OnPressLeft")]
internal static class CharacterSelectLeftPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null ||
                                                            !Plugin.Instance.isActiveAndEnabled ||
                                                            Plugin.Instance.HandleNativeJoin(__instance, false, -1);
}

[HarmonyPatch(typeof(PlayerJoinBox), "OnPressRight")]
internal static class CharacterSelectRightPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null ||
                                                            !Plugin.Instance.isActiveAndEnabled ||
                                                            Plugin.Instance.HandleNativeJoin(__instance, false, 1);
}

[HarmonyPatch(typeof(PlayerJoinBox), "Back")]
internal static class CharacterSelectBackPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null ||
                                                            !Plugin.Instance.isActiveAndEnabled ||
                                                            Plugin.Instance.HandleNativeJoin(__instance, true, 0);
}

[HarmonyPatch(typeof(PlayerJoinBox), "Join")]
internal static class CharacterSelectJoinPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled) return true;
        return plugin.HandleNativeJoin(__instance, false, 0);
    }
}