
namespace SimpleBikeTrainer.Workouts;

public readonly record struct CoursePoint(double DistanceMeters, double ElevationMeters);

/// <summary>
/// A virtual route: elevation by distance, joined by straight lines. Riding it sets the grade from where the rider is,
/// so climbs slow them down and descents speed them up.
/// </summary>
public sealed class Course
{
    public Course(IReadOnlyList<CoursePoint> points)
    {
        if (points.Count < 2 || points[0].DistanceMeters != 0)
        {
            throw new ArgumentException("A course needs at least two points, starting at 0 m.", nameof(points));
        }

        Points = points;
        for (var i = 1; i < points.Count; i++)
        {
            var rise = points[i].ElevationMeters - points[i - 1].ElevationMeters;
            ElevationGain += Math.Max(0, rise);
            MaxGrade = Math.Max(MaxGrade, rise / (points[i].DistanceMeters - points[i - 1].DistanceMeters) * 100);
        }
    }

    public IReadOnlyList<CoursePoint> Points { get; }

    public double DistanceMeters => Points[^1].DistanceMeters;

    /// <summary>Total climbing in meters: every rise added up.</summary>
    public double ElevationGain { get; }

    /// <summary>The steepest climb, in percent.</summary>
    public double MaxGrade { get; }

    public double MinElevation => Points.Min(p => p.ElevationMeters);

    public double MaxElevation => Points.Max(p => p.ElevationMeters);

    public double ElevationAt(double distance)
    {
        var i = SegmentAt(distance);
        var a = Points[i];
        var b = Points[i + 1];
        var t = Math.Clamp((distance - a.DistanceMeters) / (b.DistanceMeters - a.DistanceMeters), 0, 1);
        return a.ElevationMeters + ((b.ElevationMeters - a.ElevationMeters) * t);
    }

    /// <summary>The grade in percent at a distance; 0 past the end.</summary>
    public double GradeAt(double distance)
    {
        if (distance >= DistanceMeters)
        {
            return 0;
        }

        var i = SegmentAt(distance);
        var a = Points[i];
        var b = Points[i + 1];
        return (b.ElevationMeters - a.ElevationMeters) / (b.DistanceMeters - a.DistanceMeters) * 100;
    }

    /// <summary>Climbing between two distances, in meters.</summary>
    public double GainBetween(double from, double to)
    {
        double gain = 0;
        var previous = ElevationAt(from);
        foreach (var p in Points.Where(p => p.DistanceMeters > from && p.DistanceMeters < to))
        {
            gain += Math.Max(0, p.ElevationMeters - previous);
            previous = p.ElevationMeters;
        }

        return gain + Math.Max(0, ElevationAt(to) - previous);
    }

    private int SegmentAt(double distance)
    {
        // Binary search for the last point at or before the distance.
        int lo = 0, hi = Points.Count - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Points[mid].DistanceMeters <= distance)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}

/// <summary>Makes a random loop of a given length and total climbing.</summary>
public static class CourseGenerator
{
    /// <summary>Climbs are kept at or under this grade; asking for more climbing than fits gives less.</summary>
    public const double MaxGrade = 12;

    private const double Spacing = 50;
    private const double BaseElevation = 100;

    /// <summary>
    /// A loop that ends where it starts, made of rolling hills: a few sine waves whose periods divide the distance, so
    /// the end meets the start. It is scaled so the climbing adds up to <paramref name="gainMeters"/>.
    /// </summary>
    public static Course Generate(double distanceMeters, double gainMeters, int seed)
    {
        distanceMeters = Math.Clamp(distanceMeters, 1000, 300_000);
        gainMeters = Math.Max(0, gainMeters);
        var n = (int)Math.Ceiling(distanceMeters / Spacing);
        var xs = Enumerable.Range(0, n + 1).Select(i => i * distanceMeters / n).ToArray();
        if (gainMeters < 1)
        {
            return new Course([.. xs.Select(x => new CoursePoint(x, BaseElevation))]);
        }

        var random = new Random(seed);

        // At most one hill every 1.5 km, and fewer when the climbing would otherwise be too steep: fewer, longer hills
        // spread the same climbing over more road.
        var maxHarmonic = Math.Clamp((int)(distanceMeters / 1500), 1, 60);
        double[] elevations = [];
        double scale = 1;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            elevations = Profile(xs, distanceMeters, maxHarmonic, random);
            var gain = Gain(elevations);
            scale = gain <= 0 ? 0 : gainMeters / gain;
            if (Steepest(xs, elevations) * scale <= MaxGrade || maxHarmonic == 1)
            {
                break;
            }

            maxHarmonic = Math.Max(1, maxHarmonic / 2);
        }

        // Still too steep: flatten to the grade limit, giving less climbing than asked.
        scale = Math.Min(scale, MaxGrade / Math.Max(Steepest(xs, elevations), 1e-9));
        var min = elevations.Min() * scale;
        return new Course([.. xs.Select((x, i) => new CoursePoint(x, BaseElevation + (elevations[i] * scale) - min))]);
    }

    private static double[] Profile(double[] xs, double distance, int maxHarmonic, Random random)
    {
        // A few long waves carry most of the climbing; shorter ones add rollers.
        var count = Math.Min(maxHarmonic, 5);
        var harmonics = Enumerable.Range(1, maxHarmonic).OrderBy(_ => random.Next()).Take(count).ToList();
        var waves = harmonics.Select(k => (K: k, Amplitude: (0.5 + random.NextDouble()) / Math.Pow(k, 0.6), Phase: random.NextDouble() * 2 * Math.PI)).ToList();
        return [.. xs.Select(x => waves.Sum(w => w.Amplitude * Math.Sin((2 * Math.PI * w.K * x / distance) + w.Phase)))];
    }

    private static double Gain(double[] elevations)
    {
        double gain = 0;
        for (var i = 1; i < elevations.Length; i++)
        {
            gain += Math.Max(0, elevations[i] - elevations[i - 1]);
        }

        return gain;
    }

    /// <summary>The steepest climb in percent, before scaling.</summary>
    private static double Steepest(double[] xs, double[] elevations)
    {
        double max = 0;
        for (var i = 1; i < elevations.Length; i++)
        {
            max = Math.Max(max, Math.Abs(elevations[i] - elevations[i - 1]) / (xs[i] - xs[i - 1]) * 100);
        }

        return max;
    }
}
