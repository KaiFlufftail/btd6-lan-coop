namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.3.0";
    public const string Name = "BTD6 Versus (probe)";

    public const string Description =
        "Groundwork for a versus mode. Shift+B sends a batch of bloons, riding the networked " +
        "emote action so both simulations spawn the same bloons on the same tick, which is " +
        "proven to hold without desyncing.<br><br>" +
        "Each side now has its own life pool, shown top left. A sent bloon that leaks costs " +
        "the player it was aimed at, never the sender, and both machines count it the same " +
        "way from the same events without sending anything extra.<br><br>" +
        "Sends now cost cash and earn income, Battles style: each send bills the sender and " +
        "raises their income, which pays out at the start of every round. A side that runs " +
        "out of lives loses, and the overlay says so.<br><br>" +
        "Still missing: separate tracks, so both players defend the same path. A bloon that " +
        "splits only counts for its own layer. Every machine must run this build.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
