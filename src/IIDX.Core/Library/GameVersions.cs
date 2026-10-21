namespace IIDX.Core.Library;

/// <summary>Names and release years of the beatmania IIDX styles, keyed by the music db's game version.</summary>
public static class GameVersions
{
    private static readonly Dictionary<int, (string Name, int Year)> Versions = new()
    {
        [0] = ("substream", 1999),
        [1] = ("1st style", 1999),
        [2] = ("2nd style", 1999),
        [3] = ("3rd style", 2000),
        [4] = ("4th style", 2000),
        [5] = ("5th style", 2001),
        [6] = ("6th style", 2001),
        [7] = ("7th style", 2002),
        [8] = ("8th style", 2002),
        [9] = ("9th style", 2003),
        [10] = ("10th style", 2004),
        [11] = ("IIDX RED", 2004),
        [12] = ("HAPPY SKY", 2005),
        [13] = ("DistorteD", 2006),
        [14] = ("GOLD", 2007),
        [15] = ("DJ TROOPERS", 2007),
        [16] = ("EMPRESS", 2008),
        [17] = ("SIRIUS", 2009),
        [18] = ("Resort Anthem", 2010),
        [19] = ("Lincle", 2011),
        [20] = ("tricoro", 2012),
        [21] = ("SPADA", 2013),
        [22] = ("PENDUAL", 2014),
        [23] = ("copula", 2015),
        [24] = ("SINOBUZ", 2016),
        [25] = ("CANNON BALLERS", 2017),
        [26] = ("Rootage", 2018),
        [27] = ("HEROIC VERSE", 2019),
        [28] = ("BISTROVER", 2020),
        [29] = ("CastHour", 2021),
        [30] = ("RESIDENT", 2022),
        [31] = ("EPOLIS", 2023),
        [32] = ("Pinky Crush", 2024),
        [33] = ("Sparkle Shower", 2025),
    };

    /// <summary>Gets the display name, e.g. "beatmania IIDX 17 SIRIUS".</summary>
    public static string DisplayName(int version)
    {
        if (!Versions.TryGetValue(version, out var v))
        {
            return $"beatmania IIDX {version}";
        }

        return version switch
        {
            0 => "beatmania IIDX substream",
            1 => "beatmania IIDX",
            <= 10 => $"beatmania IIDX {v.Name}",
            _ => $"beatmania IIDX {version} {v.Name}",
        };
    }

    public static int? Year(int version) => Versions.TryGetValue(version, out var v) ? v.Year : null;
}
