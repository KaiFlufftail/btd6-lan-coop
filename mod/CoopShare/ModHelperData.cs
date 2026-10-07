namespace BTD6CoopShare;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "2.0.1";
    public const string Name = "BTD6 Co-op Shared Towers";

    public const string Description =
        "Lets co-op players upgrade and sell each other's towers.<br><br>" +
        "Clicking a teammate's upgrade sends them a request over the co-op connection and " +
        "their client performs it, so the simulation always sees the rightful owner acting " +
        "on their own tower. The clicking player pays: the cost is sent across first and the " +
        "request waits until it lands, since a cash transfer is a lockstep action that " +
        "applies a few ticks later than the message.<br><br>" +
        "Changes what both simulations do, so every machine in the match must run this same " +
        "version.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
