namespace BTD6Versus;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "0.1.0";
    public const string Name = "BTD6 Versus (probe)";

    public const string Description =
        "A determinism probe, not a game mode yet. Shift+B sends a batch of bloons into the " +
        "shared track, riding the networked emote action so both simulations spawn the same " +
        "bloons on the same tick.<br><br>" +
        "The point is to find out whether a versus mode can send bloons without desyncing. " +
        "Every machine in the match must run this same build.";

    public const string RepoOwner = "KaiFlufftail";
    public const string RepoName = "btd6-lan-coop";
}
