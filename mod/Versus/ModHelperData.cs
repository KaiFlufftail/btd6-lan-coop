namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.8.0";
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
