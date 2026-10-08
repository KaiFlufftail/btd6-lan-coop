namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.2.0";
    public const string Name = "BTD6 Versus (probe)";

    public const string Description =
        "Groundwork for a versus mode. Shift+B sends a batch of bloons, riding the networked " +
        "emote action so both simulations spawn the same bloons on the same tick, which is " +
        "proven to hold without desyncing.<br><br>" +
        "Each side now has its own life pool, shown top left. A sent bloon that leaks costs " +
        "the player it was aimed at, never the sender, and both machines count it the same " +
        "way from the same events without sending anything extra.<br><br>" +
        "Still missing: separate tracks, an economy, and anything that ends the match. A " +
        "bloon that splits only counts for its own layer. Every machine must run this build.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
