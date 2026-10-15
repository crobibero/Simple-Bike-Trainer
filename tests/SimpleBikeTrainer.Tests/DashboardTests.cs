using Spectre.Console;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Ui;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Tests;

public class DashboardTests
{
    [Fact]
    public async Task RendersEveryKindOfRide()
    {
        var workouts = new[]
        {
            WorkoutTextParser.Parse("- Warmup 1m 55-65% 80rpm\n\n[x] 2x\n- Hard 30s 120%\n- Easy 30s 50%\n\n- Cooldown 1m ramp 60-40%", "Odd [name]"),
            Workout.FreeRide(),
            RampTest.Create(),
        };

        foreach (var workout in workouts)
        {
            await using var trainer = new FakeTrainer();
            var session = new RideSession(workout, new RideOptions(200, 20000, new RiderModel(), Path.GetTempFileName(), SpeedFactor: 500), trainer, null, []);
            var rendered = new List<string>();
            session.Updated += s =>
            {
                rendered.Add(Render(s, workout, trainer));
                if (s.Moving.TotalSeconds >= 70)
                {
                    session.Send(RideCommand.Finish);
                }
            };

            var result = await session.RunAsync(TestContext.Current.CancellationToken);

            Assert.Contains(rendered, r => r.Contains("POWER 3s", StringComparison.Ordinal));
            var summary = new StringWriter();
            Summary.Write(Console(summary), result);
            Assert.Contains("Normalized power", summary.ToString(), StringComparison.Ordinal);
            File.Delete(result.FitPath!);
        }
    }

    [Fact]
    public void ProfileMarksPosition()
    {
        var workout = WorkoutTextParser.Parse("- 1m 50%\n- 1m 100%", "x");

        var profile = Dashboard.Profile(workout, 60, complete: false, width: 10);

        Assert.Contains("[bold yellow]", profile, StringComparison.Ordinal);
        Assert.Equal(10, profile.Count(c => c == '▁' || c == '█' || c == '▄' || c == '▅'));
    }

    private static string Render(RideSnapshot s, Workout workout, ISensorDevice device)
    {
        var writer = new StringWriter();
        Console(writer).Write(Dashboard.Render(s, workout, [device], "a [notice]"));
        return writer.ToString();
    }

    private static IAnsiConsole Console(TextWriter writer) => AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(writer),
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
    });
}
