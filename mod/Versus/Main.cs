using System;
using System.Collections.Generic;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

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

    private static readonly ModSettingInt SendCostEach = new(25)
    {
        displayName = "Cash each sent bloon costs",
        min = 0,
        max = 100000,
        slider = false
    };

    private static readonly ModSettingInt EcoPerSend = new(10)
    {
        displayName = "Income each send adds",
        description = "Paid out to the sender at the start of every round, Battles style.",
        min = 0,
        max = 10000,
        slider = false
    };

    private static readonly ModSettingInt StartingLives = new(100)
    {
        displayName = "Lives each side starts with",
        min = 1,
        max = 10000,
        slider = false
    };

    private static int sendsSeen;
    private static int bloonsSpawned;

    // Every client runs the same hooks in the same order inside the lockstep, so both
    // machines arrive at the same numbers without any of this being sent anywhere.
    private static readonly Dictionary<int, int> Lives = new();
    private static readonly Dictionary<IntPtr, int> SentBloonOwner = new();
    private static readonly List<PendingSend> Pending = new();

    private sealed class PendingSend(int sender, string bloon, int remaining)
    {
        public int Sender { get; } = sender;
        public string Bloon { get; } = bloon;
        public int Remaining { get; set; } = remaining;
    }

    private static readonly Dictionary<int, int> Eco = new();
    private static int loser;

    private static GameObject hudObject;
    private static ModHelperText hudText;

    public override void OnApplicationStart()
    {
        ModHelper.Msg<Main>("Versus probe loaded, Shift+B sends bloons.");
    }

    public override void OnMatchStart()
    {
        sendsSeen = 0;
        bloonsSpawned = 0;
        Lives.Clear();
        Eco.Clear();
        SentBloonOwner.Clear();
        Pending.Clear();
        loser = 0;
    }

    /// Income is paid inside a hook the simulation drives, so every client pays the same
    /// players the same cash on the same tick without anything crossing the wire.
    public override void OnRoundStart()
    {
        if (!InVersusGame(out var simulation)) return;

        foreach (var entry in Eco)
        {
            if (entry.Value <= 0) continue;

            var wallet = simulation.GetCashManager(entry.Key)?.cash;
            if (wallet == null) continue;

            wallet.Value += entry.Value;
            ModHelper.Msg<Main>($"[vs] player {entry.Key} earns {entry.Value} income, now on {(long) wallet.Value}");
        }
    }

    public override void OnMatchEnd() => Teardown();

    public override void OnMainMenu() => Teardown();

    /// A bloon that arrives right after a send, of the type that was sent, belongs to that
    /// send. Sends are explicit and bursty, so taking them in order is enough; a bloon that
    /// splits into children only counts for its own layer, which is a known gap.
    public override void OnBloonCreated(Bloon bloon)
    {
        if (Pending.Count == 0) return;

        var id = bloon.bloonModel?.id;
        for (var i = 0; i < Pending.Count; i++)
        {
            var send = Pending[i];
            if (send.Bloon != id) continue;

            SentBloonOwner[bloon.Pointer] = send.Sender;
            send.Remaining--;
            if (send.Remaining <= 0) Pending.RemoveAt(i);
            return;
        }
    }

    public override void PostBloonLeaked(Bloon bloon)
    {
        if (!SentBloonOwner.Remove(bloon.Pointer, out var sender)) return;

        // A sent bloon that gets through costs the player it was aimed at, not the sender.
        var victim = Opponent(sender);
        var damage = (int) Math.Max(1, bloon.bloonModel.leakDamage);
        Lives[victim] = LivesOf(victim) - damage;

        ModHelper.Msg<Main>($"[vs] player {victim} leaked a sent {bloon.bloonModel?.id} from player {sender}, " +
                            $"-{damage}, now on {LivesOf(victim)}");

        if (LivesOf(victim) > 0 || loser != 0) return;

        loser = victim;
        ModHelper.Msg<Main>($"[vs] player {victim} is out, player {sender} wins");
    }

    public override void OnUpdate()
    {
        UpdateHud();
        SendOnHotkey();
    }

    /// Its own overlay canvas, the same shape as the co-op HUD, because the vanilla lives
    /// counter still shows the shared pool and means nothing here.
    private static void UpdateHud()
    {
        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame())
        {
            Teardown();
            return;
        }

        if (hudObject == null)
        {
            hudObject = new GameObject("VersusLives");
            var canvas = hudObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            var scaler = hudObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            hudText = hudObject.AddText(new Info("VersusLivesText", 50, -140, 700, 48, new Vector2(0, 1)),
                "", 40, Il2CppTMPro.TextAlignmentOptions.Left);
        }

        var me = inGame.bridge.GetInputId();
        var them = Opponent(me);

        if (loser != 0)
        {
            hudText.SetText(loser == me ? "you lose" : "you win");
            hudText.Text.color = loser == me
                ? new Color(1f, 0.4f, 0.4f, 0.95f)
                : new Color(0.5f, 1f, 0.5f, 0.95f);
            return;
        }

        hudText.SetText($"you {LivesOf(me)} lives, {EcoOf(me)} income" +
                        $"      them {LivesOf(them)} lives, {EcoOf(them)} income");
        hudText.Text.color = new Color(1f, 1f, 1f, 0.75f);
    }

    private static void Teardown()
    {
        if (hudObject != null) UnityEngine.Object.Destroy(hudObject);
        hudObject = null;
        hudText = null;
    }

    private static int LivesOf(int player) => Lives.TryGetValue(player, out var lives) ? lives : StartingLives;

    private static int EcoOf(int player) => Eco.TryGetValue(player, out var eco) ? eco : 0;

    private static Il2CppAssets.Scripts.Simulation.Simulation Sim =>
        InGame.instance?.bridge?.Simulation;

    private static bool InVersusGame(out Il2CppAssets.Scripts.Simulation.Simulation simulation)
    {
        simulation = Sim;
        return simulation != null;
    }

    /// Two sided for now: whoever is not the sender.
    private static int Opponent(int sender) => sender == 1 ? 2 : 1;

    private static void SendOnHotkey()
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

                var sender = __instance.peerId;
                var cost = count * (int) SendCostEach;
                var wallet = Sim?.GetCashManager(sender)?.cash;

                if (loser != 0)
                {
                    ModHelper.Msg<Main>("[vs] the match is over, send ignored");
                    return false;
                }

                if (wallet == null || wallet.Value < cost)
                {
                    ModHelper.Msg<Main>($"[vs] player {sender} cannot afford {cost}, send refused");
                    return false;
                }

                wallet.Value -= cost;
                Eco[sender] = EcoOf(sender) + (int) EcoPerSend;

                InGame.instance.SpawnBloons(bloon, count, spacing);
                Pending.Add(new PendingSend(sender, bloon, count));

                sendsSeen++;
                bloonsSpawned += count;
                ModHelper.Msg<Main>($"[vs] send {sendsSeen} applied on round {uts.GetCurrentRound() + 1}: " +
                                    $"{count} {bloon} from player {sender} for {cost}, " +
                                    $"income now {EcoOf(sender)}, {bloonsSpawned} sent this match");
            }
            catch (Exception e)
            {
                ModHelper.Error<Main>($"[vs] send failed, which will desync: {e}");
            }

            return false;
        }
    }
}
