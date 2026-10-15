using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.Controls;

public static class ChartPoints
{
    /// <summary>
    /// A saved ride's seconds as chart points: along the road for a route, otherwise one per riding second. Grade comes
    /// from the altitude, which the ride recorded from the route or the grade the rider set.
    /// </summary>
    public static IReadOnlyList<ChartPoint> FromSamples(IReadOnlyList<Sample> samples, Workout workout)
    {
        var points = new List<ChartPoint>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            double grade = 0;
            if (i > 0 && s.AltitudeMeters is { } altitude && samples[i - 1].AltitudeMeters is { } previous
                && s.DistanceMeters - samples[i - 1].DistanceMeters is > 0.5 and var run)
            {
                grade = Math.Round((altitude - previous) / run * 200, MidpointRounding.AwayFromZero) / 2;
            }

            points.Add(new ChartPoint(workout.Course is null ? i + 1 : s.DistanceMeters, s.Power, s.HeartRate, grade));
        }

        return points;
    }
}
