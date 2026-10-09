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
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Models.Rounds;
using Il2CppAssets.Scripts.Models.TowerSets;
using Il2CppAssets.Scripts.Simulation.Input;
using Il2CppAssets.Scripts.Simulation.Track;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
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

    /// Emission indices this high are ours. The sender is folded into the number so the
    /// spawner can tell whose bloon it is about to emit, and send it down their lane.
    private const int VersusEmissionBase = 900000;
    private const int PerPlayerBlock = 10000;

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

    private static readonly ModSettingBool BanFarmsAndHeroes = new(true)
    {
        displayName = "No farms, heroes or insta monkeys",
        description = "Versus income comes from sending bloons, so the other routes are shut."
    };

    private static readonly ModSettingBool VersusMode = new(true)
    {
        displayName = "Versus mode",
        description = "Turns the powers tab into the bloon send tab. Off gives the powers back."
    };

    private static readonly ModSettingInt PanelY = new(30)
    {
        displayName = "Send panel height above the bottom edge",
        min = 0,
        max = 900,
        slider = false
    };

    private static readonly ModSettingInt SpacingTicks = new(12)
    {
        displayName = "Gap between sent bloons, in simulation ticks",
        description = "The simulation runs sixty ticks a second, so twelve is a fifth of a " +
                      "second between one bloon and the next.",
        min = 1,
        max = 300,
        slider = false
    };

    private static readonly ModSettingBool OppositeSides = new(true)
    {
        displayName = "Sends arrive from the other player's end",
        description = "Only means anything on a map with more than one lane."
    };

    private static readonly ModSettingBool SwapLanes = new(false)
    {
        displayName = "Swap which lane belongs to which player",
        description = "If sends are arriving at the wrong end, turn this on."
    };

    private static readonly ModSettingInt LivesY = new(230)
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
    private static readonly List<ModHelperText> ButtonCosts = new();
    private static GameObject hudObject;

    /// Sends are released one bloon at a time by the simulation's own clock. Doing it by
    /// frame would drift between machines; the spawner's tick does not.
    private sealed class Release(int sender, BloonModel bloon, PathSegment lane, int remaining, int nextTick)
    {
        public int Sender { get; } = sender;
        public BloonModel Bloon { get; } = bloon;
        public PathSegment Lane { get; } = lane;
        public int Remaining { get; set; } = remaining;
        public int NextTick { get; set; } = nextTick;
    }

    private static readonly List<Release> Releases = new();
    private static int simTick;

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

    public override void OnTowerInventoryInitialized(TowerInventory towerInventory,
        System.Collections.Generic.List<Il2CppAssets.Scripts.Models.TowerSets.TowerDetailsModel> allTowersInTheGame)
    {
        if (!VersusMode || !BanFarmsAndHeroes || towerInventory is null) return;

        var banned = new Il2CppSystem.Collections.Generic.List<string>();
        banned.Add("BananaFarm");

        var model = InGame.instance?.bridge?.Model ?? Game.instance.model;
        if (model is not null)
        {
            foreach (var hero in model.heroSet) banned.Add(hero.towerId);
        }

        towerInventory.DisableTowers(banned.TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
        ModHelper.Msg<Main>($"[vs] {banned.Count} towers disabled: farms and every hero");
    }

    public override void OnUpdate() => UpdateUi();

    private static void Send(int index)
    {
        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame()) return;

        var me = inGame.bridge.GetInputId();

        // With another player there, the send has to travel as an action so both
        // simulations do it on the same tick. On your own there is nobody to stay in step
        // with, so it just happens, which makes the whole thing testable in single player.
        if (inGame.bridge.TryCast<NetworkedUnityToSimulation>() is not null)
        {
            inGame.bridge.SendEmote(me, me, $"{Marker}{index}");
            return;
        }

        ApplySend(me, index, inGame.bridge.GetCurrentRound());
    }

    private static void ApplySend(int sender, int index, int round)
    {
        if (loser != 0) return;

        var send = SendType.All[index];
        var cost = CostOf(send);
        var wallet = Sim?.GetCashManager(sender)?.cash;

        if (wallet is null || wallet.Value < cost)
        {
            ModHelper.Msg<Main>($"[vs] player {sender} cannot afford {send.Label} at {cost}");
            return;
        }

        wallet.Value -= cost;
        Income[sender] = IncomeOf(sender) + send.Income;
        SpawnSpaced(sender, send, round);

        sendsSeen++;
        ModHelper.Msg<Main>($"[vs] send {sendsSeen} on round {round + 1}: player {sender} paid " +
                            $"{cost} for {send.Count} {send.Label}, income now {IncomeOf(sender)}");
    }

    /// Queues the batch. Nothing is emitted here: the bloons go out one per tick interval
    /// from the spawner's own clock, which is the only way to space them that both machines
    /// agree on, and the only thing that visibly strings a send out.
    private static void SpawnSpaced(int sender, SendType send, int round)
    {
        var bridge = InGame.instance.bridge;
        var spawner = bridge.Simulation?.Map?.spawner;
        var model = bridge.Model ?? Game.instance.model;
        var bloonModel = model is null ? null : model.GetBloon(send.Bloon);

        if (spawner is null || bloonModel is null)
        {
            ModHelper.Warning<Main>($"[vs] cannot emit {send.Label}: spawner " +
                                    $"{(spawner is null ? "missing" : "found")}, model " +
                                    $"{(bloonModel is null ? "missing" : "found")}");
            return;
        }

        var lane = OppositeSides ? LaneFor(sender) : null;
        Releases.Add(new Release(sender, bloonModel, lane, send.Count, simTick + 1));

        ModHelper.Msg<Main>($"[vs] queued {send.Count} {send.Label} for player {sender}, " +
                            $"one every {(int) SpacingTicks} ticks, lane " +
                            $"{(lane is null ? "default" : "redirected")}");
    }

    /// Runs once per simulation tick on every machine, so releasing from here keeps the
    /// two games in step.
    [HarmonyPatch(typeof(Spawner), nameof(Spawner.Process))]
    private static class ProcessPatch
    {
        private static void Postfix(Spawner __instance)
        {
            simTick++;
            if (Releases.Count == 0) return;

            try
            {
                for (var i = Releases.Count - 1; i >= 0; i--)
                {
                    var release = Releases[i];
                    if (simTick < release.NextTick) continue;

                    if (release.Lane is not null) __instance.spawnOverrideThisFrame = release.Lane;

                    var bloon = __instance.Emit(release.Bloon, __instance.CurrentRound,
                        VersusEmissionBase + release.Sender * PerPlayerBlock + release.Remaining, 0, false);

                    if (bloon is not null) SentBloonOwner[bloon.Pointer] = release.Sender;

                    release.Remaining--;
                    release.NextTick = simTick + (int) SpacingTicks;
                    if (release.Remaining <= 0) Releases.RemoveAt(i);
                }
            }
            catch (Exception e)
            {
                Releases.Clear();
                ModHelper.Error<Main>($"[vs] releasing a send failed: {e}");
            }
        }
    }

    /// Every lane the map has, not just the one this round happens to use, which is what
    /// GetSpawnPathsForRound reports and why two sided maps looked single laned.
    private static PathSegment LaneFor(int sender)
    {
        var paths = InGame.instance?.bridge?.Simulation?.Map?.pathManager?.paths;
        if (paths is null || paths.Count == 0) return null;

        var usable = new List<Path>();
        foreach (var path in paths)
        {
            if (path is null || path.segments is null || path.segments.Length == 0) continue;
            if (path.isHidden) continue;
            usable.Add(path);
        }

        if (usable.Count == 0) return null;

        var target = Opponent(sender);
        var first = target <= 1;
        if (SwapLanes) first = !first;

        var path2 = usable.Count < 2 ? usable[0] : usable[first ? 0 : 1];
        return path2.segments[0];
    }

    private static int CostOf(SendType send) => (int) Math.Round(send.Cost * (double) PriceScale);

    private static int LivesOf(int player) => Lives.TryGetValue(player, out var lives) ? lives : StartingLives;

    private static int IncomeOf(int player) => Income.TryGetValue(player, out var income) ? income : 0;

    private static Il2CppAssets.Scripts.Simulation.Simulation Sim => InGame.instance?.bridge?.Simulation;

    /// Two sided for now. On your own this still names a player 2, which is what makes a
    /// single player game a usable test: your sends land on an imaginary opponent whose
    /// lives you can watch fall.
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
                ApplySend(__instance.peerId, int.Parse(emote.Substring(Marker.Length)), uts.GetCurrentRound());
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
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame() || !VersusMode)
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

            BuildLives(health);
            built = true;
        }
        catch (Exception e)
        {
            built = true;
            ModHelper.Error<Main>($"[vs] building the panel failed: {e}");
        }
    }

    /// The powers tab builds its own buttons and then hides and rebuilds them whenever it
    /// opens, so the only way to live in it is to be part of that build. The game lays each
    /// button out through GetNextButton; its insides are replaced with a bloon and a price.
    // PowersMenu declares its own LoadPowers and RebuildPowers that shadow the base ones,
    // so patching only the base built the buttons once and then let the real build wipe them.
    [HarmonyPatch(typeof(BasePowersMenu), nameof(BasePowersMenu.LoadPowers))]
    private static class BaseLoadPatch
    {
        private static void Postfix(BasePowersMenu __instance) => FillWithSends(__instance);
    }

    [HarmonyPatch(typeof(BasePowersMenu), nameof(BasePowersMenu.RebuildPowers))]
    private static class BaseRebuildPatch
    {
        private static void Postfix(BasePowersMenu __instance) => FillWithSends(__instance);
    }

    [HarmonyPatch(typeof(PowersMenu), nameof(PowersMenu.LoadPowers))]
    private static class LoadPatch
    {
        private static void Postfix(PowersMenu __instance) => FillWithSends(__instance);
    }

    [HarmonyPatch(typeof(PowersMenu), nameof(PowersMenu.RebuildPowers))]
    private static class RebuildPatch
    {
        private static void Postfix(PowersMenu __instance) => FillWithSends(__instance);
    }

    [HarmonyPatch(typeof(PowersMenu), nameof(PowersMenu.ShowAll))]
    private static class ShowAllPatch
    {
        private static void Postfix(PowersMenu __instance) => FillWithSends(__instance);
    }

    /// The powers list is Mask/Sorter inside the menu. The insta monkeys scroll has a grid
    /// of its own, which is the one a naive search finds first, so match on the parent.
    private static Transform FindPowersGrid(BasePowersMenu menu)
    {
        foreach (var candidate in menu.GetComponentsInChildren<GridLayoutGroup>(true))
        {
            if (candidate is null) continue;

            var parent = candidate.transform.parent;
            if (parent is not null && parent.name == "Mask") return candidate.transform;
        }

        return null;
    }

    /// Insta monkeys are a second income-free supply of defence, and they live behind a
    /// button in this same tab, so the button goes.
    private static void HideInstaMonkeys(BasePowersMenu menu)
    {
        if (!BanFarmsAndHeroes) return;

        foreach (var child in menu.GetComponentsInChildren<RectTransform>(true))
        {
            if (child is null) continue;
            if (child.name != "ShowInstaMonkeysButton" && child.name != "InstaTowersMenu") continue;

            child.gameObject.SetActive(false);
        }
    }

    private static void FillWithSends(BasePowersMenu menu)
    {
        if (!VersusMode || menu is null) return;

        try
        {
            menu.ClearButtons();
            ButtonCosts.Clear();

            var model = InGame.instance?.bridge?.Model ?? Game.instance.model;
            var grid = FindPowersGrid(menu);
            HideInstaMonkeys(menu);
            var withArt = 0;

            if (grid is null) ModHelper.Warning<Main>("[vs] could not find the powers grid, sends will be off screen");

            for (var i = 0; i < SendType.All.Length; i++)
            {
                var send = SendType.All[i];
                var index = i;

                var slot = menu.GetNextButton();
                if (slot is null) break;

                // The pool it comes from sits at 5000,5000 and is switched off; the game
                // re-parents a button into the grid after building it, so this has to too.
                if (grid is not null)
                {
                    slot.transform.SetParent(grid, false);
                    slot.transform.localScale = Vector3.one;
                }

                slot.SetActive(true);

                // Keep the game's placement, replace what is drawn inside it.
                var holder = slot.transform;
                for (var child = holder.childCount - 1; child >= 0; child--)
                {
                    UnityEngine.Object.Destroy(holder.GetChild(child).gameObject);
                }

                foreach (var old in slot.GetComponents<PowerButton>())
                {
                    if (old is not null) UnityEngine.Object.Destroy(old);
                }

                var panel = slot.AddModHelperPanel(new Info($"Send{i}", InfoPreset.FillParent), null,
                    RectTransform.Axis.Vertical, 0);

                var bloon = model is null ? null : model.GetBloon(send.Bloon);
                var icon = bloon is null ? null : bloon.icon;
                var guid = icon is null ? null : icon.GetGUID();

                var button = panel.AddButton(new Info($"SendBtn{i}", 170, 170),
                    VanillaSprites.BlueInsertPanelRound, new Action(() => Send(index)));

                if (string.IsNullOrEmpty(guid))
                {
                    button.AddText(new Info($"SendName{i}", 150, 80), send.Label, 30);
                }
                else
                {
                    button.AddImage(new Info($"SendIcon{i}", 140), guid);
                    withArt++;
                }

                ButtonCosts.Add(panel.AddText(new Info($"SendCost{i}", 200, 40), "", 32));
                panel.AddText(new Info($"SendInfo{i}", 200, 32), $"x{send.Count}  +{send.Income}", 24);
            }

            ModHelper.Msg<Main>($"[vs] powers tab filled with {ButtonCosts.Count} sends, " +
                                $"{withArt} showing bloon art");
        }
        catch (Exception e)
        {
            ModHelper.Error<Main>($"[vs] filling the powers tab failed: {e}");
        }
    }

    /// Built rather than cloned: the real lives widget carries a heart sized and anchored
    /// for the top bar, and copies of it hung off the edge of the screen.
    private static void BuildLives(HealthDisplay health)
    {
        var column = hudObject.AddModHelperPanel(
            new Info("VersusLives", 250, -LivesY, 440, 200, new Vector2(0, 1)), null,
            RectTransform.Axis.Vertical, 10);

        // The top bar's lives widget sits on a sprite drawn for exactly this size; borrowing
        // it beats stretching a panel sprite until the pixels show.
        var group = health.transform.parent is null ? null : health.transform.parent.GetComponent<Image>();
        var backing = group is null ? null : group.sprite;

        p1Text = BuildCounter(column, "You", backing);
        p2Text = BuildCounter(column, "Them", backing);
    }

    private static NK_TextMeshProUGUI BuildCounter(ModHelperPanel column, string name, Sprite backing)
    {
        var row = column.AddPanel(new Info($"Versus{name}", 440, 88),
            VanillaSprites.BlueInsertPanelRound, RectTransform.Axis.Horizontal, 10, 10);

        if (backing is not null)
        {
            row.Background.sprite = backing;
            row.Background.type = Image.Type.Sliced;
            row.Background.pixelsPerUnitMultiplier = 0.5f;
        }

        row.AddImage(new Info($"Versus{name}Icon", 64), VanillaSprites.LivesIcon);
        return row.AddText(new Info($"Versus{name}Text", 330, 64), "", 36).Text;
    }

    private static void Teardown()
    {
        if (hudObject is not null) UnityEngine.Object.Destroy(hudObject);
        hudObject = null;
        ButtonCosts.Clear();
        p1Text = null;
        p2Text = null;
        built = false;
    }
}
