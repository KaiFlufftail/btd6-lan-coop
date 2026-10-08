using System;
using System.Collections.Generic;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.Enums;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2Cpp;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.BloonMenu;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.RightMenu.Powers;
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

    private static readonly ModSettingInt PanelY = new(40)
    {
        displayName = "Send panel height above the bottom edge",
        min = 0,
        max = 900,
        slider = false
    };

    private static readonly ModSettingInt LivesY = new(240)
    {
        displayName = "Lives panel distance below the top edge",
        min = 0,
        max = 900,
        slider = false
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

    private static readonly List<ModHelperText> ButtonCosts = new();
    private static GameObject hudObject;
    private static readonly List<SpawnBloonButton> SpawnButtons = new();

    private static NK_TextMeshProUGUI p1Text;
    private static NK_TextMeshProUGUI p2Text;
    private static bool built;

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

        if (!built) Build();
        if (!built) return;

        var me = inGame.bridge.GetInputId();
        var them = Opponent(me);

        if (p1Text is not null) p1Text.text = Readout(me, me);
        if (p2Text is not null) p2Text.text = Readout(them, me);

        // The cloned buttons keep their own interactable logic, which is written for
        // sandbox and would grey them out here.
        foreach (var spawnButton in SpawnButtons)
        {
            if (spawnButton is not null && spawnButton.Button is not null) spawnButton.Button.interactable = true;
        }

        var cash = Sim?.GetCashManager(me)?.cash?.Value ?? 0;
        for (var i = 0; i < ButtonCosts.Count; i++)
        {
            var cost = CostOf(SendType.All[i]);
            ButtonCosts[i].SetText(CashDisplay.LocalizeAndFormatCash(cost));
            ButtonCosts[i].Text.color = cash >= cost ? Color.white : new Color(1f, 0.45f, 0.45f);
        }
    }

    private static string Readout(int player, int me)
    {
        var who = player == me ? "You" : "Them";
        if (loser == player) return $"{who} out";
        return $"{who} {LivesOf(player)}  +{IncomeOf(player)}";
    }

    /// FindObjectOfType only sees active objects, and much of the game's interface is
    /// switched off until opened, so everything here has to search the inactive ones too.
    private static T FindAnywhere<T>() where T : Component
    {
        foreach (var found in UnityEngine.Resources.FindObjectsOfTypeAll<T>())
        {
            if (found is not null && found.gameObject.scene.IsValid()) return found;
        }

        return null;
    }

    /// Everything lives on the mod's own canvas. An earlier version put the buttons inside
    /// the powers menu and cloned the lives counter into the top bar's horizontal layout
    /// group; the buttons ended up hidden in the insta monkeys scroll and the counters laid
    /// out sideways. The game's art is reused, its containers are not.
    private static void Build()
    {
        var health = FindAnywhere<HealthDisplay>();
        if (health is null)
        {
            if (Time.frameCount % 300 == 0) ModHelper.Warning<Main>("[vs] waiting for the health display");
            return;
        }

        try
        {
            hudObject = new GameObject("VersusUi");
            var canvas = hudObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;

            var scaler = hudObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            BuildSends();
            BuildLives(health);
            built = true;
        }
        catch (Exception e)
        {
            built = true;
            ModHelper.Error<Main>($"[vs] building the panel failed: {e}");
        }
    }

    private static void BuildSends()
    {
        var model = InGame.instance.bridge?.Model ?? Game.instance.model;
        var bloonMenu = FindAnywhere<BloonMenu>();
        var prefab = bloonMenu is null ? null : bloonMenu.spawnBloonButtonPrefab;

        ButtonCosts.Clear();
        SpawnButtons.Clear();

        var bar = hudObject.AddModHelperPanel(
            new Info("VersusSends", 0, PanelY, 1260, 170, new Vector2(0.5f, 0)),
            VanillaSprites.MainBGPanelBlue, RectTransform.Axis.Horizontal, 6, 12);

        for (var i = 0; i < SendType.All.Length; i++)
        {
            var send = SendType.All[i];
            var index = i;
            var bloon = model is null ? null : model.GetBloon(send.Bloon);

            var cell = bar.AddPanel(new Info($"Send{i}", 118, 146), null, RectTransform.Axis.Vertical, 0);

            var spawnButton = prefab is null
                ? null
                : UnityEngine.Object.Instantiate(prefab, cell.transform).GetComponent<SpawnBloonButton>();

            if (spawnButton is not null && bloon is not null)
            {
                spawnButton.SetBloon(bloon, bloonMenu.bloonCount, bloonMenu.bloonRate, bloonMenu.roundDetails);

                var rect = spawnButton.GetComponent<RectTransform>();
                if (rect is not null) rect.sizeDelta = new Vector2(104, 104);

                // Its own handler spawns locally, which would desync at once.
                var button = spawnButton.Button;
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(new Action(() => Send(index)));
                SpawnButtons.Add(spawnButton);
            }
            else
            {
                ModHelper.Warning<Main>($"[vs] no game button for {send.Label}: prefab " +
                                        $"{(prefab is null ? "missing" : "found")}, bloon model " +
                                        $"{(bloon is null ? "missing" : "found")}");

                var fallback = cell.AddButton(new Info($"SendBtn{i}", 104, 104),
                    VanillaSprites.BlueInsertPanelRound, new Action(() => Send(index)));
                fallback.AddText(new Info($"SendName{i}", 100, 50), send.Label, 22);
            }

            ButtonCosts.Add(cell.AddText(new Info($"SendCost{i}", 118, 26), "", 24));
            cell.AddText(new Info($"SendInfo{i}", 118, 20), $"x{send.Count}  +{send.Income}", 18);
        }

        ModHelper.Msg<Main>($"[vs] send panel built, {SpawnButtons.Count} of {SendType.All.Length} " +
                            "buttons using the game's own bloon art");
    }

    /// Two copies of the real lives widget in the mod's own container, stacked, rather than
    /// dropped into the top bar's horizontal layout where they would sit side by side.
    private static void BuildLives(HealthDisplay health)
    {
        var group = health.transform.parent;
        var column = hudObject.AddModHelperPanel(
            new Info("VersusLives", 150, -LivesY, 520, 320, new Vector2(0, 1)), null,
            RectTransform.Axis.Vertical, 10);

        p1Text = CloneCounter(group.gameObject, column.transform, "VersusLivesP1");
        p2Text = CloneCounter(group.gameObject, column.transform, "VersusLivesP2");
    }

    private static NK_TextMeshProUGUI CloneCounter(GameObject source, Transform parent, string name)
    {
        var clone = UnityEngine.Object.Instantiate(source, parent);
        clone.name = name;
        clone.SetActive(true);

        foreach (var display in clone.GetComponentsInChildren<HealthDisplay>(true))
        {
            if (display is not null) UnityEngine.Object.Destroy(display);
        }

        var rect = clone.GetComponent<RectTransform>();
        if (rect is not null)
        {
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.anchoredPosition = Vector2.zero;
        }

        return clone.GetComponentInChildren<NK_TextMeshProUGUI>();
    }

    private static void Teardown()
    {
        if (hudObject is not null) UnityEngine.Object.Destroy(hudObject);
        hudObject = null;
        ButtonCosts.Clear();
        SpawnButtons.Clear();
        p1Text = null;
        p2Text = null;
        built = false;
    }
}
