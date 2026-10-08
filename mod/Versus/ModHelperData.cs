namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.6.2";
    public const string Name = "BTD6 Versus";

    public const string Description =
        "Versus for BTD6, in the shape Bloons TD Battles had it.<br><br>" +
        "The send buttons take over the powers menu on the right, so they sit where powers " +
        "sat and inherit its layout. Each button is a clone of the game's own spawn-bloon " +
        "button, so it carries that bloon's real artwork, with its price and the " +
        "income it earns. The lives counter is replaced by two copies of the real one, " +
        "stacked, yours above and theirs below.<br><br>" +
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
