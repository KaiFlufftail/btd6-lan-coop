using System.Collections.Generic;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Simulation.Track;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.Network;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.Stats;
using Il2CppAssets.Scripts.Unity.UI_New.Pause;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(CoopHud.CoopHud), CoopHud.ModHelperData.Name, CoopHud.ModHelperData.Version,
    CoopHud.ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonOptionalDependencies("BTD_Mod_Helper")]

namespace CoopHud;

public class CoopHud : BloonsTD6Mod
{
    private const float RefreshSeconds = 0.25f;
    private const int MaxPlayers = 4;

    private static readonly ModSettingBool Enabled = new(true)
    {
        displayName = "Show the teammate HUD"
    };

    private static readonly ModSettingBool ShowOwnRow = new(false)
    {
        displayName = "Include a row for the local player",
        description = "Off by default because the game already shows that cash."
    };

    private static readonly ModSettingBool ShowRoundChange = new(false)
    {
        displayName = "Show each teammate's change since the round started",
        description = "Banking or spending at a glance. Adds a second number per row."
    };

    private static readonly ModSettingBool ShowBloonsLeft = new(true)
    {
        displayName = "Show how many bloons are left in the round",
        description = "Bloons on the track plus the ones still to be sent."
    };

    private static readonly ModSettingBool ShowPing = new(false)
    {
        displayName = "Show each teammate's latency",
        description = "Appends their round trip time to the relay on the end of their row."
    };

    private static readonly ModSettingInt Opacity = new(55)
    {
        displayName = "Opacity",
        min = 10,
        max = 100,
        slider = true,
        sliderSuffix = "%"
    };

    private static readonly ModSettingInt FontSize = new(32)
    {
        displayName = "Text size",
        min = 18,
        max = 60,
        slider = true,
        onValueChanged = _ => Teardown()
    };

    private static readonly ModSettingInt OffsetX = new(50)
    {
        displayName = "Distance from the left edge",
        min = 0,
        max = 1800,
        slider = false,
        onValueChanged = _ => Teardown()
    };

    private static readonly ModSettingInt OffsetY = new(300)
    {
        displayName = "Distance from the top edge",
        min = 0,
        max = 1000,
        slider = false,
        onValueChanged = _ => Teardown()
    };

    private static readonly ModSettingHotkey ToggleKey = new(KeyCode.H, HotkeyModifier.Shift)
    {
        displayName = "Hide or show the HUD"
    };

    private static GameObject canvasObject;
    private static ModHelperPanel panel;
    private static readonly List<ModHelperText> Rows = new();
    private static readonly Dictionary<int, double> CashAtRoundStart = new();

    // Pending bloons are counted as the round's emission total minus what the spawner has
    // actually emitted, keyed by round because BTD6 overlaps them: a new round starts while
    // the one before it is still sending.
    private static readonly Dictionary<int, int> RoundEmissionTotal = new();
    private static readonly Dictionary<int, int> RoundEmitted = new();
    private static float nextRefresh;
    private static bool hiddenByHotkey;
    private static bool hiddenByPause;

    public override void OnApplicationStart()
    {
        ModHelper.Msg<CoopHud>("Co-op Teammate HUD loaded, Shift+H to hide it.");
    }

    public override void OnMatchEnd() => Teardown();

    public override void OnRestart() => Teardown();

    public override void OnMainMenu() => Teardown();

    public override void OnPauseScreenOpened(PauseScreen pauseScreen) => hiddenByPause = true;

    public override void OnPauseScreenClosed(PauseScreen pauseScreen) => hiddenByPause = false;

    public override void OnRoundStart()
    {
        CashAtRoundStart.Clear();
        var coop = Connection();
        if (coop == null) return;

        for (var number = 1; number <= coop.NumPlayers; number++)
        {
            CashAtRoundStart[number] = CashOf(number);
        }

        var round = InGame.instance.bridge.GetCurrentRound() + 1;
        if (RoundEmissionTotal.ContainsKey(round)) return;

        var total = EmissionsFor(round);
        RoundEmissionTotal[round] = total;
        RoundEmitted[round] = 0;
        ModHelper.Msg<CoopHud>($"[hud] round {round}: {total} bloons to send");
    }

    public override void OnBloonEmitted(Spawner spawner, BloonModel bloonModel, int round, int index,
        float startingDist, ref Bloon bloon)
    {
        RoundEmitted[round] = RoundEmitted.TryGetValue(round, out var emitted) ? emitted + 1 : 1;
    }

    public override void OnUpdate()
    {
        if (ToggleKey.JustPressed()) hiddenByHotkey = !hiddenByHotkey;

        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + RefreshSeconds;

        var inGame = InGame.instance;
        if (inGame == null || inGame.bridge == null || !inGame.IsInGame())
        {
            Teardown();
            return;
        }

        var coop = Connection();
        if (coop == null || coop.NumPlayers < 2 || !Enabled)
        {
            Teardown();
            return;
        }

        if (panel == null)
        {
            Build();
            LogNameSources(coop);
        }

        var visible = !hiddenByHotkey && !hiddenByPause;
        panel.SetActive(visible);
        if (!visible) return;

        var alpha = Opacity / 100f;
        var own = coop.OwnPlayerNumber;
        var row = 0;

        for (var number = 1; number <= coop.NumPlayers && row < Rows.Count; number++)
        {
            if (number == own && !ShowOwnRow) continue;

            Write(Rows[row], Describe(coop, number), alpha);
            row++;
        }

        if (ShowBloonsLeft && row < Rows.Count)
        {
            Write(Rows[row], $"{BloonsLeft()} bloons left", alpha * 0.8f);
            row++;
        }

        for (var spare = row; spare < Rows.Count; spare++)
        {
            Rows[spare].SetActive(false);
        }
    }

    private static string Describe(Btd6CoopGame coop, int number)
    {
        var player = InfoFor(coop, number);
        var name = NameOf(player, number);
        if (player != null && player.IsDisconnected) return $"{name}  offline";

        var cash = CashOf(number);
        var line = $"{name}  {CashDisplay.LocalizeAndFormatCash(cash)}";

        if (ShowRoundChange && CashAtRoundStart.TryGetValue(number, out var start))
        {
            var change = cash - start;
            var sign = change < 0 ? "-" : "+";
            line += $"   {sign}{CashDisplay.LocalizeAndFormatCash(Mathf.Abs((float) change))}";
        }

        if (ShowPing && player?.LobbyPlayer != null)
        {
            line += $"   {player.LobbyPlayer.Latency.TotalMilliseconds:0} ms";
        }

        return line;
    }

    /// One log line per player per match, so a wrong name on screen can be traced to the
    /// field it came from without another test game.
    private static void LogNameSources(Btd6CoopGame coop)
    {
        for (var number = 1; number <= coop.NumPlayers; number++)
        {
            var player = InfoFor(coop, number);
            if (player?.Info == null)
            {
                ModHelper.Msg<CoopHud>($"[hud] player {number}: no lobby info");
                continue;
            }

            var info = player.Info;
            ModHelper.Msg<CoopHud>($"[hud] player {number}: DisplayName '{info.DisplayName}', " +
                                   $"displayName '{info.displayName}', safeName '{info.safeName}', " +
                                   $"lobby {(player.LobbyPlayer == null ? "no" : "yes")}");
        }
    }

    /// Only the networked connection carries the lobby player list; couch co-op and any
    /// gap in it fall back to a plain player number.
    private static CoopPlayerInfo InfoFor(Btd6CoopGame coop, int number)
    {
        var networked = coop.TryCast<Btd6CoopGameNetworked>();
        if (networked?.PlayersInfo == null) return null;
        return networked.PlayersInfo.TryGetValue((byte) number, out var player) ? player : null;
    }

    private static string NameOf(CoopPlayerInfo player, int number)
    {
        var info = player?.Info;
        // DisplayName is the game's own accessor and is what the lobby shows; the raw
        // safeName field is the auto-generated filtered one, which is not the real name.
        var name = info == null ? null : info.DisplayName;
        if (string.IsNullOrEmpty(name)) name = info?.displayName;
        if (string.IsNullOrEmpty(name)) name = info?.safeName;
        if (string.IsNullOrEmpty(name)) return $"P{number}";
        return name.Length > 14 ? name.Substring(0, 14) : name;
    }

    /// On the track plus still to be sent. Children count as they appear, so a ceramic wave
    /// pushes the number up before it comes down, which is what the track actually holds.
    private static int BloonsLeft()
    {
        var inGame = InGame.instance;
        if (inGame == null) return 0;

        var onTrack = inGame.GetBloons()?.Count ?? 0;

        var pending = 0;
        foreach (var round in RoundEmissionTotal)
        {
            var emitted = RoundEmitted.TryGetValue(round.Key, out var done) ? done : 0;
            var left = round.Value - emitted;
            if (left > 0) pending += left;
        }

        return onTrack + pending;
    }

    private static int EmissionsFor(int round)
    {
        var spawner = InGame.instance?.bridge?.Simulation?.Map?.spawner;
        if (spawner == null || spawner.roundData == null) return 0;

        return spawner.roundData.TryGetValue(round, out var data) && data.emissions != null
            ? data.emissions.Count
            : 0;
    }

    private static double CashOf(int playerNumber)
    {
        var simulation = InGame.instance?.bridge?.Simulation;
        if (simulation == null) return 0;

        // Keyed by player, unlike ModHelper's GetCashManager, which indexes the raw entry array.
        return simulation.TryGetCashManager(playerNumber, out var manager) ? manager.cash.Value : 0;
    }

    private static Btd6CoopGame Connection()
    {
        var bridge = InGame.instance?.bridge;
        if (bridge == null) return null;

        var networked = bridge.TryCast<NetworkedUnityToSimulation>();
        if (networked != null) return networked.connection;

        var couch = bridge.TryCast<CouchUnityToSimulation>();
        return couch?.connection;
    }

    private static void Write(ModHelperText text, string value, float alpha)
    {
        text.SetActive(true);
        text.SetText(value);
        text.Text.fontSize = FontSize;
        text.Text.color = new Color(1f, 1f, 1f, alpha);
    }

    /// Its own overlay canvas rather than a child of the game's HUD, so no vanilla layout
    /// group can move it and nothing of the game's can be covered but this strip.
    private static void Build()
    {
        canvasObject = new GameObject("CoopTeammateHud");
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Below the pause screen and popups so it never sits on top of a menu.
        canvas.sortingOrder = 50;

        var scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var rowHeight = FontSize + 8;
        var info = new Info("CoopHudRows", OffsetX, -OffsetY, 620, rowHeight * (MaxPlayers + 1),
            new Vector2(0, 1))
        {
            Pivot = new Vector2(0, 1)
        };

        panel = canvasObject.AddModHelperPanel(info, null, RectTransform.Axis.Vertical, 2);

        Rows.Clear();
        for (var i = 0; i < MaxPlayers + 1; i++)
        {
            var text = panel.AddText(new Info($"Row{i}", 620, rowHeight), "", FontSize,
                Il2CppTMPro.TextAlignmentOptions.Left);
            text.SetActive(false);
            Rows.Add(text);
        }
    }

    private static void Teardown()
    {
        if (canvasObject != null) Object.Destroy(canvasObject);
        canvasObject = null;
        panel = null;
        Rows.Clear();
        CashAtRoundStart.Clear();
        RoundEmissionTotal.Clear();
        RoundEmitted.Clear();
        hiddenByPause = false;
    }
}
