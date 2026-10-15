using Spectre.Console;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Uploads;

namespace SimpleBikeTrainer.Commands;

/// <summary>Reads <c>--upload-to intervals,garmin</c>.</summary>
internal static class UploadTargetList
{
    public static (IReadOnlyList<UploadTarget> Targets, ValidationResult Result) Parse(string text)
    {
        var targets = new List<UploadTarget>();
        foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Enum.TryParse also takes numbers, which name no destination.
            if (!Enum.TryParse<UploadTarget>(name, ignoreCase: true, out var target) || !Enum.IsDefined(target) || char.IsDigit(name[0]))
            {
                return ([], ValidationResult.Error(Loc.Format(CliStrings.Cli_BadUploadTarget, name)));
            }

            targets.Add(target);
        }

        return ([.. targets.Distinct()], ValidationResult.Success());
    }
}
