using System;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.ModOptions;
using HarmonyLib;
using Il2CppNinjaKiwi.NKMulti;
using MelonLoader;

[assembly: MelonInfo(typeof(BTD6LanCoop.Main), BTD6LanCoop.ModHelperData.Name,
    BTD6LanCoop.ModHelperData.Version, BTD6LanCoop.ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonOptionalDependencies("BTD_Mod_Helper")]

namespace BTD6LanCoop;

// Moves BTD6 co-op gameplay traffic off Ninja Kiwi's relay and onto one running on the
// local network. Matchmaking is untouched: the lobby and its invite code still come from
// LiNK over the internet, and the relay groups peers by the lobby code they announce in
// their join frame, so both players land in the same match without any extra setting.
//
// Mechanism: NKMultiGameInterface holds the relay host and port and opens the socket in an
// async Connect. Those fields are rewritten on the live object from a prefix on a plain
// synchronous instance method the lobby calls between constructing the interface and
// connecting it. Three things that do not work and are deliberately not attempted here:
// patching the async Connect methods at all (merely attaching a patch breaks the Il2Cpp
// state machine), patching constructors (silently dropped), and replacing a ref object
// parameter in a prefix (does not reach the native call).
public class Main : BloonsTD6Mod
{
    public static readonly ModSettingBool LanMode = new(false)
    {
        displayName = "LAN mode",
        description = "Sends match traffic to the relay below instead of Ninja Kiwi's."
    };

    public static readonly ModSettingString RelayIP = new("127.0.0.1")
    {
        displayName = "Relay address",
        description = "The LAN address of the machine running btd6relay. Same value on every machine."
    };

    public static readonly ModSettingInt RelayPort = new(1445)
    {
        displayName = "Relay port",
        min = 1,
        max = 65535,
        slider = false
    };

    public override void OnApplicationStart()
    {
        ModHelper.Msg<Main>(LanMode
            ? $"LAN mode on, match traffic will go to {(string) RelayIP}:{(int) RelayPort}"
            : "LAN mode off, co-op will use Ninja Kiwi's relay");
    }

    private static void Swap(NKMultiGameInterface gi, string where)
    {
        if (!LanMode || gi == null) return;

        try
        {
            string ip = RelayIP;
            if (gi.RelayHostName == ip) return;

            ModHelper.Msg<Main>($"[{where}] {gi.RelayHostName}:{gi.RelayPort} match={gi.MatchID} -> {ip}:{(int) RelayPort}");
            gi.RelayHostName = ip;
            gi.RelayPort = RelayPort;
        }
        catch (Exception e)
        {
            ModHelper.Error<Main>($"[{where}] relay swap failed: {e}");
        }
    }

    [HarmonyPatch(typeof(NKMultiGameInterface), nameof(NKMultiGameInterface.add_DisconnectedEvent))]
    private static class OnDisconnectedSub
    {
        private static void Prefix(NKMultiGameInterface __instance) => Swap(__instance, "subscribe");
    }
}
