namespace BTD6LanCoop;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "1.0.1";
    public const string Name = "BTD6 LAN Co-op";

    public const string Description =
        "Plays co-op through a relay on the local network instead of Ninja Kiwi's relay, " +
        "so the match traffic never leaves the house.<br><br>" +
        "Run btd6relay on one machine, set the relay address to that machine on every " +
        "machine, turn LAN mode on, then create and join a co-op lobby as normal.<br><br>" +
        "The lobby and its invite code still come from Ninja Kiwi, so this needs an internet " +
        "connection to start a game. Every machine in the match must run the same game " +
        "version and the same mods.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
