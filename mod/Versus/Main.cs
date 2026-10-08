using System;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BTD6Versus.Main), BTD6Versus.ModHelperData.Name,
    BTD6Versus.ModHelperData.Version, BTD6Versus.ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonOptionalDependencies("BTD_Mod_Helper")]

namespace BTD6Versus;

// Step one of a versus mode: can one player push bloons at the other without the two
// simulations drifting apart?
//
// There is no networked action for spawning bloons. Calling the simulation's SpawnBloons
// locally would desync immediately, because only one machine would do it. What does travel
// is SendEmoteAction, which carries an arbitrary string and, like every gameplay action,
// runs inside the lockstep on every client at the same tick. So the send rides in that
// string: the sender asks for an emote, and every client's copy of the action spawns the
// identical bloons at the identical moment.
public class Main : BloonsTD6Mod
{
    private const string Marker = "VS|";

    private static readonly ModSettingHotkey SendKey = new(KeyCode.B, HotkeyModifier.Shift)
    {
        displayName = "Send a batch of bloons"
    };

    private static readonly ModSettingString BloonType = new("Red")
    {
        displayName = "Bloon to send",
        description = "A bloon id as the game spells it: Red, Blue, Pink, Ceramic, Moab."
    };

    private static readonly ModSettingInt BloonCount = new(10)
    {
        displayName = "How many",
        min = 1,
        max = 200,
        slider = false
    };

    private static readonly ModSettingInt SpacingMs = new(200)
    {
        displayName = "Gap between them in milliseconds",
        min = 0,
        max = 5000,
        slider = false
    };

    private static int sendsSeen;
    private static int bloonsSpawned;

    public override void OnApplicationStart()
    {
        ModHelper.Msg<Main>("Versus probe loaded, Shift+B sends bloons.");
    }

    public override void OnMatchStart()
    {
        sendsSeen = 0;
        bloonsSpawned = 0;
    }

    public override void OnUpdate()
    {
        if (!SendKey.JustPressed()) return;

        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame()) return;

        var payload = $"{Marker}{(string) BloonType}|{(int) BloonCount}|{(int) SpacingMs}";
        var me = inGame.bridge.GetInputId();
        inGame.bridge.SendEmote(me, me, payload);
        ModHelper.Msg<Main>($"[vs] player {me} asked to send {payload}");
    }

    [HarmonyPatch(typeof(UnityToSimulation.SendEmoteAction), nameof(UnityToSimulation.SendEmoteAction.Run))]
    private static class SendEmotePatch
    {
        /// Runs on every client at the same simulation tick, which is the whole point.
        private static bool Prefix(UnityToSimulation.SendEmoteAction __instance, UnityToSimulation uts)
        {
            var emote = __instance.emoteId;
            if (emote == null || !emote.StartsWith(Marker)) return true;

            try
            {
                var parts = emote.Substring(Marker.Length).Split('|');
                var bloon = parts[0];
                var count = int.Parse(parts[1]);
                var spacing = int.Parse(parts[2]) / 1000f;

                InGame.instance.SpawnBloons(bloon, count, spacing);

                sendsSeen++;
                bloonsSpawned += count;
                ModHelper.Msg<Main>($"[vs] send {sendsSeen} applied on round {uts.GetCurrentRound() + 1}: " +
                                    $"{count} {bloon} from player {__instance.peerId}, {bloonsSpawned} sent this match");
            }
            catch (Exception e)
            {
                ModHelper.Error<Main>($"[vs] send failed, which will desync: {e}");
            }

            return false;
        }
    }
}
