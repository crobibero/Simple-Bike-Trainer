using System.Text.Json.Serialization;
using SimpleBikeTrainer.Ble;
using System.Text.Json;

namespace SimpleBikeTrainer;

public sealed class IntervalsConfig
{
    /// <summary>Intervals.icu &gt; Settings &gt; Developer Settings &gt; API key. INTERVALS_API_KEY overrides it.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>"0" means the athlete who owns the API key. INTERVALS_ATHLETE_ID overrides it.</summary>
    public string AthleteId { get; set; } = "0";
}

public sealed class RiderConfig
{
    /// <summary>Used when Intervals.icu has no FTP, or with --offline.</summary>
    public int Ftp { get; set; } = 200;

    /// <summary>W' in joules for W' balance, when Intervals.icu has none.</summary>
    public int WPrime { get; set; } = 20000;

    /// <summary>Rider weight, when Intervals.icu has none.</summary>
    public double WeightKg { get; set; } = 80;

    public double BikeWeightKg { get; set; } = 9;

    public double RollingResistance { get; set; } = 0.004;

    /// <summary>Sets heart rate zones (50/60/70/80/90% of it) when Intervals.icu has none. 0 leaves heart rate uncolored.</summary>
    public int MaxHeartRate { get; set; }
}

public sealed class SavedDevice
{
    public DeviceKind Kind { get; set; }

    public DeviceTransport Transport { get; set; }

    /// <summary>The Bluetooth address, ANT+ device number, or Wi-Fi trainer's MAC or "host:port".</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The name the device advertises, to show it while it is not connected.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The name the rider gave it, which wins over <see cref="Name"/>. Empty for none.</summary>
    public string Alias { get; set; } = string.Empty;

    [JsonIgnore]
    public DeviceId Id => new(Transport, Address);
}

public sealed class DevicesConfig
{
    /// <summary>At most one trainer and one heart rate monitor; with none saved, Connect takes any in range.</summary>
    public List<SavedDevice> Saved { get; set; } = [];

    /// <summary>True once the rider picked devices from a scan; an empty heart rate or shifter slot then means none.</summary>
    public bool Picked { get; set; }

    /// <summary>The BlueZ adapter, e.g. "hci0", on Linux. Empty uses the first one; other systems have only one.</summary>
    public string Adapter { get; set; } = string.Empty;

    /// <summary>Wi-Fi trainers at a fixed address, "host" or "host:port", for networks where the scan cannot find them by mDNS.</summary>
    public List<string> WifiTrainers { get; set; } = [];

    [JsonIgnore]
    public DeviceId? Trainer => Saved.FirstOrDefault(d => d.Kind == DeviceKind.Trainer)?.Id;

    [JsonIgnore]
    public DeviceId? HeartRate => Saved.FirstOrDefault(d => d.Kind == DeviceKind.HeartRate)?.Id;

    [JsonIgnore]
    public IReadOnlyList<DeviceId> Shifters => [.. Saved.Where(d => d.Kind == DeviceKind.Shifter).Select(d => d.Id)];

    public SavedDevice? Find(DeviceId id) => Saved.FirstOrDefault(d => d.Id == id);

    /// <summary>A trainer or heart rate monitor replaces the saved one; controllers add up. A device saved already keeps its alias.</summary>
    public void Pick(DeviceKind kind, DeviceId id, string name)
    {
        var existing = Find(id);
        Saved.RemoveAll(d => d != existing && (d.Id == id || (kind != DeviceKind.Shifter && d.Kind == kind)));
        if (existing is null)
        {
            Saved.Add(new SavedDevice { Kind = kind, Transport = id.Transport, Address = id.Address, Name = name });
        }
        else
        {
            existing.Kind = kind;
            existing.Name = name;
        }
    }

    public void Unpick(DeviceKind kind) => Saved.RemoveAll(d => d.Kind == kind);

    /// <summary>False when the device is not saved, or the name is the same.</summary>
    public bool RememberName(DeviceId id, string name)
    {
        if (Find(id) is not { } saved || saved.Name == name)
        {
            return false;
        }

        saved.Name = name;
        return true;
    }

    /// <summary>A saved device's name: the rider's, else the advertised one, else its address.</summary>
    public string NameOf(DeviceId id) => Find(id) is { Alias.Length: > 0 } saved ? saved.Alias : AdvertisedName(id);

    public string AdvertisedName(DeviceId id) => Find(id) is { Name.Length: > 0 } saved ? saved.Name : id.Address;

    /// <summary>A blank name, or the advertised one, clears the alias.</summary>
    public void Rename(DeviceId id, string name)
    {
        if (Find(id) is { } saved)
        {
            name = name.Trim();
            saved.Alias = name == AdvertisedName(id) ? string.Empty : name;
        }
    }

    public string Describe(DeviceId id) => NameOf(id) is var name && name != id.Address ? $"{name} ({id})" : id.ToString();

    public void Forget(DeviceId id) => Saved.RemoveAll(d => d.Id == id);
}

/// <summary>How the trainer is held off ERG steps: in free rides, on routes, and with ERG switched off.</summary>
public enum ResistanceMode
{
    /// <summary>The app works out the power the virtual gear, cadence and grade cost, and holds the trainer at it.</summary>
    Power,

    /// <summary>The trainer simulates the road itself; the app scales it by the virtual gear, for a single-speed setup.</summary>
    SimulationVirtualGears,

    /// <summary>The trainer simulates the road itself, and the rider shifts the bike's own gears.</summary>
    SimulationRealGears,
}

public sealed class TrainerConfig
{
    public ResistanceMode Resistance { get; set; } = ResistanceMode.Power;

    /// <summary>The chain's one real gear on a single-speed setup such as the Zwift Cog, for simulation with virtual gears.</summary>
    public int ChainringTeeth { get; set; } = 34;

    public int CogTeeth { get; set; } = 17;
}

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>Where finished rides go. Each one is ticked by default on the summary screen when enabled.</summary>
public sealed class UploadsConfig
{
    public bool Intervals { get; set; } = true;

    public bool Garmin { get; set; }

    /// <summary>Upload to the enabled destinations as soon as a ride is saved, without asking.</summary>
    public bool Automatic { get; set; }
}

public sealed class AppConfig
{
    public IntervalsConfig Intervals { get; set; } = new();

    public RiderConfig Rider { get; set; } = new();

    public DevicesConfig Devices { get; set; } = new();

    public TrainerConfig Trainer { get; set; } = new();

    /// <summary>Where finished rides are saved as FIT files.</summary>
    public string RidesDirectory { get; set; } = DefaultRidesDirectory;

    /// <summary>Display units: "metric" or "imperial" (mi, mph, lb). Values are stored in metric either way.</summary>
    public UnitSystem Units { get; set; } = UnitSystem.Metric;

    public UploadsConfig Uploads { get; set; } = new();

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    /// <summary>Beep 3-2-1 before each workout step and once as it starts.</summary>
    public bool SoundCues { get; set; } = true;

    /// <summary>Show a 3, 2, 1, GO countdown on the ride page before the ride starts.</summary>
    public bool StartCountdown { get; set; } = true;

    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath;

    // The values as the file had them, so Save never writes ones that came from the environment.
    private string _fileApiKey = string.Empty;
    private string _fileAthleteId = "0";
    private string? _envApiKey;
    private string? _envAthleteId;

    /// <summary>The app's folder under <see cref="ConfigHome"/> and <see cref="DataHome"/>.</summary>
    public const string FolderName = "simple-bike-trainer";

    /// <summary>The folder name used before the app was named; <see cref="Load"/> moves settings and rides out of it.</summary>
    private const string LegacyFolderName = "trainer";

    public static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
        ? xdg
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    public static string DefaultPath => System.IO.Path.Combine(ConfigHome, FolderName, "config.json");

    /// <summary>Moves <c>trainer/</c> (settings and Garmin tokens) to <c>simple-bike-trainer/</c> when only the old one exists.</summary>
    public static bool MoveLegacyFolder(string configHome)
    {
        var legacy = System.IO.Path.Combine(configHome, LegacyFolderName);
        var current = System.IO.Path.Combine(configHome, FolderName);
        if (Directory.Exists(current) || !File.Exists(System.IO.Path.Combine(legacy, "config.json")))
        {
            return false;
        }

        try
        {
            Directory.Move(legacy, current);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left where it is; the app starts with fresh settings.
            return false;
        }
    }

    public static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
        ? xdg
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static string DefaultRidesDirectory => System.IO.Path.Combine(DataHome, FolderName, "rides");

    /// <summary>
    /// Moves rides out of <c>trainer/rides</c> when that old default is in use, and returns the folder to use. A custom
    /// folder is returned as is; a file whose name already exists in the new folder stays in the old one.
    /// </summary>
    public static string MoveLegacyRides(string dataHome, string ridesDirectory)
    {
        var legacy = System.IO.Path.Combine(dataHome, LegacyFolderName, "rides");
        var current = System.IO.Path.Combine(dataHome, FolderName, "rides");
        if (System.IO.Path.GetFullPath(ridesDirectory).TrimEnd('/') != legacy.TrimEnd('/'))
        {
            return ridesDirectory;
        }

        if (!Directory.Exists(legacy))
        {
            return current;
        }

        try
        {
            Directory.CreateDirectory(current);
            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                var target = System.IO.Path.Combine(current, System.IO.Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Move(file, target);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
                var parent = System.IO.Path.Combine(dataHome, LegacyFolderName);
                if (!Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Rides that did not move stay in the old folder, which is left in place.
        }

        return current;
    }

    /// <summary>Garmin Connect session tokens. Never the password.</summary>
    [JsonIgnore]
    public string GarminTokenDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "garmin");

    public bool HasApiKey => !string.IsNullOrWhiteSpace(Intervals.ApiKey);

    public static AppConfig Load(string? path = null)
    {
        if (path is null)
        {
            MoveLegacyFolder(ConfigHome);
            path = DefaultPath;
        }

        var config = File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.AppConfig) ?? new AppConfig()
            : new AppConfig();
        config.Path = path;
        config._fileApiKey = config.Intervals.ApiKey;
        config._fileAthleteId = config.Intervals.AthleteId;
        config.RidesDirectory = MoveLegacyRides(DataHome, config.RidesDirectory);

        if (Environment.GetEnvironmentVariable("INTERVALS_API_KEY") is { Length: > 0 } key)
        {
            config._envApiKey = key;
            config.Intervals.ApiKey = key;
        }

        if (Environment.GetEnvironmentVariable("INTERVALS_ATHLETE_ID") is { Length: > 0 } athlete)
        {
            config._envAthleteId = athlete;
            config.Intervals.AthleteId = athlete;
        }

        return config;
    }

    /// <summary>The API key in the file, without INTERVALS_API_KEY.</summary>
    [JsonIgnore]
    public string FileApiKey => _fileApiKey;

    /// <summary>The athlete ID in the file, without INTERVALS_ATHLETE_ID.</summary>
    [JsonIgnore]
    public string FileAthleteId => _fileAthleteId;

    /// <summary>Sets the API key that <see cref="Save"/> writes to the file. INTERVALS_API_KEY still overrides it.</summary>
    public void SetFileApiKey(string key)
    {
        _fileApiKey = key;
        Intervals.ApiKey = _envApiKey ?? key;
    }

    /// <summary>Sets the athlete ID that <see cref="Save"/> writes to the file. INTERVALS_ATHLETE_ID still overrides it.</summary>
    public void SetFileAthleteId(string athleteId)
    {
        _fileAthleteId = athleteId;
        Intervals.AthleteId = _envAthleteId ?? athleteId;
    }

    /// <summary>Writes the file owner-only, through a temporary file, since it holds the API key.</summary>
    public void Save()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var temp = System.IO.Path.Combine(folder, $".config.{Guid.NewGuid():N}.tmp");
        var (key, athlete) = (Intervals.ApiKey, Intervals.AthleteId);
        (Intervals.ApiKey, Intervals.AthleteId) = (_fileApiKey, _fileAthleteId);
        try
        {
            using var stream = new FileStream(temp, options);
            JsonSerializer.Serialize(stream, this, ConfigJsonContext.Default.AppConfig);
        }
        finally
        {
            (Intervals.ApiKey, Intervals.AthleteId) = (key, athlete);
        }

        File.Move(temp, Path, overwrite: true);
    }
}
