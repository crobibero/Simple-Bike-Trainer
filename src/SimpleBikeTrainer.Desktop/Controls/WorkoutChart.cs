using Avalonia.Controls;
using Avalonia.Media;
using Avalonia;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.Controls;

/// <summary>One recorded second on the chart. X is seconds into the workout, or meters along a route; grade is in percent.</summary>
public readonly record struct ChartPoint(double X, int Power, int? HeartRate, double Grade = 0);

/// <summary>
/// A workout's steps as power-zone blocks under the ridden traces, or a route's elevation profile colored by grade.
/// <see cref="Views.ChartLegend"/> explains them.
/// </summary>
public sealed class WorkoutChart : Control
{
    public static readonly StyledProperty<Workout?> WorkoutProperty =
        AvaloniaProperty.Register<WorkoutChart, Workout?>(nameof(Workout));

    public static readonly StyledProperty<int> FtpProperty =
        AvaloniaProperty.Register<WorkoutChart, int>(nameof(Ftp), 200);

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<WorkoutChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<WorkoutChart, double>(nameof(Position), -1);

    /// <summary>Bump to redraw after <see cref="Points"/> grew.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<WorkoutChart, int>(nameof(Version));

    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<WorkoutChart, bool>(nameof(Compact));


    static WorkoutChart()
    {
        AffectsRender<WorkoutChart>(WorkoutProperty, FtpProperty, PointsProperty, PositionProperty, VersionProperty, CompactProperty);
    }

    public WorkoutChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public Workout? Workout
    {
        get => GetValue(WorkoutProperty);
        set => SetValue(WorkoutProperty, value);
    }

    public int Ftp
    {
        get => GetValue(FtpProperty);
        set => SetValue(FtpProperty, value);
    }

    public IReadOnlyList<ChartPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>Route colors: blue downhill, gray flat, then green, yellow, orange and red as the road steepens.</summary>
    public static Color GradeColor(double grade) => grade switch
    {
        < -1 => Color.Parse("#3B8EEA"),
        < 2 => Color.Parse("#8C95A3"),
        < 4 => Color.Parse("#4CC38A"),
        < 6 => Color.Parse("#F2C14E"),
        < 9 => Color.Parse("#FF8C42"),
        _ => Color.Parse("#F0565A"),
    };

    /// <summary>Coggan zone colors, from Z1 recovery to Z7 neuromuscular.</summary>
    public static Color ZoneColor(double fractionOfFtp) => fractionOfFtp switch
    {
        < 0.56 => Color.Parse("#8C95A3"),
        < 0.76 => Color.Parse("#3B8EEA"),
        < 0.91 => Color.Parse("#4CC38A"),
        < 1.06 => Color.Parse("#F2C14E"),
        < 1.21 => Color.Parse("#FF8C42"),
        < 1.51 => Color.Parse("#F0565A"),
        _ => Color.Parse("#B26BF0"),
    };

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        // The theme's colors, so the chart reads on a light or a dark background.
        var trace = Palette.Color("TraceBrush", "#F5F7FA", this);
        var grade = Palette.Color("GradeBrush", "#5ED6D0", this);
        var ftpPen = new Pen(Palette.Find("FtpLineBrush", "#99FFFFFF", this), 1, new DashStyle([4, 4], 0));
        var powerPen = new Pen(new SolidColorBrush(trace), 1.6);
        var heartPen = new Pen(new SolidColorBrush(Palette.Color("HeartBrush", "#F0565A", this), 0.8), 1.4);
        var gradePen = new Pen(new SolidColorBrush(grade), 1.6);
        var gradeZeroPen = new Pen(new SolidColorBrush(grade, 0.2), 1);
        var positionPen = new Pen(Palette.Find("PrimaryColor", "#E5E5E5", this), 2);
        var freeRideBrush = new SolidColorBrush(trace, 0.2);
        context.FillRectangle(Palette.Find("InsetBrush", "#0F0F0F", this), new Rect(0, 0, w, h), Compact ? 6 : 8);
        var workout = Workout;
        var points = Points ?? [];
        var ftp = Math.Max(1, Ftp);
        var steps = workout?.Steps.Where(s => s.DurationSeconds is > 0 and < int.MaxValue).ToList() ?? [];
        var finite = steps.Count > 0 && steps.Count == workout!.Steps.Count(s => s.DurationSeconds > 0);
        var lastX = points.Count > 0 ? points[^1].X : 0;

        // The x axis spans the workout, or the ride so far for a free ride. A ramp test runs to failure, so only the
        // part around the rider is shown. A route spans its distance.
        double maxX;
        if (workout?.Course is { } course)
        {
            maxX = Math.Max(course.DistanceMeters, lastX);
        }
        else if (workout?.IsRampTest == true)
        {
            maxX = Math.Max(RampTest.WarmupSeconds + (15 * RampTest.StepSeconds), Math.Max(lastX, Position) + 180);
        }
        else if (finite)
        {
            maxX = Math.Max(workout!.TotalSeconds, lastX);
        }
        else
        {
            maxX = Math.Max(600, lastX + 60);
        }

        // Visible steps with their start times.
        var visible = new List<(double Start, WorkoutStep Step)>();
        double t = 0;
        foreach (var step in steps)
        {
            if (t < maxX)
            {
                visible.Add((t, step));
            }

            t += step.DurationSeconds;
        }

        double maxWatts = ftp * 1.25;
        foreach (var (_, step) in visible)
        {
            if (step.Power is { } p)
            {
                maxWatts = Math.Max(maxWatts, Math.Max(p.WattsAt(0, ftp), p.WattsAt(1, ftp)));
            }
        }

        foreach (var p in points)
        {
            maxWatts = Math.Max(maxWatts, p.Power);
        }

        maxWatts *= 1.08;
        var pad = Compact ? 2.0 : 6.0;
        var plotH = h - (2 * pad);
        double X(double seconds) => seconds / maxX * w;
        double Y(double watts) => h - pad - (Math.Clamp(watts / maxWatts, 0, 1) * plotH);

        if (workout?.Course is { } route)
        {
            DrawRoute(context, route, maxX, w, h, pad, plotH);
        }

        foreach (var (start, step) in visible)
        {
            var x0 = X(start);
            var x1 = Math.Min(w, X(start + step.DurationSeconds));
            if (step.Power is not { } power)
            {
                context.FillRectangle(freeRideBrush, new Rect(x0, Y(ftp * 0.5), Math.Max(1, x1 - x0 - 1), h - pad - Y(ftp * 0.5)));
                continue;
            }

            var lo = power.WattsAt(0, ftp);
            var hi = power.WattsAt(1, ftp);
            var color = ZoneColor((lo + hi) / 2.0 / ftp);
            var brush = new SolidColorBrush(color, Compact ? 0.9 : 0.55);
            var gap = x1 - x0 > 3 ? 1 : 0;
            if (power.Ramp && lo != hi)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(x0, h - pad), true);
                    g.LineTo(new Point(x0, Y(lo)));
                    g.LineTo(new Point(x1 - gap, Y(hi)));
                    g.LineTo(new Point(x1 - gap, h - pad));
                    g.EndFigure(true);
                }

                context.DrawGeometry(brush, null, geometry);
            }
            else
            {
                context.FillRectangle(brush, new Rect(x0, Y(lo), Math.Max(1, x1 - x0 - gap), h - pad - Y(lo)));
            }
        }

        if (!Compact)
        {
            context.DrawLine(ftpPen, new Point(0, Y(ftp)), new Point(w, Y(ftp)));
            DrawTrace(context, points, maxX, w, p => Y(p.Power), powerPen, _ => true);

            // Heart rate on its own scale, 60-200 bpm.
            double HrY(double bpm) => h - pad - (Math.Clamp((bpm - 60) / 140, 0, 1) * plotH);
            DrawTrace(context, points, maxX, w, p => HrY(p.HeartRate ?? 0), heartPen, p => p.HeartRate is > 0);

            // Grade on its own scale, -10% to +20% (the range the ride allows), once the rider has changed it. A route
            // shows its grade as the profile instead.
            if (workout?.Course is null && points.Any(p => p.Grade != 0))
            {
                double GradeY(double grade) => h - pad - (Math.Clamp((grade + 10) / 30, 0, 1) * plotH);
                context.DrawLine(gradeZeroPen, new Point(0, GradeY(0)), new Point(w, GradeY(0)));
                DrawTrace(context, points, maxX, w, p => GradeY(p.Grade), gradePen, _ => true);
            }
        }

        if (Position >= 0)
        {
            var x = X(Position);
            context.DrawLine(positionPen, new Point(x, 0), new Point(x, h));
        }
    }

    /// <summary>
    /// The route's elevation as a filled profile in the lower 70% of the chart, one column per pixel, each colored by
    /// the grade around it.
    /// </summary>
    private static void DrawRoute(DrawingContext context, Workouts.Course route, double maxX, double w, double h, double pad, double plotH)
    {
        var low = route.MinElevation;
        var range = Math.Max(30, route.MaxElevation - low);
        var bottom = h - pad;
        var columns = Math.Max(1, (int)(route.DistanceMeters / maxX * w));
        var brushes = new Dictionary<Color, IBrush>();
        for (var c = 0; c < columns; c++)
        {
            var from = c * maxX / w;
            var to = Math.Min(route.DistanceMeters, (c + 1) * maxX / w);
            var elevation = route.ElevationAt((from + to) / 2);
            // Colored by the grade over 300 m, like a road sign, so short wiggles do not stripe the profile.
            var middle = (from + to) / 2;
            var (back, ahead) = (Math.Max(0, middle - 150), Math.Min(route.DistanceMeters, middle + 150));
            var grade = (route.ElevationAt(ahead) - route.ElevationAt(back)) / Math.Max(1, ahead - back) * 100;
            var color = GradeColor(grade);
            if (!brushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color, 0.6);
                brushes[color] = brush;
            }

            var top = bottom - ((0.1 + (0.6 * (elevation - low) / range)) * plotH);
            context.FillRectangle(brush, new Rect(c, top, 1.01, bottom - top));
        }
    }

    /// <summary>Draws a trace, averaging the points that share a pixel column so long rides stay cheap.</summary>
    private static void DrawTrace(DrawingContext context, IReadOnlyList<ChartPoint> points, double maxX, double width,
        Func<ChartPoint, double> y, IPen pen, Func<ChartPoint, bool> include)
    {
        var geometry = new StreamGeometry();
        var any = false;
        using (var g = geometry.Open())
        {
            var column = -1;
            double sum = 0;
            var count = 0;
            var started = false;

            void Flush()
            {
                if (count == 0)
                {
                    return;
                }

                var point = new Point(column, sum / count);
                if (!started)
                {
                    g.BeginFigure(point, false);
                    started = true;
                }
                else
                {
                    g.LineTo(point);
                }

                any = true;
            }

            foreach (var p in points)
            {
                if (!include(p))
                {
                    continue;
                }

                var c = (int)(p.X / maxX * width);
                if (c != column)
                {
                    Flush();
                    column = c;
                    sum = 0;
                    count = 0;
                }

                sum += y(p);
                count++;
            }

            Flush();
            if (started)
            {
                g.EndFigure(false);
            }
        }

        if (any)
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
