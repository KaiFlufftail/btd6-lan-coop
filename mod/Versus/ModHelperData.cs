namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.7.0";
    public const string Name = "BTD6 Versus";

    public const string Description =
        "Versus for BTD6, in the shape Bloons TD Battles had it.<br><br>" +
        "A send bar sits along the bottom, each button a clone of the game's own spawn-bloon " +
        "button so it carries that bloon's real artwork, with its price and income under it. " +
        "Two copies of the real lives widget sit stacked below the top bar, yours above and " +
        "theirs below. Both positions are settings, so nudge them to taste.<br><br>" +
        "Prices come from Bloons TD Battles: reds 25, blues 42, greens 60, yellows 75, pinks " +
        "and whites 90, zebras 125, blacks and rainbows 150. Battles pays income every six " +
        "seconds, so here it pays per round at ten times the eco figure, and the price " +
        "multiplier scales the lot against BTD6 cash.<br><br>" +
        "A sent bloon that leaks costs the player it was aimed at, and a side on zero loses. " +
        "Still missing: separate tracks. A bloon that splits only counts for its own layer. " +
        "Every machine must run this build.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
