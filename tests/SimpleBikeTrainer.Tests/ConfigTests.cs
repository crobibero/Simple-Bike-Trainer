namespace SimpleBikeTrainer.Tests;

public class ConfigTests
{
    [Fact]
    public void MovesSettingsFromTheOldFolderOnce()
    {
        var home = Directory.CreateTempSubdirectory("trainer-config").FullName;
        try
        {
            var legacy = Path.Combine(home, "trainer");
            Directory.CreateDirectory(Path.Combine(legacy, "garmin"));
            File.WriteAllText(Path.Combine(legacy, "config.json"), """{ "rider": { "ftp": 251 } }""");
            File.WriteAllText(Path.Combine(legacy, "garmin", "garmin_tokens.json"), "{}");

            Assert.True(AppConfig.MoveLegacyFolder(home));
            var current = Path.Combine(home, AppConfig.FolderName);
            Assert.False(Directory.Exists(legacy));
            Assert.Equal(251, AppConfig.Load(Path.Combine(current, "config.json")).Rider.Ftp);
            Assert.True(File.Exists(Path.Combine(current, "garmin", "garmin_tokens.json")));

            // With the new folder in place, an old one that reappears is left alone.
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "config.json"), "{}");
            Assert.False(AppConfig.MoveLegacyFolder(home));
            Assert.True(File.Exists(Path.Combine(legacy, "config.json")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void StartsEmptyWithNoOldFolder()
    {
        var home = Directory.CreateTempSubdirectory("trainer-config").FullName;
        try
        {
            Assert.False(AppConfig.MoveLegacyFolder(home));
            Assert.False(Directory.Exists(Path.Combine(home, AppConfig.FolderName)));
            Assert.True(AppConfig.Load(Path.Combine(home, AppConfig.FolderName, "config.json")).StartCountdown);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void MovesRidesFromTheOldDefaultFolder()
    {
        var data = Directory.CreateTempSubdirectory("trainer-data").FullName;
        try
        {
            var legacy = Path.Combine(data, "trainer", "rides");
            var current = Path.Combine(data, AppConfig.FolderName, "rides");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "a.fit"), "a");
            File.WriteAllText(Path.Combine(legacy, "a.fit.json"), "{}");

            Assert.Equal(current, AppConfig.MoveLegacyRides(data, legacy));
            Assert.True(File.Exists(Path.Combine(current, "a.fit")));
            Assert.True(File.Exists(Path.Combine(current, "a.fit.json")));
            Assert.False(Directory.Exists(Path.Combine(data, "trainer")));

            // Run again, nothing is left to move; a custom folder is kept.
            Assert.Equal(current, AppConfig.MoveLegacyRides(data, legacy));
            var custom = Path.Combine(data, "elsewhere");
            Assert.Equal(custom, AppConfig.MoveLegacyRides(data, custom));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void SaveWritesTheFileAndANewFolderOwnerOnly()
    {
        var home = Directory.CreateTempSubdirectory("trainer-config").FullName;
        try
        {
            var folder = Path.Combine(home, "new");
            var path = Path.Combine(folder, "config.json");
            var config = AppConfig.Load(path);
            config.SetFileApiKey("file-key");
            config.Save();

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            Assert.Equal("file-key", AppConfig.Load(path).Intervals.ApiKey);
            Assert.Equal(["config.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}

/// <summary>The INTERVALS_* variables are process-wide, so these run apart from every other test.</summary>
[CollectionDefinition(nameof(EnvironmentCollection), DisableParallelization = true)]
public sealed class EnvironmentCollection;

[Collection(nameof(EnvironmentCollection))]
public sealed class ConfigEnvironmentTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("trainer-config").FullName;
    private readonly string? _key = Environment.GetEnvironmentVariable("INTERVALS_API_KEY");
    private readonly string? _athlete = Environment.GetEnvironmentVariable("INTERVALS_ATHLETE_ID");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("INTERVALS_API_KEY", _key);
        Environment.SetEnvironmentVariable("INTERVALS_ATHLETE_ID", _athlete);
        Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public void SavingWhatSettingsShowsKeepsTheEnvironmentOutOfTheFile()
    {
        var path = Path.Combine(_home, "config.json");
        File.WriteAllText(path, """{ "intervals": { "apiKey": "file-key", "athleteId": "i1" } }""");
        Environment.SetEnvironmentVariable("INTERVALS_API_KEY", "env-key");
        Environment.SetEnvironmentVariable("INTERVALS_ATHLETE_ID", "i999");

        // What the Settings page does: show the file's values, then save them back.
        var config = AppConfig.Load(path);
        config.SetFileApiKey(config.FileApiKey);
        config.SetFileAthleteId(config.FileAthleteId);
        config.Save();

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("env-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("i999", json, StringComparison.Ordinal);
        Assert.Equal("env-key", config.Intervals.ApiKey);
        Assert.Equal("i999", config.Intervals.AthleteId);

        Environment.SetEnvironmentVariable("INTERVALS_API_KEY", null);
        Environment.SetEnvironmentVariable("INTERVALS_ATHLETE_ID", null);
        var reloaded = AppConfig.Load(path);
        Assert.Equal("file-key", reloaded.Intervals.ApiKey);
        Assert.Equal("i1", reloaded.Intervals.AthleteId);
    }
}
