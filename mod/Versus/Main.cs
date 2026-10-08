using System;
using System.Collections.Generic;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.Enums;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.Stats;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(BTD6Versus.Main), BTD6Versus.ModHelperData.Name,
    BTD6Versus.ModHelperData.Version, BTD6Versus.ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonOptionalDependencies("BTD_Mod_Helper")]

namespace BTD6Versus;

// Versus for BTD6, in the shape Bloons TD Battles had it: buy bloons, push them at the
// other player, live off the income the sends earn you.
//
// Nothing about a send travels as custom data. There is no networked action for spawning
// bloons, so a local spawn would desync; instead the send rides in the string that
// SendEmoteAction already carries, and that action runs inside the lockstep on every
// client at the same tick. The same goes for the money and the lives: every client derives
// them from identical events, so the two machines agree without anything extra on the wire.
public class Main : BloonsTD6Mod
{
    private const string Marker = "VS|";

    private static readonly ModSettingInt StartingLives = new(100)
    {
        displayName = "Lives each side starts with",
        min = 1,
        max = 10000,
        slider = false
    };

    private static readonly ModSettingDouble PriceScale = new(1.0)
    {
        displayName = "Price multiplier",
        description = "Battles prices are small next to BTD6 cash. Raise this to make sends bite.",
        minValue = 0.1,
        maxValue = 50
    };

    private static readonly ModSettingBool ShowPanel = new(true)
    {
        displayName = "Show the send panel in game"
    };

    private static int sendsSeen;
    private static int loser;

    private static readonly Dictionary<int, int> Lives = new();
    private static readonly Dictionary<int, int> Income = new();
    private static readonly Dictionary<IntPtr, int> SentBloonOwner = new();
    private static readonly List<PendingSend> Pending = new();

    private sealed class PendingSend(int sender, string bloon, int remaining)
    {
        public int Sender { get; } = sender;
        public string Bloon { get; } = bloon;
        public int Remaining { get; set; } = remaining;
    }

    private static GameObject hudObject;
    private static ModHelperText scoreText;
    private static readonly List<ModHelperText> ButtonCosts = new();

    public override void OnApplicationStart() => ModHelper.Msg<Main>("Versus loaded.");

    public override void OnMatchStart()
    {
        sendsSeen = 0;
        loser = 0;
        Lives.Clear();
        Income.Clear();
        SentBloonOwner.Clear();
        Pending.Clear();
    }

    public override void OnMatchEnd() => Teardown();

    public override void OnMainMenu() => Teardown();

    /// Income arrives in a hook the simulation drives, so every client pays the same
    /// players the same cash on the same tick.
    public override void OnRoundStart()
    {
        var simulation = Sim;
        if (simulation == null) return;

        foreach (var entry in Income)
        {
            if (entry.Value <= 0) continue;

            var wallet = simulation.GetCashManager(entry.Key)?.cash;
            if (wallet == null) continue;

            wallet.Value += entry.Value;
            ModHelper.Msg<Main>($"[vs] player {entry.Key} earns {entry.Value}, now on {(long) wallet.Value}");
        }
    }

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

        var victim = Opponent(sender);
        var damage = (int) Math.Max(1, bloon.bloonModel.leakDamage);
        Lives[victim] = LivesOf(victim) - damage;

        ModHelper.Msg<Main>($"[vs] player {victim} leaked a sent {bloon.bloonModel?.id} from player {sender}, " +
                            $"-{damage}, now on {LivesOf(victim)}");

        if (LivesOf(victim) > 0 || loser != 0) return;

        loser = victim;
        ModHelper.Msg<Main>($"[vs] player {victim} is out, player {sender} wins");
    }

    public override void OnUpdate() => UpdateUi();

    private static void Send(int index)
    {
        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame()) return;

        var me = inGame.bridge.GetInputId();
        inGame.bridge.SendEmote(me, me, $"{Marker}{index}");
    }

    private static int CostOf(SendType send) => (int) Math.Round(send.Cost * (double) PriceScale);

    private static int LivesOf(int player) => Lives.TryGetValue(player, out var lives) ? lives : StartingLives;

    private static int IncomeOf(int player) => Income.TryGetValue(player, out var income) ? income : 0;

    private static Il2CppAssets.Scripts.Simulation.Simulation Sim => InGame.instance?.bridge?.Simulation;

    private static int Opponent(int sender) => sender == 1 ? 2 : 1;

    [HarmonyPatch(typeof(UnityToSimulation.SendEmoteAction), nameof(UnityToSimulation.SendEmoteAction.Run))]
    private static class SendEmotePatch
    {
        /// Runs on every client at the same simulation tick, which is what keeps the two
        /// games agreeing about who paid what and what got spawned.
        private static bool Prefix(UnityToSimulation.SendEmoteAction __instance, UnityToSimulation uts)
        {
            var emote = __instance.emoteId;
            if (emote == null || !emote.StartsWith(Marker)) return true;

            try
            {
                var index = int.Parse(emote.Substring(Marker.Length));
                var send = SendType.All[index];
                var sender = __instance.peerId;
                var cost = CostOf(send);
                var wallet = Sim?.GetCashManager(sender)?.cash;

                if (loser != 0) return false;

                if (wallet == null || wallet.Value < cost)
                {
                    ModHelper.Msg<Main>($"[vs] player {sender} cannot afford {send.Label} at {cost}");
                    return false;
                }

                wallet.Value -= cost;
                Income[sender] = IncomeOf(sender) + send.Income;
                InGame.instance.SpawnBloons(send.Bloon, send.Count, 0.3f);
                Pending.Add(new PendingSend(sender, send.Bloon, send.Count));

                sendsSeen++;
                ModHelper.Msg<Main>($"[vs] send {sendsSeen} on round {uts.GetCurrentRound() + 1}: " +
                                    $"player {sender} paid {cost} for {send.Count} {send.Label}, " +
                                    $"income now {IncomeOf(sender)}");
            }
            catch (Exception e)
            {
                ModHelper.Error<Main>($"[vs] send failed, which will desync: {e}");
            }

            return false;
        }
    }

    private static void UpdateUi()
    {
        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame() || !ShowPanel)
        {
            Teardown();
            return;
        }

        if (hudObject == null) Build();

        var me = inGame.bridge.GetInputId();
        var them = Opponent(me);

        if (loser != 0)
        {
            scoreText.SetText(loser == me ? "DEFEAT" : "VICTORY");
            scoreText.Text.color = loser == me ? new Color(1f, 0.45f, 0.45f) : new Color(0.55f, 1f, 0.55f);
        }
        else
        {
            scoreText.SetText($"You  {LivesOf(me)} lives   +{IncomeOf(me)}        " +
                              $"Them  {LivesOf(them)} lives   +{IncomeOf(them)}");
            scoreText.Text.color = Color.white;
        }

        var cash = Sim?.GetCashManager(me)?.cash?.Value ?? 0;
        for (var i = 0; i < ButtonCosts.Count; i++)
        {
            var cost = CostOf(SendType.All[i]);
            ButtonCosts[i].SetText(CashDisplay.LocalizeAndFormatCash(cost));
            ButtonCosts[i].Text.color = cash >= cost ? Color.white : new Color(1f, 0.5f, 0.5f);
        }
    }

    /// Built from the game's own art: vanilla panel and button sprites, and each bloon's
    /// own icon straight off its model, so it reads as part of the game rather than an
    /// overlay bolted on top.
    private static void Build()
    {
        hudObject = new GameObject("VersusUi");
        var canvas = hudObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;

        var scaler = hudObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var scorePanel = hudObject.AddModHelperPanel(
            new Info("VersusScore", 0, -90, 1100, 110, new Vector2(0.5f, 1)), VanillaSprites.MainBGPanelBlue);
        scoreText = scorePanel.AddText(new Info("VersusScoreText", InfoPreset.FillParent), "", 42);

        var bar = hudObject.AddModHelperPanel(
            new Info("VersusSends", 0, 150, 1500, 210, new Vector2(0.5f, 0)),
            VanillaSprites.MainBGPanelBlue, RectTransform.Axis.Horizontal, 10, 20);

        ButtonCosts.Clear();
        var model = Game.instance.model;
        for (var i = 0; i < SendType.All.Length; i++)
        {
            var send = SendType.All[i];
            var index = i;

            var cell = bar.AddPanel(new Info($"Send{i}", 140, 170), null, RectTransform.Axis.Vertical, 2);
            var button = cell.AddButton(new Info($"SendBtn{i}", 130, 110), VanillaSprites.BlueInsertPanelRound,
                new Action(() => Send(index)));

            var bloon = model?.GetBloon(send.Bloon);
            if (bloon?.icon != null)
            {
                button.AddImage(new Info($"SendIcon{i}", 90), bloon.icon.GetGUID());
            }
            else
            {
                button.AddText(new Info($"SendName{i}", 120, 60), send.Label, 28);
            }

            cell.AddText(new Info($"SendLabel{i}", 140, 26), $"x{send.Count}  +{send.Income}", 22);
            ButtonCosts.Add(cell.AddText(new Info($"SendCost{i}", 140, 30), "", 26));
        }
    }

    private static void Teardown()
    {
        if (hudObject != null) UnityEngine.Object.Destroy(hudObject);
        hudObject = null;
        scoreText = null;
        ButtonCosts.Clear();
    }
}
