using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;
using Spectre.Console.Rendering;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Commands;

internal class TrainerSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; set; }
}

/// <summary>Loads the settings file, the one <c>--config</c> names or the default one, and runs the command with it.</summary>
internal abstract class TrainerCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : TrainerSettings
{
    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        AppConfig config;
        try
        {
            config = AppConfig.Load(settings.ConfigPath);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(CliStrings.Cli_ConfigUnreadable)}[/] {Markup.Escape(e.Message)}");
            return 1;
        }

        return await ExecuteAsync(config, settings, cancellationToken);
    }

    protected abstract Task<int> ExecuteAsync(AppConfig config, TSettings settings, CancellationToken ct);
}

/// <summary>Prints the help from <see cref="CliStrings"/>, so it can be translated, in place of Spectre's English help.</summary>
internal sealed class LocalizedHelp : IHelpProvider
{
    public IEnumerable<IRenderable> Write(ICommandModel model, ICommandInfo? command) => [new Markup(CliStrings.Cli_Help)];
}
