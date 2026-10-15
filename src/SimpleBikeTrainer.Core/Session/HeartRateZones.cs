using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Session;

/// <summary>Heart rate zones as upper bounds in bpm, the way Intervals.icu sends them.</summary>
public static class HeartRateZones
{
    // Intervals.icu's default 7 zones (from threshold heart rate) and the common 5 zones of max heart rate.
    private static string[] SevenNames => [CoreStrings.HrZone7_1, CoreStrings.HrZone7_2, CoreStrings.HrZone7_3, CoreStrings.HrZone7_4, CoreStrings.HrZone7_5, CoreStrings.HrZone7_6, CoreStrings.HrZone7_7];
    private static string[] FiveNames => [CoreStrings.HrZone5_1, CoreStrings.HrZone5_2, CoreStrings.HrZone5_3, CoreStrings.HrZone5_4, CoreStrings.HrZone5_5];

    /// <summary>Five zones at 60/70/80/90/100% of max heart rate, or none without one.</summary>
    public static int[]? FromMax(int maxHr) =>
        maxHr is >= 100 and <= 250 ? [.. new[] { 0.6, 0.7, 0.8, 0.9, 1.0 }.Select(f => (int)Math.Round(maxHr * f))] : null;

    /// <summary>The zone a heart rate is in, 0-based. Above the top bound counts as the top zone.</summary>
    public static int IndexOf(IReadOnlyList<int> upperBounds, double bpm)
    {
        for (var i = 0; i < upperBounds.Count; i++)
        {
            if (bpm <= upperBounds[i])
            {
                return i;
            }
        }

        return upperBounds.Count - 1;
    }

    /// <summary>"Z3 Tempo": the athlete's zone name from Intervals.icu, else a standard one, else just "Z3".</summary>
    public static string Name(int index, int count, IReadOnlyList<string>? names = null)
    {
        if (names is not null && index < names.Count && !string.IsNullOrWhiteSpace(names[index]))
        {
            return Loc.Format(CoreStrings.Zone_Named, index + 1, names[index]);
        }

        var standard = count switch { 7 => SevenNames, 5 => FiveNames, _ => null };
        return standard is null ? Loc.Format(CoreStrings.Zone_Number, index + 1) : Loc.Format(CoreStrings.Zone_Named, index + 1, standard[index]);
    }
}
