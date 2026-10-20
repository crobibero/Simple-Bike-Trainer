using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Commands;

/// <summary><c>scan</c>: lists trainers, sensors, heart rate monitors and Zwift controllers, and saves the rider's picks.</summary>
internal sealed class ScanCommand : TrainerCommand<ScanCommand.Settings>
{
    internal sealed class Settings : TrainerSettings
    {
        [CommandOption("--seconds <N>")]
        public int Seconds { get; set; } = 15;
    }

    internal static void Warn(DeviceScanner scanner)
    {
        foreach (var warning in scanner.Warnings)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");
        }
    }

    protected override async Task<int> ExecuteAsync(AppConfig config, Settings settings, CancellationToken ct)
    {
        var seconds = settings.Seconds;
        await using var scanner = await DeviceScanner.CreateAsync(config.Devices, ct);
        Warn(scanner);
        await scanner.StartAsync(ct);
        await AnsiConsole.Status().StartAsync(Loc.Format(CliStrings.Cli_ScanningFor, seconds), async _ =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
            catch (OperationCanceledException)
            {
            }
        });
        await scanner.StopAsync();

        var found = scanner.Found.OrderBy(d => d.Kind).ThenByDescending(d => d.Rssi ?? short.MinValue).ToList();
        var table = new Table().AddColumns(CliStrings.Cli_ColKind, CliStrings.Cli_ColName, CliStrings.Cli_ColTransport, CliStrings.Cli_ColAddress, CliStrings.Cli_ColRssi);
        foreach (var d in found)
        {
            var signal = d.Rssi?.ToString(CultureInfo.CurrentCulture)
                ?? $"[grey]{Markup.Escape(d.Id.Transport switch
                {
                    DeviceTransport.Wifi => CliStrings.Cli_Network,
                    DeviceTransport.AntPlus => CliStrings.Cli_InRange,
                    _ => CliStrings.Cli_Cached,
                })}[/]";
            table.AddRow(d.Kind.ToString(), Markup.Escape(d.Name), $"[bold]{Markup.Escape(d.Id.TransportLabel)}[/]", Markup.Escape(d.Id.Address), signal);
        }

        AnsiConsole.Write(table);
        if (found.Count == 0 || Console.IsInputRedirected)
        {
            return 0;
        }

        // A choice is an index into found, or -1 for "any" and -2 for "none".
        string Choice(int i) => i == -2
            ? Markup.Escape(CliStrings.Cli_None)
            : i < 0
            ? Markup.Escape(CliStrings.Cli_Any)
            : $"{Markup.Escape(found[i].Name)} [grey]({Markup.Escape(found[i].Id.TransportLabel)} {Markup.Escape(found[i].Id.Address)})[/]";
        List<int> Of(DeviceKind kind) => [.. found.Index().Where(f => f.Item.Kind == kind).Select(f => f.Index)];
        void Save(DeviceKind kind, IEnumerable<int> picks)
        {
            config.Devices.Unpick(kind);
            foreach (var i in picks)
            {
                config.Devices.Pick(kind, found[i].Id, found[i].Name);
            }
        }

        if (Of(DeviceKind.Trainer) is { Count: > 0 } trainers)
        {
            var pick = await AnsiConsole.PromptAsync(new SelectionPrompt<int>().Title(Markup.Escape(CliStrings.Cli_PickTrainer))
                .UseConverter(Choice).AddChoices([.. trainers, -1]), ct);
            Save(DeviceKind.Trainer, pick < 0 ? [] : [pick]);
        }

        // A power meter or cadence sensor connects only once picked, so there is no "any".
        foreach (var (kind, title) in new[] { (DeviceKind.PowerMeter, CliStrings.Cli_PickPowerMeter), (DeviceKind.CadenceSensor, CliStrings.Cli_PickCadence) })
        {
            if (Of(kind) is { Count: > 0 } sensors)
            {
                var pick = await AnsiConsole.PromptAsync(new SelectionPrompt<int>().Title(Markup.Escape(title))
                    .UseConverter(Choice).AddChoices([.. sensors, -2]), ct);
                Save(kind, pick < 0 ? [] : [pick]);
            }
        }

        if (Of(DeviceKind.HeartRate) is { Count: > 0 } hrs)
        {
            var pick = await AnsiConsole.PromptAsync(new SelectionPrompt<int>().Title(Markup.Escape(CliStrings.Cli_PickHr))
                .UseConverter(Choice).AddChoices([.. hrs, -1]), ct);
            Save(DeviceKind.HeartRate, pick < 0 ? [] : [pick]);
        }

        if (Of(DeviceKind.Shifter) is { Count: > 0 } shifters)
        {
            var picks = await AnsiConsole.PromptAsync(new MultiSelectionPrompt<int>().Title(Markup.Escape(CliStrings.Cli_PickShifters))
                .UseConverter(Choice).NotRequired().AddChoices(shifters), ct);
            Save(DeviceKind.Shifter, picks);
        }

        // Here an empty slot means "any".
        config.Devices.Picked = false;
        config.Save();
        AnsiConsole.MarkupLine(Markup.Escape(Loc.Format(CliStrings.Cli_SavedTo, config.Path)));
        return 0;
    }
}
