using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TwinShotNet;

internal static class SteamRestartPatch
{
    public static IEnumerable<CodeInstruction> TranspileDeckQuery(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var calls = code.Where(i => i.opcode == OpCodes.Call && i.operand is MethodInfo m &&
            m.DeclaringType?.FullName == "Steamworks.SteamUtils" && m.Name == "IsSteamRunningOnSteamDeck" &&
            m.IsStatic && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Expected one Steam Deck query.");
        calls[0].opcode = OpCodes.Ldc_I4_0;
        calls[0].operand = null;
        return code;
    }

    public static IEnumerable<CodeInstruction> TranspileOffline(IEnumerable<CodeInstruction> instructions)
    {
        var code = Transpile(instructions).ToList();
        var calls = code.Where(i => i.opcode == OpCodes.Call && i.operand is MethodInfo m &&
            m.DeclaringType?.FullName == "Steamworks.SteamAPI" && m.Name == "Init" &&
            m.IsStatic && m.ReturnType == typeof(bool) && m.GetParameters().Length == 0).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Expected one Steam Init call; refusing optional patch.");
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = AccessTools.Method(typeof(SteamRestartPatch), nameof(InitializationDisabled));
        return code;
    }

    public static bool InitializationDisabled()
    {
        UnityEngine.Debug.Log("[TwinShotNet] Startup reached; Steam initialization intentionally disabled.");
        return false;
    }

    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var matches = code.Where(i => i.opcode == OpCodes.Call && i.operand is MethodInfo method &&
            method.DeclaringType?.FullName == "Steamworks.SteamAPI" &&
            method.Name == "RestartAppIfNecessary" && method.IsStatic &&
            method.ReturnType == typeof(bool) && method.GetParameters().Length == 1).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one Steam restart call; leaving startup unchanged.");
        foreach (var instruction in code)
        {
            if (instruction == matches[0])
            {
                // Consume the existing AppId argument and return false, preserving branch labels.
                instruction.opcode = OpCodes.Pop;
                instruction.operand = null;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            }
            else yield return instruction;
        }
    }
}
