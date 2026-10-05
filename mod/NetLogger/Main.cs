using System;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Il2CppNinjaKiwi.NKMulti;

[assembly: MelonInfo(typeof(BTD6LanNetLogger.Main), "BTD6 LAN Net Logger", "0.1.0", "homebrew")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]

namespace BTD6LanNetLogger;

// Dumps every relay message the game sends and receives, so one real co-op match
// yields the exact message codes and payload shapes needed to build the LAN relay.
// Output goes to the MelonLoader console
// and Logs; nothing gameplay-facing is changed.
public class Main : MelonMod
{
    public override void OnInitializeMelon()
    {
        LoggerInstance.Msg("net logger armed; join an online co-op game to capture the protocol");
    }

    private static void Dump(string dir, Il2CppSystem.Collections.Generic.Queue<Message> q)
    {
        if (q == null) return;
        try
        {
            var arr = q.ToArray();
            foreach (var m in arr)
            {
                if (m == null) continue;
                string code = m.code != null ? m.code.ToString() : "<null>";
                int len = m.bytes != null ? m.bytes.Length : 0;
                var sb = new StringBuilder();
                int show = Math.Min(len, 48);
                for (int i = 0; i < show; i++) sb.Append(m.bytes[i].ToString("x2"));
                Melon<Main>.Logger.Msg($"[{dir}] code={code} len={len} head={sb}");
            }
        }
        catch (Exception e) { Melon<Main>.Logger.Warning($"dump {dir} failed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(NKMultiConnection), nameof(NKMultiConnection.Send))]
    private static class SendPatch
    {
        private static void Prefix(NKMultiConnection __instance) => Dump("OUT", __instance.SendQueue);
    }

    [HarmonyPatch(typeof(NKMultiConnection), nameof(NKMultiConnection.Receive))]
    private static class ReceivePatch
    {
        private static void Postfix(NKMultiConnection __instance) => Dump("IN", __instance.ReceiveQueue);
    }
}
