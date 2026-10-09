namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.21.0";
    public const string Name = "BTD6 Versus";

    public const string Description =
        "Versus for BTD6, in the shape Bloons TD Battles had it.<br><br>" +
        "The powers tab becomes the bloon send tab: the game builds and lays out the buttons " +
        "itself, each carrying a bloon, its price and the income it earns. Turn versus mode " +
        "off in these settings to have the powers back. " +
        "Two lives rows sit stacked below the top bar, yours above and theirs below, and their " +
        "position is a setting.<br><br>" +
        "Playable on your own: with nobody else there a send just happens, lands on an " +
        "imaginary second player and drops their lives, so the whole thing can be tried in " +
        "single player.<br><br>" +
        "Fifteen sends, from reds up to BADs. Prices come from Bloons TD Battles: reds 25, blues " +
        "42, greens 60, yellows 75, pinks and whites 90, zebras 125, blacks and rainbows 150, " +
        "MOABs 1500, BFBs 2500, ZOMGs 9000. Blimps carry a negative income, as they do in " +
        "Battles. Battles pays income every six " +
        "seconds, so here it pays per round at ten times the eco figure, and the price " +
        "multiplier scales the lot against BTD6 cash.<br><br>" +
        "Farms, heroes and insta monkeys are switched off, so income comes from sending and " +
        "nothing else; one setting puts them back.<br><br>" +
        "A batch arrives strung out rather than stacked, and on a map with more than one lane it " +
        "enters from the other player's end. Both are settings.<br><br>" +
        
        "The match stops sending waves of its own, so the only bloons on the track are the ones " +
        "you have paid for, and everyone earns a base income on the same six second clock." +
        "<br><br>" +
        "Optionally each player's towers carry a team mark and a sent bloon is immune to its " +
        "sender's mark, so you cannot pop what you paid to send.<br><br>" +
        "A sent bloon that leaks costs the player it was aimed at, and a side on zero loses. " +
        "The match's own shared lives are held full so a leak cannot end the game for both " +
        "of you; the two counters at the top left are the ones that matter. " +
        "Still missing: separate tracks. A bloon that splits only counts for its own layer. " +
        "Every machine must run this build.<br><br>" +
        "Writes a full account of itself to VersusLog.txt in the game folder: which patches " +
        "attached, what the map offers, every send and what it cost, every payout, every " +
        "leak, and any failure with its context.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
