using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Ui;
using SimpleBikeTrainer.Uploads;

namespace SimpleBikeTrainer.Commands;

/// <summary><c>workouts</c>: lists planned rides from Intervals.icu.</summary>
internal sealed class WorkoutsCommand(IHttpClientFactory http) : TrainerCommand<WorkoutsCommand.Settings>
{
    internal sealed class Settings : TrainerSettings
    {
        [CommandOption("--days <N>")]
        public int Days { get; set; } = 7;

        public override ValidationResult Validate() =>
            Days > 0 ? ValidationResult.Success() : ValidationResult.Error(Loc.Format(CliStrings.Cli_MustBePositive, "--days"));
    }

    protected override async Task<int> ExecuteAsync(AppConfig config, Settings settings, CancellationToken ct)
    {
        using var intervals = IntervalsAccount.Client(config, http);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rides = await intervals.GetPlannedRidesAsync(today, today.AddDays(settings.Days - 1), ct);
        if (rides.Count == 0)
        {
            AnsiConsole.MarkupLine(Markup.Escape(CliStrings.Cli_NoPlanned));
            return 0;
        }

        foreach (var ride in rides)
        {
            var w = ride.Workout;
            AnsiConsole.MarkupLine($"[bold]{ride.Date.ToString(CliStrings.Cli_ShortDate, CultureInfo.CurrentCulture)}[/]  {Markup.Escape(w.Name)}  [grey]{Markup.Escape(Loc.Format(CliStrings.Cli_PlannedDetail, ride.Id, ride.Type, w.TotalSeconds / 60, w.Ftp?.ToString(CultureInfo.CurrentCulture) ?? "-"))}[/]");
            foreach (var step in w.Steps)
            {
                AnsiConsole.MarkupLine($"    {Markup.Escape(step.Label),-22} {Dashboard.Clock(TimeSpan.FromSeconds(step.DurationSeconds)),7}  {Markup.Escape(step.Power?.ToString() ?? CliStrings.Cli_FreeRide)} {Markup.Escape(step.Cadence?.ToString() ?? string.Empty)}");
            }
        }

        return 0;
    }
}

/// <summary><c>upload &lt;file.fit&gt;</c>: sends a ride to Intervals.icu.</summary>
internal sealed class UploadCommand(IHttpClientFactory http) : TrainerCommand<UploadCommand.Settings>
{
    internal sealed class Settings : TrainerSettings
    {
        [CommandArgument(0, "<FILE>")]
        public string Path { get; set; } = string.Empty;

        [CommandOption("--event <ID>")]
        public long? Event { get; set; }

        [CommandOption("--name <NAME>")]
        public string? Name { get; set; }

        public override ValidationResult Validate() =>
            File.Exists(Path) ? ValidationResult.Success() : ValidationResult.Error(Loc.Format(CliStrings.Cli_FileMissing, Path));
    }

    protected override async Task<int> ExecuteAsync(AppConfig config, Settings settings, CancellationToken ct)
    {
        using var intervals = IntervalsAccount.Client(config, http);
        var result = await intervals.UploadActivityAsync(settings.Path, settings.Name ?? Path.GetFileNameWithoutExtension(settings.Path), settings.Event, description: null, ct);
        AnsiConsole.MarkupLine(result.ActivityId is { } id ? $"[green]{Markup.Escape(CliStrings.Cli_UploadedAt)}[/] https://intervals.icu/activities/{Markup.Escape(id)}" : $"[green]{Markup.Escape(CliStrings.Cli_Uploaded)}[/]");
        return 0;
    }
}

/// <summary><c>connect garmin</c>: signs in to Garmin Connect and saves the session tokens, never the password.</summary>
internal sealed class ConnectGarminCommand(IHttpClientFactory http) : TrainerCommand<TrainerSettings>
{
    protected override async Task<int> ExecuteAsync(AppConfig config, TrainerSettings settings, CancellationToken ct)
    {
        using var garmin = new GarminClient(config, http);
        var email = await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_GarminEmail)), ct);
        var password = await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_GarminPassword)).Secret(), ct);
        if (await garmin.SignInAsync(email, password, ct))
        {
            var code = await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_GarminCode)), ct);
            await garmin.SubmitMfaAsync(code, ct);
        }

        var name = await garmin.GetNameAsync(ct);
        config.Uploads.Garmin = true;
        config.Save();
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(Loc.Format(CliStrings.Cli_GarminSignedIn, name))}[/]");
        return 0;
    }
}

internal static class IntervalsAccount
{
    /// <summary>A client for the saved API key; without one, says how to set it.</summary>
    public static IntervalsClient Client(AppConfig config, IHttpClientFactory http) =>
        config.HasApiKey
            ? new IntervalsClient(http, config.Intervals.ApiKey, config.Intervals.AthleteId)
            : throw new InvalidOperationException(CliStrings.Cli_NoKey);
}
