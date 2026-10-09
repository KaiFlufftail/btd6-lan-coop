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
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Rounds;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Simulation.Objects;
using Il2CppAssets.Scripts.Models.TowerSets;
using Il2CppAssets.Scripts.Simulation.Input;
using Il2CppAssets.Scripts.Simulation.Towers;
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

    private static readonly ModSettingInt IncomeSeconds = new(6)
    {
        displayName = "Seconds between income payouts",
        description = "Six, as in Battles. Income no longer waits for the end of a round.",
        min = 1,
        max = 120,
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

    private static readonly ModSettingBool TeamImmunity = new(false)
    {
        displayName = "Your towers cannot pop the bloons you sent",
        description = "Each player's towers are put into a set of their own and a sent bloon " +
                      "is made immune to exactly that set, which is the only form the game's " +
                      "immunity test accepts. Towers still aim at bloons they cannot hurt, " +
                      "and buffs that key on Primary, Military and the rest stop applying."
    };

    private static readonly ModSettingBool NoNaturalRounds = new(true)
    {
        displayName = "No rounds of their own, only sent bloons",
        description = "The match stops sending its own waves, so the only bloons on the " +
                      "track are the ones the two of you paid for."
    };

    private static readonly ModSettingHotkey ProbeKey = new(KeyCode.J, HotkeyModifier.Shift)
    {
        displayName = "Ask the game how bloon immunity actually works"
    };

    private static readonly ModSettingInt BaseIncome = new(50)
    {
        displayName = "Income everyone earns anyway",
        description = "Paid on the same clock as send income. With the natural rounds off " +
                      "there is no end of round cash, so this is what starts the economy.",
        min = 0,
        max = 100000,
        slider = false
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
    /// Flags above Items in the game's own TowerSet enum, which nothing else uses, so a
    /// player's towers can be told apart from their opponent's by the one mechanism the
    /// game already honours: a bloon being immune to a whole set.
    private const TowerSet TeamOne = (TowerSet) 128;
    private const TowerSet TeamTwo = (TowerSet) 256;



    private static GameObject hudObject;
    private static ModHelperPanel banner;
    private static CanvasGroup hiddenLives;

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
    private static int tickFailures;


    private static NK_TextMeshProUGUI p1Text;
    private static NK_TextMeshProUGUI p2Text;
    private static bool built;

    public override void OnApplicationStart()
    {
        Diagnostics.Begin(ModHelperData.Version);
        Diagnostics.Say($"settings: versus {(bool) VersusMode}, " +
                        $"opposite sides {(bool) OppositeSides}, swap lanes {(bool) SwapLanes}, " +
                        $"team immunity {(bool) TeamImmunity}, " +
                        $"no farms or heroes {(bool) BanFarmsAndHeroes}, gap {(int) SpacingTicks} ticks, " +
                        $"income every {(int) IncomeSeconds}s, price scale {(double) PriceScale}");

        // Each of these is a place a silent failure has cost an evening before.
        Diagnostics.CheckPatch(typeof(BasePowersMenu), nameof(BasePowersMenu.LoadPowers));
        Diagnostics.CheckPatch(typeof(BasePowersMenu), nameof(BasePowersMenu.RebuildPowers));
        Diagnostics.CheckPatch(typeof(PowersMenu), nameof(PowersMenu.LoadPowers));
        Diagnostics.CheckPatch(typeof(PowersMenu), nameof(PowersMenu.RebuildPowers));
        Diagnostics.CheckPatch(typeof(PowersMenu), nameof(PowersMenu.ShowAll));
        Diagnostics.CheckPatch(typeof(Spawner), nameof(Spawner.Process));
        Diagnostics.CheckPatch(typeof(Spawner), nameof(Spawner.Emit));
        Diagnostics.CheckPatch(typeof(UnityToSimulation.SendEmoteAction),
            nameof(UnityToSimulation.SendEmoteAction.Run));
    }

    public override void OnInGameLoaded(InGame inGame) => Diagnostics.Say("in game loaded");

    public override void OnMatchStart() => Diagnostics.Guard("starting a match", MatchStart);

    private void MatchStart()
    {
        sendsSeen = 0;
        loser = 0;
        Lives.Clear();
        Income.Clear();
        SentBloonOwner.Clear();
        StopNaturalRound();
    }

    /// Versus is about the bloons the players buy, so the round's own wave is called off
    /// as it begins. Sent bloons are emitted directly and are not affected.
    private static void StopNaturalRound()
    {
        if (!VersusMode || !NoNaturalRounds) return;

        Diagnostics.Guard("calling off the round's own bloons", () =>
        {
            var spawner = InGame.instance?.bridge?.Simulation?.Map?.spawner;
            if (spawner is null) return;

            spawner.CeaseAllEmissions();
            Diagnostics.Say("the round's own bloons were called off");
        });
    }

    public override void OnMatchEnd() => Teardown();

    public override void OnMainMenu() => Teardown();

    /// Bloons are tracked by pointer and there is no hook for one being popped, so the
    /// table is emptied each round. Left to grow it would both slow the targeting check
    /// and risk crediting a new bloon that reused a dead one's address.
    /// Marking a tower as it is created does not work: its owner is not settled yet and
    /// reads back as a placeholder, which put every tower on the same team. The towers are
    /// swept instead, from the simulation's own tick so both machines do it together, and
    /// an upgrade that swaps a tower's model is picked up by the next sweep.
    private static void SweepTowerTeams()
    {
        if (!VersusMode || !TeamImmunity) return;
        if (simTick % 30 != 0) return;

        var inGame = InGame.instance;
        if (inGame is null || inGame.bridge is null || !inGame.IsInGame()) return;

        foreach (var tower in inGame.GetTowers())
        {
            if (tower is null) continue;

            var model = tower.towerModel;
            if (model is null) continue;

            var owner = tower.owner >= 1 ? tower.owner : tower.originalOwner;
            if (owner < 1) continue;

            var team = owner <= 1 ? TeamOne : TeamTwo;
            if (model.towerSet == team) continue;

            // The whole set, not a mark added to it. The game's immunity test is an exact
            // match of the bloon's immunity against the tower's set value, proven by probe:
            // a tower on 1 is stopped by an immunity of exactly 1, and not by 15 or 129
            // even though both contain it. So every tower a player owns has to carry the
            // same single value for one immunity to cover them all.
            model.towerSet = team;
            Diagnostics.Say($"tower {model.baseId} of player {owner} joined team " +
                            $"{(team == TeamOne ? 1 : 2)}, set is now {model.towerSet}");
        }
    }

    public override void OnRoundStart()
    {
        if (SentBloonOwner.Count > 0)
        {
            Diagnostics.Say($"round start: forgetting {SentBloonOwner.Count} tracked sent bloons");
        }

        SentBloonOwner.Clear();
        StopNaturalRound();
    }

    public override void PostBloonLeaked(Bloon bloon) =>
        Diagnostics.Guard("counting a leak", () => Leaked(bloon));

    /// The shared pool still drains on a leak and the game declares everyone defeated, so
    /// it is topped back up on the simulation's own tick. Each side's real lives are the
    /// mod's, and they are the only ones that end anything.
    private static void HoldSharedLives()
    {
        if (!VersusMode) return;

        var simulation = Sim;
        if (simulation is null) return;

        if (simulation.Health >= simulation.MaxHealth) return;

        simulation.SetHealthDirectlyWithoutNotify(simulation.MaxHealth);

        if (simTick % 300 == 0) Diagnostics.Say("shared lives topped back up, versus lives are the real ones");
    }

    private static void Leaked(Bloon bloon)
    {
        if (!SentBloonOwner.Remove(bloon.Pointer, out var sender)) return;

        var victim = Opponent(sender);
        var damage = (int) Math.Max(1, bloon.bloonModel.leakDamage);
        Lives[victim] = LivesOf(victim) - damage;

        Diagnostics.Say($"player {victim} leaked a sent {bloon.bloonModel?.id} from player {sender}, " +
                            $"-{damage}, now on {LivesOf(victim)}");

        if (LivesOf(victim) > 0 || loser != 0) return;

        loser = victim;
        Diagnostics.Say($"player {victim} is out, player {sender} wins the match");
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
        Diagnostics.Say($"{banned.Count} towers disabled: farms and every hero");
    }

    public override void OnUpdate()
    {
        Diagnostics.Guard("drawing the interface", UpdateUi);
        if (ProbeKey.JustPressed()) Diagnostics.Guard("probing immunity", ProbeImmunity);
    }

    /// Rather than guess at the rule again, ask the game: take a real tower and a real
    /// bloon, try each candidate mask on the bloon, and call the game's own immunity check
    /// with it. Whatever comes back true is the shape the rule wants.
    private static void ProbeImmunity()
    {
        var inGame = InGame.instance;
        if (inGame is null || !inGame.IsInGame())
        {
            Diagnostics.Warn("probe needs to be in a match");
            return;
        }

        var towers = inGame.GetTowers();
        var bloons = inGame.GetBloons();

        if (towers is null || towers.Count == 0 || bloons is null || bloons.Count == 0)
        {
            Diagnostics.Warn($"probe needs a tower and a bloon on screen: {towers?.Count ?? 0} towers, " +
                             $"{bloons?.Count ?? 0} bloons");
            return;
        }

        var tower = towers[0];
        var bloon = bloons[0];
        var towerSet = tower.towerModel is null ? (TowerSet) 0 : tower.towerModel.towerSet;
        var was = bloon.TowerSetImmunity;

        Diagnostics.Say($"probe: tower {tower.towerModel?.baseId} set {(int) towerSet}, " +
                        $"bloon {bloon.bloonModel?.id}, immunity was {(int) was}");

        foreach (var candidate in new[] { 0, 1, 15, 127, 128, 129, 255, 256, 257, 383, (int) towerSet, -1 })
        {
            try
            {
                bloon.ApplyTowerSetImmunity((TowerSet) candidate);
                var immune = bloon.IsImmuneByTowerSet(null, tower);
                Diagnostics.Say($"probe: immunity {candidate} -> immune {immune}");
            }
            catch (Exception e)
            {
                Diagnostics.Say($"probe: immunity {candidate} -> threw {e.GetType().Name}");
            }
        }

        bloon.ApplyTowerSetImmunity(was);
        Diagnostics.Say("probe finished, original immunity restored");
    }

    private static void Send(int index)
    {
        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame()) return;

        var me = inGame.bridge.GetInputId();
        Diagnostics.Say($"player {me} pressed {SendType.All[index].Label}");

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
        var simulation = Sim;
        var wallet = simulation?.GetCashManager(sender)?.cash;

        if (wallet is null)
        {
            Diagnostics.Warn($"player {sender} has no wallet, send of {send.Label} dropped");
            return;
        }

        if (wallet.Value < cost)
        {
            Diagnostics.Say($"player {sender} cannot afford {send.Label}: has {(long) wallet.Value}, needs {cost}");
            return;
        }

        // Writing to the wallet directly moves the money but not the number on screen,
        // which only redraws when the simulation announces the change.
        var before = wallet.Value;
        simulation.RemoveCash(cost, Il2CppAssets.Scripts.Simulation.Simulation.CashType.Normal, sender,
            Il2CppAssets.Scripts.Simulation.Simulation.CashSource.Normal);

        if (Math.Abs(before - cost - wallet.Value) > 1)
        {
            Diagnostics.Warn($"charging player {sender} {cost} moved their cash from {(long) before} " +
                             $"to {(long) wallet.Value}, which is not what was asked for");
        }
        Income[sender] = IncomeOf(sender) + send.Income;
        SpawnSpaced(sender, send, round);

        sendsSeen++;
        Diagnostics.Say($"send {sendsSeen} on round {round + 1}: player {sender} paid " +
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
            Diagnostics.Warn($"cannot emit {send.Label}: spawner " +
                                    $"{(spawner is null ? "missing" : "found")}, model " +
                                    $"{(bloonModel is null ? "missing" : "found")}");
            return;
        }

        var lane = OppositeSides ? LaneFor(sender) : null;
        Releases.Add(new Release(sender, bloonModel, lane, send.Count, simTick + 1));

        Diagnostics.Say($"queued {send.Count} {send.Label} for player {sender}, " +
                            $"one every {(int) SpacingTicks} ticks, lane " +
                            $"{(lane is null ? "default" : "redirected")}");
    }

    /// Runs once per simulation tick on every machine, so releasing from here keeps the
    /// two games in step.
    [HarmonyPatch(typeof(Spawner), nameof(Spawner.Process))]
    private static class ProcessPatch
    {
        /// Runs every simulation tick. Nothing here may throw: an exception escaping into
        /// the bridge kills the spawner's tick, the simulation stops advancing, and the
        /// match sits on the loading screen forever with nothing logged. The whole body is
        /// inside the guard for that reason, income included.
        private static void Postfix(Spawner __instance)
        {
            try
            {
                simTick++;

                // A heartbeat, so a stuck match can be told apart from a stuck simulation:
                // if these keep coming, the tick is alive and the fault is elsewhere.
                if (simTick % 600 == 0) Diagnostics.Say($"simulation tick {simTick}");

                SweepTowerTeams();
                HoldSharedLives();

                PayIncome();
                if (Releases.Count == 0) return;

                for (var i = Releases.Count - 1; i >= 0; i--)
                {
                    var release = Releases[i];
                    if (simTick < release.NextTick) continue;

                    if (release.Lane is not null) __instance.spawnOverrideThisFrame = release.Lane;

                    var bloon = __instance.Emit(release.Bloon, __instance.CurrentRound,
                        VersusEmissionBase + release.Sender * PerPlayerBlock + release.Remaining, 0, false);

                    if (bloon is not null)
                    {
                        SentBloonOwner[bloon.Pointer] = release.Sender;

                        // Immune to its own sender's towers, so only the other side can pop it.
                        if (TeamImmunity)
                        {
                            var mine = release.Sender <= 1 ? TeamOne : TeamTwo;
                            bloon.ApplyTowerSetImmunity(mine);

                            if (release.Remaining == 1)
                            {
                                Diagnostics.Say($"sent bloons of player {release.Sender} made immune to " +
                                                $"exactly {(int) mine}, which is the set their own towers " +
                                                $"carry; bloon reports {(int) bloon.TowerSetImmunity}");
                            }
                        }
                    }

                    if (bloon is null) Diagnostics.Warn($"the spawner returned nothing for player {release.Sender}");

                    release.Remaining--;
                    release.NextTick = simTick + (int) SpacingTicks;

                    if (release.Remaining <= 0)
                    {
                        Releases.RemoveAt(i);
                        Diagnostics.Say($"send for player {release.Sender} fully released, " +
                                        $"{SentBloonOwner.Count} sent bloons now tracked");
                    }
                }
            }
            catch (Exception e)
            {
                Releases.Clear();
                Diagnostics.Failed("releasing a send failed: ", e);
            }
        }
    }

    /// Battles pays out every six seconds rather than at the end of a round, and the
    /// simulation's tick is the clock both machines share.
    private static void PayIncome()
    {
        var interval = (int) IncomeSeconds * 60;
        if (interval <= 0 || simTick % interval != 0 || Income.Count == 0) return;

        // The spawner ticks before the match is fully up, so none of this can assume the
        // game is ready.
        var inGame = InGame.instance;
        if (inGame is null || inGame.bridge is null || !inGame.IsInGame()) return;

        var simulation = Sim;
        if (simulation is null) return;

        var players = new List<int>();
        for (var player = 1; player <= 4; player++)
        {
            if (simulation.InputManagerExists(player)) players.Add(player);
        }

        if (players.Count == 0) players.Add(inGame.bridge.GetInputId());

        foreach (var player in players)
        {
            var amount = IncomeOf(player) + (int) BaseIncome;
            if (amount == 0) continue;

            if (amount > 0)
            {
                simulation.AddCash(amount, Il2CppAssets.Scripts.Simulation.Simulation.CashType.Normal,
                    player, Il2CppAssets.Scripts.Simulation.Simulation.CashSource.EcoEarned, null, false);
                Diagnostics.Say($"income: player {player} paid {amount}, now on " +
                                $"{(long) (simulation.GetCashManager(player)?.cash?.Value ?? 0)}");
                continue;
            }

            var owed = simulation.GetCashManager(player)?.cash;
            var due = Math.Min(-amount, owed is null ? 0 : owed.Value);
            if (due > 0)
            {
                simulation.RemoveCash(due, Il2CppAssets.Scripts.Simulation.Simulation.CashType.Normal,
                    player, Il2CppAssets.Scripts.Simulation.Simulation.CashSource.Normal);
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
                Diagnostics.Failed("send failed, which will desync: ", e);
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

        if (!built)
        {
            Build();
            if (built) Diagnostics.Say("interface built");
        }

        if (!built) return;

        var me = inGame.bridge.GetInputId();
        var them = Opponent(me);

        if (p1Text is not null) p1Text.text = Readout(me, me);
        if (p2Text is not null) p2Text.text = Readout(them, me);

        if (loser != 0 && banner is null) ShowResult(loser == me);

        var cash = Sim?.GetCashManager(me)?.cash?.Value ?? 0;
        for (var i = 0; i < ButtonCosts.Count; i++)
        {
            var cost = CostOf(SendType.All[i]);
            ButtonCosts[i].SetText(CashDisplay.LocalizeAndFormatCash(cost));
            ButtonCosts[i].Text.color = cash >= cost ? Color.white : new Color(1f, 0.45f, 0.45f);
        }
    }

    /// Said properly rather than left to a line of small text: the match is over and each
    /// screen should say which way it went.
    private static void ShowResult(bool lost)
    {
        banner = hudObject.AddModHelperPanel(
            new Info("VersusResult", 0, 120, 900, 260, new Vector2(0.5f, 0.5f)),
            VanillaSprites.MainBGPanelBlue, RectTransform.Axis.Vertical, 6, 20);

        var headline = banner.AddText(new Info("VersusResultText", 860, 150), lost ? "DEFEAT" : "VICTORY", 110);
        headline.Text.color = lost ? new Color(1f, 0.4f, 0.4f) : new Color(0.5f, 1f, 0.5f);

        banner.AddText(new Info("VersusResultWho", 860, 60),
            lost ? "you ran out of lives" : "they ran out of lives", 40);

        Diagnostics.Say(lost ? "this player lost the match" : "this player won the match");
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
            if (Time.frameCount % 300 == 0) Diagnostics.Warn($"waiting for the health display");
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
            Diagnostics.Failed("building the panel failed: ", e);
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

            if (grid is null) Diagnostics.Warn($"could not find the powers grid, sends will be off screen");

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

            Diagnostics.Say($"powers tab filled with {ButtonCosts.Count} sends, " +
                                $"{withArt} showing bloon art");
        }
        catch (Exception e)
        {
            Diagnostics.Failed("filling the powers tab failed: ", e);
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
        var backdrop = health.transform.parent is null ? null : health.transform.parent.GetComponent<Image>();
        var backing = backdrop is null ? null : backdrop.sprite;

        p1Text = BuildCounter(column, "You", backing);
        p2Text = BuildCounter(column, "Them", backing);

        // The round's own lives pool means nothing in versus, so it is hidden. Hidden, not
        // switched off: it runs a startup coroutine, and Unity stops coroutines on an
        // inactive object, which left the match loading forever waiting on it.
        var vanilla = health.transform.parent;
        if (vanilla is null) return;

        var fade = vanilla.gameObject.GetComponent<CanvasGroup>();
        if (fade is null) fade = vanilla.gameObject.AddComponent<CanvasGroup>();

        fade.alpha = 0;
        fade.interactable = false;
        fade.blocksRaycasts = false;
        hiddenLives = fade;
        Diagnostics.Say("vanilla lives counter faded out");
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
        if (hiddenLives is not null)
        {
            hiddenLives.alpha = 1;
            hiddenLives.interactable = true;
            hiddenLives.blocksRaycasts = true;
        }
        hudObject = null;
        banner = null;
        hiddenLives = null;
        ButtonCosts.Clear();
        p1Text = null;
        p2Text = null;
        built = false;
    }
}
