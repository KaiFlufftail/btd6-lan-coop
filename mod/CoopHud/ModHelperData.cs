namespace CoopHud;

public static class ModHelperData
{
    public const string WorksOnVersion = "57.0";
    public const string Version = "1.2.1";
    public const string Name = "Co-op Teammate HUD";

    public const string Description =
        "One quiet line per teammate in co-op showing their cash, so there is no guessing " +
        "whether they can afford the next upgrade.<br><br>" +
        "Nothing is drawn in single player, nothing is drawn while paused, and the local " +
        "player is left out because that cash is already on screen. Reads the simulation only, " +
        "sends no messages and patches nothing, so it is safe to run on one machine while the " +
        "other stays stock.<br><br>" +
        "Shift+H hides and shows it. Position, size, opacity and the optional round change and " +
        "ping lines are in this mod's settings.";

    public const string RepoOwner = "local";
    public const string RepoName = "CoopHud";
}
