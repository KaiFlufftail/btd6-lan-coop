using System;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using MelonLoader;
using Il2CppNinjaKiwi.NKMulti;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.Network;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.TowerSelectionMenu;

[assembly: MelonInfo(typeof(BTD6CoopShare.Main), "BTD6 Co-op Shared Towers", "2.0.0", "homebrew")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]

namespace BTD6CoopShare;

// Let co-op players upgrade and sell each other's towers.
//
// Why this is not a simple patch: the simulation's permission check is
// TowerManager.CanUpgradeTower, which opens by comparing Tower.owner against the acting
// input index and refusing on a mismatch. It cannot be patched out, because its
// `ref float cost` parameter makes Il2CppInterop's native->managed trampoline throw before
// any patch body runs, and a throwing patch breaks the check itself (v1.2 proved that
// twice). Nor can the actor be forged: UpgradeTowerAction carries no player at all, only
// the tower id, path, callback id, paragon flag and cash investment. The player comes from
// GameplayAction.initiatingPlayerNumber, stamped from the local client when the action is
// queued, so the input id handed to bridge.UpgradeTower is discarded on the networked path.
// That is why v1.1 (send as self) and v1.3 (send as the owner) produced identical actions
// and both did nothing.
//
// So the upgrade is performed by the client that is allowed to perform it. Clicking a
// teammate's tower sends them a short message over the co-op connection; their client
// recognises the tower as its own and calls the upgrade itself, which the simulation
// permits, and the resulting action replicates through the normal lockstep path. No
// simulation patches and no forged actions, so there is nothing new to desync.
//
// Trade-off: the tower's owner pays, since it is their client buying the upgrade.
//
// Both machines need this mod with the setting on, since one side has to answer the
// request for anything to happen.
public class Main : BloonsTD6Mod
{
    private const string Code = "CST";

    public static readonly ModSettingBool ShareTowers = new(false)
    {
        displayName = "Shared towers (BOTH players need this mod + setting)",
    };

    private static NKMultiGameInterface cached;

    public override void OnConnected(NKMultiGameInterface nkGi) => cached = nkGi;

    public override void OnPeerConnected(NKMultiGameInterface nkGi, int peerId) => cached = nkGi;

    public override void OnDisconnected(NKMultiGameInterface nkGi) => cached = null;

    // ModHelper's connect callbacks are marked obsolete, so the cache may never be filled.
    // The live interface is also reachable by walking the bridge down to the relay socket
    // owner, which is the route that does not depend on a deprecated callback firing.
    private static NKMultiGameInterface Coop()
    {
        if (cached != null) return cached;

        var networked = InGame.instance?.bridge?.TryCast<NetworkedUnityToSimulation>();
        var game = networked?.connection?.TryCast<Btd6CoopGameNetworked>();
        cached = game?.Connection?.Connection?.NKGI;
        return cached;
    }

    private static int MyInputId() => InGame.instance.bridge.GetInputId();

    // ModHelper's GetCashManager indexes the dictionary's entry array rather than keying
    // it by player, so it reads player 1's wallet for everyone and throws outright for
    // player 2. The simulation's own accessor takes the player index.
    private static double MyCash() => InGame.instance.bridge.Simulation.GetCashManager(MyInputId()).cash.Value;

    // Returns the teammate's tower the click landed on, or null when the normal path
    // should run (own tower, feature off, not in a co-op game).
    private static Il2CppAssets.Scripts.Unity.Bridge.TowerToSimulation Target(TowerSelectionMenu menu)
    {
        if (!ShareTowers || menu == null) return null;
        var tts = menu.selectedTower;
        if (tts == null || InGame.instance?.bridge == null) return null;
        return tts.owner == MyInputId() ? null : tts;
    }

    private static bool Request(string payload)
    {
        var coop = Coop();
        if (coop == null)
        {
            Melon<Main>.Logger.Warning("[share] no co-op connection, letting the click fall through");
            return false;
        }

        try
        {
            coop.SendMessageEx(payload, null, Code);
            Melon<Main>.Logger.Msg($"[share] sent '{payload}' as code {Code}");
            return true;
        }
        catch (Exception e)
        {
            Melon<Main>.Logger.Error($"[share] send failed: {e.Message}");
            return false;
        }
    }

    [HarmonyPatch(typeof(TowerSelectionMenu), nameof(TowerSelectionMenu.UpgradeTower), typeof(int), typeof(bool))]
    private static class UpgradeClickPatch
    {
        private static bool Prefix(TowerSelectionMenu __instance, int index)
        {
            try { return Share(__instance, index); }
            catch (Exception e)
            {
                // A throwing patch does not fail quietly on Il2Cpp, it breaks the method it
                // is attached to, which is what killed one direction of this in v1.8.
                Melon<Main>.Logger.Error($"[share] upgrade click failed, nothing billed: {e}");
                return false;
            }
        }

        private static bool Share(TowerSelectionMenu __instance, int index)
        {
            var tts = Target(__instance);
            if (tts == null) return true;

            var bridge = InGame.instance.bridge;
            int me = MyInputId();

            // The owner's client is the one that buys the upgrade, so the cost is wired
            // across first and the money ends up coming out of the clicking player's cash.
            // The figure comes from the button itself, which is the discounted number on
            // screen rather than the model's list price.
            float cost = __instance.upgradeButtons[index]?.upgradeButton?.GetUpgradeCost() ?? 0f;
            double mine = MyCash();
            if (cost > mine)
            {
                // Refusing outright is the point: a shortfall must never land on the owner.
                Melon<Main>.Logger.Msg($"[share] upgrade costs {cost}, player {me} holds {mine}, refusing without billing player {tts.owner}");
                return false;
            }

            if (cost > 0) bridge.SendCash(me, tts.owner, cost);

            Melon<Main>.Logger.Msg($"[share] asking player {tts.owner} to upgrade their tower {tts.id.Id} path {index}, sending {cost}");
            return !Request($"U|{tts.id.Raw}|{index}|{me}|{cost}");
        }
    }

    [HarmonyPatch(typeof(TowerSelectionMenu), nameof(TowerSelectionMenu.Sell))]
    private static class SellClickPatch
    {
        private static bool Prefix(TowerSelectionMenu __instance)
        {
            try
            {
                var tts = Target(__instance);
                if (tts == null) return true;

                Melon<Main>.Logger.Msg($"[share] asking player {tts.owner} to sell their tower {tts.id.Id}");
                return !Request($"S|{tts.id.Raw}|{MyInputId()}");
            }
            catch (Exception e)
            {
                Melon<Main>.Logger.Error($"[share] sell click failed: {e}");
                return false;
            }
        }
    }

    // Logged once per code, to show what the mod dispatch is actually offered.
    private static readonly System.Collections.Generic.HashSet<string> seenCodes = new();

    public override bool ActOnMessage(Message message)
    {
        if (seenCodes.Add(message.code))
            Melon<Main>.Logger.Msg($"[share] ActOnMessage saw code '{message.code}' ({message.bytes?.Length ?? 0}B)");

        return false;
    }

    // Requests are picked up where every incoming frame is decoded rather than from the
    // mod dispatch, because the game drains the same receive queue from its own loop and
    // a lone mod message loses that race: v1.5 proved the frame arrives (the relay logged
    // it leaving and being forwarded) yet ActOnMessage was only ever offered the codes the
    // game itself handles.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> pending = new();

    [HarmonyPatch(typeof(Il2CppNinjaKiwi.NKMulti.MessageFactory), nameof(Il2CppNinjaKiwi.NKMulti.MessageFactory.Create))]
    private static class DecodePatch
    {
        private static void Postfix(string code, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> data)
        {
            if (code != Code) return;

            // Decoding runs on the network thread, so the work is queued for the game thread.
            pending.Enqueue(System.Text.Encoding.UTF8.GetString(data));
        }
    }

    // A teammate's upgrade buttons render grey because the menu asks whether the tower is
    // someone else's and styles them as unavailable. The click already works, so this is
    // purely the look of it. Note the cost shown then measures against the clicker's cash
    // while the owner's cash is what actually pays.
    [HarmonyPatch(typeof(UpgradeButton), nameof(UpgradeButton.DoesNotOwn))]
    private static class OwnershipLookPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (ShareTowers) __result = false;
        }
    }

    // A request cannot be acted on the moment it lands. The money is a normal game action
    // and so arrives a few ticks later, while the message arrives at once, which is how an
    // upgrade could take the asker's cash and then be refused for lack of funds. So each
    // request waits until the upgrade is affordable here, and if it never applies the cash
    // goes back to whoever asked.
    private class Request_
    {
        public uint raw;
        public int path;
        public int asker;
        public float cost;
        public bool sent;
        public int tierBefore;
        public float giveUpAt;
    }

    private static readonly System.Collections.Generic.List<Request_> waiting = new();

    public override void OnUpdate()
    {
        while (pending.TryDequeue(out var raw))
        {
            if (!ShareTowers || InGame.instance?.bridge == null) continue;

            // SendMessageEx serialises through JSON, so a plain string arrives quoted.
            var parts = raw.Trim('"').Split('|');
            var bridge = InGame.instance.bridge;
            int me = MyInputId();

            var tts = bridge.GetTower(new ObjectId { data = uint.Parse(parts[1]) }, true);
            if (tts == null || tts.owner != me) continue;   // not our tower, so not our job

            if (parts[0] == "U")
            {
                waiting.Add(new Request_
                {
                    raw = uint.Parse(parts[1]),
                    path = int.Parse(parts[2]),
                    asker = int.Parse(parts[3]),
                    cost = float.Parse(parts[4]),
                    giveUpAt = UnityEngine.Time.time + 6f,
                });
            }
            else
            {
                // Read the sale value before the tower is gone, then hand it to the player
                // who asked, which keeps sales working the same way round as upgrades.
                float refund = tts.sellFor;
                int askedBy = int.Parse(parts[2]);

                Melon<Main>.Logger.Msg($"[share] selling own tower {tts.id.Id} at player {askedBy}'s request, refunding {refund}");
                bridge.SellTower(me, tts.id);
                if (refund > 0) bridge.SendCash(me, askedBy, refund);
            }
        }

        if (waiting.Count == 0) return;
        ServeWaiting();
    }

    private static void ServeWaiting()
    {
        var bridge = InGame.instance?.bridge;
        if (bridge == null) { waiting.Clear(); return; }

        int me = MyInputId();
        float now = UnityEngine.Time.time;

        for (int i = waiting.Count - 1; i >= 0; i--)
        {
            var req = waiting[i];
            var tts = bridge.GetTower(new ObjectId { data = req.raw }, true);
            if (tts == null) { Refund(bridge, me, req, "tower is gone"); waiting.RemoveAt(i); continue; }

            if (!req.sent)
            {
                if (MyCash() + 0.5 >= req.cost)
                {
                    req.tierBefore = tts.Def.tiers[req.path];
                    bridge.UpgradeTower(me, tts.id, req.path, 0d, null);
                    req.sent = true;
                    req.giveUpAt = now + 3f;
                    Melon<Main>.Logger.Msg($"[share] upgrading own tower {tts.id.Id} path {req.path} for player {req.asker}, cost {req.cost}");
                }
                else if (now > req.giveUpAt)
                {
                    Refund(bridge, me, req, $"never had {req.cost} to spend");
                    waiting.RemoveAt(i);
                }
                continue;
            }

            if (tts.Def.tiers[req.path] > req.tierBefore)
            {
                waiting.RemoveAt(i);   // applied
            }
            else if (now > req.giveUpAt)
            {
                Refund(bridge, me, req, "the simulation refused it");
                waiting.RemoveAt(i);
            }
        }
    }

    private static void Refund(UnityToSimulation bridge, int me, Request_ req, string why)
    {
        Melon<Main>.Logger.Msg($"[share] returning {req.cost} to player {req.asker}: {why}");
        if (req.cost > 0) bridge.SendCash(me, req.asker, req.cost);
    }
}
