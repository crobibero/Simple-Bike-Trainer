using Spectre.Console;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;

namespace SimpleBikeTrainer.Ui;

/// <summary>The end-of-ride report.</summary>
public static class Summary
{
    public static void Write(IAnsiConsole console, RideResult result, UnitSystem units = UnitSystem.Metric)
    {
        var m = result.Metrics;
        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle($"[bold]{Markup.Escape(result.Workout.Name)}[/]") };
        table.AddColumn(CliStrings.Sum_Metric);
        table.AddColumn(new TableColumn(CliStrings.Sum_Value).RightAligned());
        table.AddRow(CliStrings.Sum_ElapsedMoving, $"{Dashboard.Clock(result.EndUtc - result.StartUtc)} / {Dashboard.Clock(TimeSpan.FromSeconds(m.Seconds))}");
        table.AddRow(CliStrings.Sum_Distance, Loc.Format(CliStrings.Sum_DistanceValue, units.FormatDistance(m.DistanceMeters), units.FormatSpeed(m.AverageSpeedMs)));
        if (m.ElevationGain >= 1)
        {
            table.AddRow(CliStrings.Sum_Climbing, units.FormatElevation(m.ElevationGain));
        }

        table.AddRow(CliStrings.Sum_AvgPower, Loc.Format(CliStrings.Fmt_Watts, m.AveragePower));
        table.AddRow(CliStrings.Sum_MaxPower, Loc.Format(CliStrings.Fmt_Watts, m.MaxPower));
        table.AddRow(CliStrings.Sum_Np, Loc.Format(CliStrings.Fmt_Watts, m.NormalizedPower));
        table.AddRow(CliStrings.Sum_If, Loc.Format(CliStrings.Sum_IfValue, m.IntensityFactor, m.Ftp));
        table.AddRow(CliStrings.Sum_Tss, Loc.Format(CliStrings.Sum_TssValue, m.TrainingStressScore));
        table.AddRow(CliStrings.Sum_Work, Loc.Format(CliStrings.Sum_WorkValue, m.KiloJoules, m.Kilocalories));
        table.AddRow(CliStrings.Sum_WPrimeLow, m.Seconds > 0 ? Loc.Format(CliStrings.Sum_WPrimeValue, m.MinWBalance / 1000, m.WPrime / 1000.0) : "-");
        table.AddRow(CliStrings.Sum_Hr, m.AverageHeartRate is { } hr ? Loc.Format(CliStrings.Sum_HrValue, hr, m.MaxHeartRate) : "-");
        table.AddRow(CliStrings.Sum_Cadence, m.AverageCadence is { } c ? Loc.Format(CliStrings.Sum_CadenceValue, c, m.MaxCadence) : "-");
        foreach (var window in RideMetrics.PeakWindows.Where(m.Peaks.ContainsKey))
        {
            table.AddRow(Loc.Format(CliStrings.Sum_Best, window < 60 ? Loc.Format(CliStrings.Sum_Seconds, window) : Loc.Format(CliStrings.Sum_Minutes, window / 60)), Loc.Format(CliStrings.Fmt_Watts, m.Peaks[window]));
        }

        table.AddRow(CliStrings.Sum_Laps, result.LapCount.ToString(System.Globalization.CultureInfo.CurrentCulture));
        if (result.FtpEstimate is { } ftp)
        {
            table.AddRow($"[bold]{Markup.Escape(CliStrings.Sum_FtpEstimate)}[/]", $"[bold]{Loc.Format(CliStrings.Fmt_Watts, ftp)}[/]");
        }

        console.Write(table);
        if (result.FitPath is not null)
        {
            console.MarkupLine($"{Markup.Escape(CliStrings.Sum_Saved)} [bold]{Markup.Escape(result.FitPath)}[/]");
        }
    }
}
