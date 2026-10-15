using System.Text;
using Spectre.Console.Rendering;
using Spectre.Console;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Ui;

/// <summary>Renders the live ride screen from a <see cref="RideSnapshot"/>.</summary>
public static class Dashboard
{
    private const string Blocks = "▁▂▃▄▅▆▇█";
    private static readonly string[] ZoneNames = ["Z1", "Z2", "Z3", "Z4", "Z5", "Z6", "Z7", "Z8", "Z9", "Z10"];

    public static IRenderable Render(RideSnapshot s, Workout workout, IReadOnlyList<ISensorDevice> devices, string? notice, UnitSystem units = UnitSystem.Metric)
    {
        var rows = new List<IRenderable>
        {
            Header(s, workout),
            LiveNumbers(s, units),
            StepPanel(s, workout),
            new Columns(RidePanel(s, units), LapPanel(s, units), ZonePanel(s)) { Expand = true },
        };

        var message = notice ?? s.Message;
        if (message is not null)
        {
            rows.Add(new Markup($"[yellow]{Markup.Escape(message)}[/]"));
        }

        rows.Add(DeviceLine(devices));
        rows.Add(new Markup(
            $"[grey]{Markup.Escape(CliStrings.Dash_Keys + (workout.IsRampTest ? CliStrings.Dash_KeysRamp : string.Empty) + CliStrings.Dash_KeysFinish)}[/]"));
        return new Rows(rows);
    }

    private static IRenderable Header(RideSnapshot s, Workout workout)
    {
        var state = s.State switch
        {
            RideState.WaitingToStart => $"[yellow]{CliStrings.Dash_PedalToStart}[/]",
            RideState.Paused => $"[yellow]{CliStrings.Dash_Paused}[/]",
            RideState.AutoPaused => $"[yellow]{CliStrings.Dash_AutoPaused}[/]",
            _ => s.WorkoutComplete ? $"[green]{CliStrings.Dash_WorkoutDone}[/]" : $"[green]{CliStrings.Dash_Riding}[/]",
        };
        return new Markup(
            $"[bold]{Markup.Escape(workout.Name)}[/]  {state}  " +
            $"{CliStrings.Dash_Elapsed} [bold]{Clock(s.Elapsed)}[/]  {CliStrings.Dash_Moving} [bold]{Clock(s.Moving)}[/]");
    }

    private static IRenderable LiveNumbers(RideSnapshot s, UnitSystem units)
    {
        var powerColor = s.TargetWatts is > 0 and var t && s.ErgActive
            ? Deviation(s.Power3s, t, Math.Max(10, t * 0.05), Math.Max(25, t * 0.12))
            : "white";
        // In simulation the trainer sets the resistance from the road, so there is no target power.
        var targetText = s.Simulating ? CliStrings.Dash_Sim
            : s.TargetWatts is { } target
            ? $"{target} W [grey]{(s.ErgActive ? CliStrings.Dash_Erg + (s.ErgOffset != 0 ? $" {s.ErgOffset:+0;-0}" : string.Empty) : CliStrings.Dash_Gears)}[/]"
            : "-";

        var cadenceText = s.Cadence?.ToString() ?? "-";
        var cadenceColor = "white";
        if (s.CadenceTarget is { } ct && s.Cadence is { } c)
        {
            cadenceColor = c >= ct.Low - 3 && c <= ct.High + 3 ? "green" : Math.Abs(c - ct.Mid) <= 8 ? "yellow" : "red";
            cadenceText += $" [grey]/ {ct.Mid}[/]";
        }

        var table = new Table { Border = TableBorder.Rounded, Expand = true };
        foreach (var column in new[] { CliStrings.Dash_ColPower, CliStrings.Dash_ColTarget, CliStrings.Dash_ColCadence, CliStrings.Dash_ColHr, CliStrings.Dash_ColSpeed, s.ErgActive || !s.VirtualGears ? CliStrings.Dash_ColMode : CliStrings.Dash_ColGear })
        {
            table.AddColumn(new TableColumn($"[grey]{column}[/]").Centered());
        }

        var gear = s.ErgActive ? CliStrings.Dash_Erg
            : !s.VirtualGears ? $"{CliStrings.Dash_Sim} [grey]{s.Grade:+0.0;-0.0;0.0}%[/]"
            : $"{s.Gear}/{s.GearCount} [grey]{s.Grade:+0.0;-0.0;0.0}%[/]";
        table.AddRow(
            new Markup($"[bold {powerColor}]{s.Power3s} W[/] [grey]({s.Power})[/]"),
            new Markup($"[bold]{targetText}[/]"),
            new Markup($"[bold {cadenceColor}]{cadenceText}[/] [grey]{CliStrings.Dash_UnitRpm}[/]"),
            new Markup(s.HeartRate is { } hr ? $"[bold red]{hr}[/] [grey]{CliStrings.Dash_UnitBpm}[/]" : "[grey]-[/]"),
            new Markup($"[bold]{units.Speed(s.SpeedKmh / 3.6):0.0}[/] [grey]{units.SpeedUnit()}[/]"),
            new Markup($"[bold]{gear}[/]"));
        return table;
    }

    private static IRenderable StepPanel(RideSnapshot s, Workout workout)
    {
        var lines = new List<IRenderable>();
        if (s.Step is { } step && step.DurationSeconds != int.MaxValue)
        {
            var progress = step.DurationSeconds == 0 ? 0 : (double)s.StepElapsed / step.DurationSeconds;
            lines.Add(new Markup(
                $"[bold]{Markup.Escape(step.Label)}[/]  {Target(step)}  " +
                $"[bold]{Clock(TimeSpan.FromSeconds(s.StepRemaining))}[/] {CliStrings.Dash_Left}  {Bar(progress, 30)}"));
            lines.Add(new Markup(s.Next is { } next
                ? $"[grey]{Loc.Format(CliStrings.Dash_Next, Markup.Escape(next.Label), Clock(TimeSpan.FromSeconds(next.DurationSeconds)), Target(next))}[/]"
                : $"[grey]{CliStrings.Dash_NextDone}[/]"));
        }
        else
        {
            lines.Add(new Markup($"[bold]{CliStrings.Dash_FreeRide}[/] [grey]{(s.VirtualGears ? CliStrings.Dash_OnGears : CliStrings.Dash_OnBikeGears)}[/]"));
        }

        if (workout.Steps.Count > 1)
        {
            lines.Add(new Markup(Profile(workout, s.WorkoutElapsed, s.WorkoutComplete, 72)));
            var total = workout.TotalSeconds;
            lines.Add(new Markup($"[grey]{Loc.Format(CliStrings.Dash_WorkoutProgress, Clock(TimeSpan.FromSeconds(Math.Min(s.WorkoutElapsed, total))), Clock(TimeSpan.FromSeconds(total)), Math.Min(s.StepIndex + 1, workout.Steps.Count), workout.Steps.Count)}[/]"));
        }

        return new Panel(new Rows(lines)) { Header = new PanelHeader(CliStrings.Dash_Step), Expand = true, Border = BoxBorder.Rounded };
    }

    private static IRenderable RidePanel(RideSnapshot s, UnitSystem units)
    {
        var m = s.Metrics;
        var grid = new Grid().AddColumn().AddColumn(new GridColumn().RightAligned());
        grid.AddRow(CliStrings.Dash_Distance, units.FormatDistance(s.DistanceKm * 1000));
        grid.AddRow(CliStrings.Dash_AvgMaxPower, Loc.Format(CliStrings.Dash_PairPower, m.AveragePower, m.MaxPower));
        grid.AddRow(CliStrings.Dash_NpIf, Loc.Format(CliStrings.Dash_NpIfValue, m.NormalizedPower, m.IntensityFactor));
        grid.AddRow(CliStrings.Dash_Tss, m.TrainingStressScore.ToString("0", System.Globalization.CultureInfo.CurrentCulture));
        grid.AddRow(CliStrings.Dash_Work, Loc.Format(CliStrings.Dash_WorkValue, m.KiloJoules, m.Kilocalories));
        grid.AddRow(CliStrings.Dash_WBalance, Loc.Format(CliStrings.Dash_WBalanceValue, m.WBalance / 1000, m.WPrime / 1000.0));
        grid.AddRow(CliStrings.Dash_AvgMaxHr, m.AverageHeartRate is { } hr ? Loc.Format(CliStrings.Dash_PairHr, hr, m.MaxHeartRate) : "-");
        grid.AddRow(CliStrings.Dash_AvgMaxCadence, m.AverageCadence is { } cad ? Loc.Format(CliStrings.Dash_PairCadence, cad, m.MaxCadence) : "-");
        grid.AddRow(CliStrings.Dash_AvgSpeed, units.FormatSpeed(m.AverageSpeedMs));
        var peaks = string.Join("  ", RideMetrics.PeakWindows.Where(m.Peaks.ContainsKey).Select(w => $"{Window(w)} {m.Peaks[w]:0}"));
        grid.AddRow(CliStrings.Dash_Peaks, peaks.Length > 0 ? peaks : "-");
        return new Panel(grid) { Header = new PanelHeader(Loc.Format(CliStrings.Dash_RideHeader, m.Ftp)), Expand = true };
    }

    private static IRenderable LapPanel(RideSnapshot s, UnitSystem units)
    {
        var lap = s.Lap;
        var grid = new Grid().AddColumn().AddColumn(new GridColumn().RightAligned());
        grid.AddRow(CliStrings.Dash_Time, Clock(TimeSpan.FromSeconds(lap.Seconds)));
        grid.AddRow(CliStrings.Dash_AvgMaxPower, Loc.Format(CliStrings.Dash_PairPower, lap.AveragePower, lap.MaxPower));
        grid.AddRow(CliStrings.Dash_AvgHr, lap.AverageHeartRate is { } hr ? Loc.Format(CliStrings.Dash_Bpm, hr) : "-");
        grid.AddRow(CliStrings.Dash_AvgCadence, lap.AverageCadence is { } c ? Loc.Format(CliStrings.Dash_Rpm, c) : "-");
        grid.AddRow(CliStrings.Dash_Distance, units.FormatDistance(lap.DistanceMeters));
        grid.AddRow(CliStrings.Dash_OnTarget, lap.Compliance is { } on ? on.ToString("P0", System.Globalization.CultureInfo.CurrentCulture) : "-");
        return new Panel(grid) { Header = new PanelHeader(Loc.Format(CliStrings.Dash_LapHeader, lap.Index + 1)), Expand = true };
    }

    private static IRenderable ZonePanel(RideSnapshot s)
    {
        var m = s.Metrics;
        var grid = new Grid().AddColumn().AddColumn().AddColumn(new GridColumn().RightAligned());
        var total = Math.Max(1, m.Seconds);
        for (var i = 0; i < m.TimeInPowerZones.Length; i++)
        {
            var share = (double)m.TimeInPowerZones[i] / total;
            grid.AddRow(ZoneNames[Math.Min(i, ZoneNames.Length - 1)], $"[blue]{Bar(share, 12, plain: true)}[/]", Clock(TimeSpan.FromSeconds(m.TimeInPowerZones[i])));
        }

        if (m.TimeInHrZones.Length > 0)
        {
            var hrZones = string.Join(" ", m.TimeInHrZones.Select((t, i) => $"{i + 1}:{t / 60}m"));
            grid.AddRow(CliStrings.Dash_Hr, string.Empty, $"[grey]{hrZones}[/]");
        }

        return new Panel(grid) { Header = new PanelHeader(CliStrings.Dash_PowerZones), Expand = true };
    }

    private static IRenderable DeviceLine(IReadOnlyList<ISensorDevice> devices)
    {
        var parts = devices.Select(d =>
        {
            var extra = d.Battery is { } b ? $" {b}%" : string.Empty;
            var error = d is ITrainer { LastError: { } e } ? $" [red]{Markup.Escape(e)}[/]" : string.Empty;
            return d.IsConnected
                ? $"[green]●[/] {Markup.Escape(d.Name)}{extra}{error}"
                : $"[red]●[/] {Markup.Escape(d.Name)} [grey]{Markup.Escape(d.Status ?? CliStrings.Dash_Disconnected)}[/]";
        });
        return new Markup(string.Join("   ", parts));
    }

    /// <summary>The workout's power profile as block characters, with the rider's position marked.</summary>
    internal static string Profile(Workout workout, long elapsed, bool complete, int width)
    {
        var total = workout.TotalSeconds;
        if (total <= 0)
        {
            return string.Empty;
        }

        var ftp = workout.Ftp ?? 200;
        var watts = new int?[width];
        for (var col = 0; col < width; col++)
        {
            var t = (col + 0.5) * total / width;
            long start = 0;
            foreach (var step in workout.Steps)
            {
                if (t < start + step.DurationSeconds)
                {
                    watts[col] = step.Power?.WattsAt((t - start) / step.DurationSeconds, ftp);
                    break;
                }

                start += step.DurationSeconds;
            }
        }

        var max = Math.Max(1, watts.Max() ?? 1);
        var position = complete ? width : (int)(elapsed * width / total);
        var sb = new StringBuilder();
        for (var col = 0; col < width; col++)
        {
            var ch = watts[col] is { } w ? Blocks[Math.Clamp((int)Math.Round((double)w / max * (Blocks.Length - 1)), 0, Blocks.Length - 1)] : '·';
            var color = col < position ? "grey" : col == position ? "bold yellow" : ZoneColor(watts[col], ftp);
            sb.Append($"[{color}]{ch}[/]");
        }

        return sb.ToString();
    }

    private static string ZoneColor(int? watts, int ftp) => watts switch
    {
        null => "grey",
        var w when w < ftp * 0.56 => "grey70",
        var w when w < ftp * 0.76 => "blue",
        var w when w < ftp * 0.91 => "green",
        var w when w < ftp * 1.06 => "yellow",
        var w when w < ftp * 1.21 => "orange1",
        _ => "red",
    };

    private static string Target(WorkoutStep step)
    {
        var power = step.Power?.ToString() ?? CliStrings.Cli_FreeRide;
        return step.Cadence is { } c ? $"[cyan]{power} {c}[/]" : $"[cyan]{power}[/]";
    }

    private static string Deviation(double value, double target, double ok, double warn)
    {
        var d = Math.Abs(value - target);
        return d <= ok ? "green" : d <= warn ? "yellow" : "red";
    }

    private static string Bar(double fraction, int width, bool plain = false)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        var bar = new string('█', filled) + new string('░', width - filled);
        return plain ? bar : $"[green]{bar}[/]";
    }

    private static string Window(int seconds) => seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m";

    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
