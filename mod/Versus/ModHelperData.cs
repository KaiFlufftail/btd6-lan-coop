namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.4.0";
    public const string Name = "BTD6 Versus (probe)";

    public const string Description =
        "Versus for BTD6, in the shape Bloons TD Battles had it. A send panel sits along the " +
        "bottom with each bloon's own icon, its price and the income it earns; click one to " +
        "push that batch at the other player. A scoreboard across the top shows both sides' " +
        "lives and income.<br><br>" +
        "Prices come from Bloons TD Battles, the versus game built on BTD5's towers: reds 25, " +
        "blues 42, greens 60, yellows 75, pinks 90, whites 90, zebras 125, blacks and rainbows " +
        "150. Battles pays income every six seconds, so here it pays per round at ten times the " +
        "eco figure. The price multiplier in these settings scales the lot against BTD6 cash." +
        "<br><br>" +
        "A sent bloon that leaks costs the player it was aimed at, and a side on zero loses. " +
        "Still missing: separate tracks, so both players defend the same path. A bloon that " +
        "splits only counts for its own layer. Every machine must run this build.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
