using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Uploads;

namespace SimpleBikeTrainer.Commands;

/// <summary><c>config</c>: changes the settings given, then shows them all.</summary>
internal sealed class ConfigCommand : TrainerCommand<ConfigCommand.Settings>
{
    /// <summary>The <c>--resistance</c> names.</summary>
    private static readonly Dictionary<ResistanceMode, string> ResistanceNames = new()
    {
        [ResistanceMode.Power] = "power",
        [ResistanceMode.SimulationVirtualGears] = "virtual-gears",
        [ResistanceMode.SimulationRealGears] = "real-gears",
    };

    internal sealed class Settings : TrainerSettings
    {
        [CommandOption("--api-key <KEY>")]
        public string? ApiKey { get; set; }

        [CommandOption("--athlete-id <ID>")]
        public string? AthleteId { get; set; }

        [CommandOption("--ftp <WATTS>")]
        public int? Ftp { get; set; }

        [CommandOption("--units <UNITS>")]
        public UnitSystem? Units { get; set; }

        /// <summary>In the configured units.</summary>
        [CommandOption("--weight <WEIGHT>")]
        public double? Weight { get; set; }

        [CommandOption("--bike-weight <WEIGHT>")]
        public double? BikeWeight { get; set; }

        [CommandOption("--upload-to <TARGETS>")]
        public string? UploadTo { get; set; }

        [CommandOption("--auto-upload <BOOL>")]
        public bool? AutoUpload { get; set; }

        [CommandOption("--max-hr <BPM>")]
        public int? MaxHeartRate { get; set; }

        [CommandOption("--resistance <MODE>")]
        public string? Resistance { get; set; }

        /// <summary>The real gear as chainring x cog, e.g. 34x17.</summary>
        [CommandOption("--chain <TEETH>")]
        public string? Chain { get; set; }

        [CommandOption("--crr <CRR>")]
        public double? RollingResistance { get; set; }

        /// <summary>Comma-separated; an empty value clears them.</summary>
        [CommandOption("--wifi-trainers <HOSTS>")]
        public string? WifiTrainers { get; set; }

        [CommandOption("--forget-devices")]
        public bool ForgetDevices { get; set; }

        public IReadOnlyList<UploadTarget>? UploadTargets { get; private set; }

        public ResistanceMode? ResistanceMode { get; private set; }

        public (int Chainring, int Cog)? ChainTeeth { get; private set; }

        public override ValidationResult Validate()
        {
            if (UploadTo is not null)
            {
                var (targets, error) = UploadTargetList.Parse(UploadTo);
                if (!error.Successful)
                {
                    return error;
                }

                UploadTargets = targets;
            }

            if (Resistance is not null)
            {
                var named = ResistanceNames.FirstOrDefault(n => string.Equals(n.Value, Resistance, StringComparison.OrdinalIgnoreCase));
                if (named.Value is null)
                {
                    return ValidationResult.Error(Loc.Format(CliStrings.Cli_BadResistance, Resistance, string.Join(", ", ResistanceNames.Values)));
                }

                ResistanceMode = named.Key;
            }

            if (Chain is not null)
            {
                var teeth = Chain.Split('x', StringSplitOptions.TrimEntries);
                if (teeth.Length != 2
                    || !int.TryParse(teeth[0], CultureInfo.InvariantCulture, out var chainring) || chainring <= 0
                    || !int.TryParse(teeth[1], CultureInfo.InvariantCulture, out var cog) || cog <= 0)
                {
                    return ValidationResult.Error(Loc.Format(CliStrings.Cli_BadChain, Chain));
                }

                ChainTeeth = (chainring, cog);
            }

            return ValidationResult.Success();
        }
    }

    protected override Task<int> ExecuteAsync(AppConfig config, Settings settings, CancellationToken ct)
    {
        var changed = false;
        if (settings.ApiKey is { } key)
        {
            config.SetFileApiKey(key);
            changed = true;
        }

        if (settings.AthleteId is { } athlete)
        {
            config.SetFileAthleteId(athlete);
            changed = true;
        }

        if (settings.Ftp is { } ftp)
        {
            config.Rider.Ftp = ftp;
            changed = true;
        }

        if (settings.Units is { } units)
        {
            config.Units = units;
            changed = true;
        }

        // Weights are typed in the configured units, so after --units.
        if (settings.Weight is { } weight)
        {
            config.Rider.WeightKg = config.Units.WeightToKg(weight);
            changed = true;
        }

        if (settings.BikeWeight is { } bikeWeight)
        {
            config.Rider.BikeWeightKg = config.Units.WeightToKg(bikeWeight);
            changed = true;
        }

        if (settings.UploadTargets is { } targets)
        {
            config.Uploads.Intervals = targets.Contains(UploadTarget.Intervals);
            config.Uploads.Garmin = targets.Contains(UploadTarget.Garmin);
            changed = true;
        }

        if (settings.AutoUpload is { } auto)
        {
            config.Uploads.Automatic = auto;
            changed = true;
        }

        if (settings.MaxHeartRate is { } maxHr)
        {
            config.Rider.MaxHeartRate = maxHr;
            changed = true;
        }

        if (settings.ResistanceMode is { } resistance)
        {
            config.Trainer.Resistance = resistance;
            changed = true;
        }

        if (settings.ChainTeeth is { } chain)
        {
            (config.Trainer.ChainringTeeth, config.Trainer.CogTeeth) = chain;
            changed = true;
        }

        if (settings.RollingResistance is { } crr)
        {
            config.Rider.RollingResistance = crr;
            changed = true;
        }

        if (settings.WifiTrainers is { } wifiTrainers)
        {
            config.Devices.WifiTrainers = [.. wifiTrainers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            changed = true;
        }

        if (settings.ForgetDevices)
        {
            config.Devices = new DevicesConfig { Adapter = config.Devices.Adapter, WifiTrainers = config.Devices.WifiTrainers };
            changed = true;
        }

        if (changed || !File.Exists(config.Path))
        {
            config.Save();
            AnsiConsole.MarkupLine(Markup.Escape(Loc.Format(CliStrings.Cli_Saved, config.Path)));
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(CliStrings.Cli_RowConfig, Markup.Escape(config.Path));
        grid.AddRow(CliStrings.Cli_RowKey, config.HasApiKey ? CliStrings.Cli_Set : $"[yellow]{CliStrings.Cli_NotSet}[/]");
        grid.AddRow(CliStrings.Cli_RowAthlete, Markup.Escape(config.Intervals.AthleteId));
        grid.AddRow(CliStrings.Cli_RowFtp, Loc.Format(CliStrings.Cli_FtpValue, config.Rider.Ftp, config.Rider.WPrime));
        grid.AddRow(CliStrings.Cli_RowUnits, config.Units == UnitSystem.Imperial ? CliStrings.Cli_Imperial : CliStrings.Cli_Metric);
        grid.AddRow(CliStrings.Cli_RowWeights, $"{config.Units.FormatWeight(config.Rider.WeightKg)} + {config.Units.FormatWeight(config.Rider.BikeWeightKg)}");
        grid.AddRow(CliStrings.Cli_RowTrainer, Markup.Escape(config.Devices.Trainer is { } trainer ? config.Devices.Describe(trainer) : CliStrings.Cli_AnyDevice));
        grid.AddRow(CliStrings.Cli_RowHr, Markup.Escape(config.Devices.HeartRate is { } heartRate ? config.Devices.Describe(heartRate) : CliStrings.Cli_AnyDevice));
        grid.AddRow(CliStrings.Cli_RowShifters, Markup.Escape(config.Devices.Shifters.Count > 0 ? string.Join(", ", config.Devices.Shifters.Select(config.Devices.Describe)) : CliStrings.Cli_AnyDevice));
        grid.AddRow(CliStrings.Cli_RowWifi, Markup.Escape(config.Devices.WifiTrainers.Count > 0 ? string.Join(", ", config.Devices.WifiTrainers) : CliStrings.Cli_WifiByMdns));
        grid.AddRow(CliStrings.Cli_RowRides, Markup.Escape(config.RidesDirectory));
        var chosen = new[] { (UploadTarget.Intervals, config.Uploads.Intervals), (UploadTarget.Garmin, config.Uploads.Garmin) };
        grid.AddRow(CliStrings.Cli_RowUpload, string.Join(", ", chosen.Where(t => t.Item2).Select(t => t.Item1)) + (config.Uploads.Automatic ? CliStrings.Cli_Automatic : CliStrings.Cli_Ask));
        grid.AddRow(CliStrings.Cli_RowResistance, Loc.Format(
            CliStrings.Cli_ResistanceValue,
            ResistanceNames[config.Trainer.Resistance],
            config.Trainer.ChainringTeeth,
            config.Trainer.CogTeeth,
            config.Rider.RollingResistance));
        grid.AddRow(CliStrings.Cli_RowMaxHr, config.Rider.MaxHeartRate > 0 ? Loc.Format(CliStrings.Cli_Bpm, config.Rider.MaxHeartRate) : CliStrings.Cli_NotSet);
        grid.AddRow(CliStrings.Cli_RowGarmin, GarminClient.IsSignedIn(config) ? CliStrings.Cli_SignedIn : CliStrings.Cli_NotSignedIn);
        AnsiConsole.Write(grid);
        return Task.FromResult(0);
    }
}
