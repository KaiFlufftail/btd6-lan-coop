namespace BTD6Versus;

/// What a player can send, priced from Bloons TD Battles, which is the game Kai means by
/// BTD5 versus: it runs on BTD5's towers and art. Battles pays income every six seconds,
/// so the income here is per round instead, at ten times the Battles eco figure.
///
/// Blimps cost what they cost in Battles and carry a negative income, as they do there:
/// a blimp rush buys pressure now and pays for it later. DDTs and BADs postdate Battles,
/// so theirs are set by eye against the rest.
public sealed record SendType(string Bloon, int Count, int Cost, int Income, string Label)
{
    public static readonly SendType[] All =
    {
        new("Red", 8, 25, 10, "Red"),
        new("Blue", 6, 42, 17, "Blue"),
        new("Green", 5, 60, 24, "Green"),
        new("Yellow", 5, 75, 30, "Yellow"),
        new("Pink", 4, 90, 36, "Pink"),
        new("White", 4, 90, 30, "White"),
        new("Zebra", 3, 125, 50, "Zebra"),
        new("Black", 3, 150, 60, "Black"),
        new("Rainbow", 1, 150, 60, "Rainbow"),
        new("Ceramic", 1, 200, 0, "Ceramic"),
        new("Moab", 1, 1500, -30, "MOAB"),
        new("Bfb", 1, 2500, -80, "BFB"),
        new("Zomg", 1, 9000, -150, "ZOMG"),
        new("Ddt", 1, 4000, -60, "DDT"),
        new("Bad", 1, 15000, -250, "BAD")
    };
}
