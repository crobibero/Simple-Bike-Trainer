using System.Globalization;
using AsyncAwaitBestPractices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Uploads;

namespace SimpleBikeTrainer.Desktop.ViewModels;

/// <summary>A saved device on the Settings page, to rename or forget.</summary>
public sealed partial class SavedDeviceSetting : ObservableObject
{
    public required string Kind { get; init; }

    public required string Address { get; init; }

    /// <summary>What the device calls itself, shown when the name box is empty; Save then goes back to it.</summary>
    public required string AdvertisedName { get; init; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Forget was pressed once; a second press forgets the device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForgetLabel))]
    public partial bool ConfirmForget { get; set; }

    public string ForgetLabel => ConfirmForget ? Strings.Settings_ConfirmForget : Strings.Settings_Forget;
}

/// <summary>Intervals.icu, rider, upload and Garmin settings.</summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly AppConfig _config;
    private GarminClient? _garmin;

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _config = main.Config;
        // The file's values: Save writes these fields back, and the environment's must never land in the file.
        ApiKey = _config.FileApiKey;
        AthleteId = _config.FileAthleteId;
        Ftp = _config.Rider.Ftp.ToString(CultureInfo.InvariantCulture);
        Imperial = _config.Units == UnitSystem.Imperial;
        Weight = Show(_config.Rider.WeightKg);
        BikeWeight = Show(_config.Rider.BikeWeightKg);
        MaxHeartRate = _config.Rider.MaxHeartRate > 0 ? _config.Rider.MaxHeartRate.ToString(CultureInfo.InvariantCulture) : string.Empty;
        UploadIntervals = _config.Uploads.Intervals;
        UploadGarmin = _config.Uploads.Garmin;
        Automatic = _config.Uploads.Automatic;
        Theme = _config.Theme;
        SoundCues = _config.SoundCues;
        StartCountdown = _config.StartCountdown;
        GarminSignedIn = GarminClient.IsSignedIn(_config);
        GarminStatus = GarminSignedIn ? Strings.Settings_SignedIn : Strings.Settings_NotSignedIn;
        var saved = _config.Devices;
        foreach (var (kind, address) in new[] { (Strings.Device_Trainer, saved.Trainer), (Strings.Device_HeartRate, saved.HeartRate) }
            .Concat(saved.Shifters.Select(s => (Strings.Device_Controller, s)))
            .Where(d => !string.IsNullOrEmpty(d.Item2)))
        {
            SavedDevices.Add(new SavedDeviceSetting { Kind = kind, Address = address, AdvertisedName = saved.AdvertisedName(address), Name = saved.NameOf(address) });
        }

        HasDevices = SavedDevices.Count > 0;
        RefreshGarminAsync().SafeFireAndForget();
    }

    public string ConfigPath => _config.Path;

    [ObservableProperty] public partial string ApiKey { get; set; }

    [ObservableProperty] public partial string AthleteId { get; set; }

    [ObservableProperty] public partial string Ftp { get; set; }

    /// <summary>Show and enter distances in miles, speeds in mph and weights in pounds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeightLabel), nameof(BikeWeightLabel))]
    public partial bool Imperial { get; set; }

    public string WeightLabel => Loc.Format(Strings.Settings_RiderWeight, UnitsChosen.WeightUnit());

    public string BikeWeightLabel => Loc.Format(Strings.Settings_BikeWeight, UnitsChosen.WeightUnit());

    private UnitSystem UnitsChosen => Imperial ? UnitSystem.Imperial : UnitSystem.Metric;

    [ObservableProperty] public partial string Weight { get; set; }

    [ObservableProperty] public partial string BikeWeight { get; set; }

    /// <summary>Optional; colors heart rate by zone when Intervals.icu has no heart rate zones.</summary>
    [ObservableProperty] public partial string MaxHeartRate { get; set; }

    [ObservableProperty] public partial bool UploadIntervals { get; set; }

    [ObservableProperty] public partial bool UploadGarmin { get; set; }

    [ObservableProperty] public partial bool Automatic { get; set; }

    /// <summary>Applied as soon as it is picked, so the choice can be seen; Cancel puts the saved one back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeSystem), nameof(ThemeLight), nameof(ThemeDark))]
    public partial ThemeChoice Theme { get; set; }

    public bool ThemeSystem
    {
        get => Theme == ThemeChoice.System;
        set
        {
            if (value)
            {
                Theme = ThemeChoice.System;
            }
        }
    }

    public bool ThemeLight
    {
        get => Theme == ThemeChoice.Light;
        set
        {
            if (value)
            {
                Theme = ThemeChoice.Light;
            }
        }
    }

    public bool ThemeDark
    {
        get => Theme == ThemeChoice.Dark;
        set
        {
            if (value)
            {
                Theme = ThemeChoice.Dark;
            }
        }
    }

    [ObservableProperty] public partial bool SoundCues { get; set; }

    [ObservableProperty] public partial bool StartCountdown { get; set; }

    partial void OnThemeChanged(ThemeChoice value) => App.ApplyTheme(value);

    [ObservableProperty] public partial string GarminEmail { get; set; } = string.Empty;

    [ObservableProperty] public partial string GarminPassword { get; set; } = string.Empty;

    [ObservableProperty] public partial string GarminCode { get; set; } = string.Empty;

    [ObservableProperty] public partial bool GarminNeedsCode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GarminSignedOut))]
    public partial bool GarminSignedIn { get; set; }

    [ObservableProperty] public partial bool GarminBusy { get; set; }

    [ObservableProperty] public partial string GarminStatus { get; set; }

    /// <summary>The saved devices, to rename or forget.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SavedDeviceSetting> SavedDevices { get; } = [];

    [ObservableProperty] public partial bool HasDevices { get; set; }

    /// <summary>Forget all was pressed once; a second press forgets every device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForgetAllLabel))]
    public partial bool ConfirmForgetAll { get; set; }

    public string ForgetAllLabel => ConfirmForgetAll ? Strings.Settings_ConfirmForgetAll : Strings.Settings_ForgetDevices;

    [ObservableProperty] public partial string? Error { get; set; }

    public bool GarminSignedOut => !GarminSignedIn;

    [RelayCommand]
    private async Task SaveAsync()
    {
        var units = UnitsChosen;
        if (!int.TryParse(Ftp, CultureInfo.InvariantCulture, out var ftp) || ftp is < 50 or > 600
            || !TryKg(Weight, out var weight) || weight is < 30 or > 200
            || !TryKg(BikeWeight, out var bike) || bike is < 0 or > 40)
        {
            Error = Loc.Format(Strings.Settings_RangeError, units.FormatWeight(30), units.FormatWeight(200), units.FormatWeight(40));
            return;
        }

        var maxHr = 0;
        if (!string.IsNullOrWhiteSpace(MaxHeartRate) && (!int.TryParse(MaxHeartRate, CultureInfo.InvariantCulture, out maxHr) || maxHr is < 100 or > 250))
        {
            Error = Strings.Settings_MaxHrError;
            return;
        }

        Apply(ftp, weight, bike, maxHr);
        _config.Save();
        _main.Devices.SavedChanged();
        Dispose();
        _main.ShowHome();
        await _main.Home.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void Cancel()
    {
        App.ApplyTheme(_config.Theme);
        Dispose();
        _main.ShowHome();
    }

    [RelayCommand]
    private async Task GarminSignInAsync()
    {
        GarminBusy = true;
        try
        {
            _garmin ??= new GarminClient(_config);
            GarminStatus = Strings.Settings_SigningIn;
            if (await _garmin.SignInAsync(GarminEmail.Trim(), GarminPassword, CancellationToken.None))
            {
                GarminNeedsCode = true;
                GarminStatus = Strings.Settings_CodeSent;
            }
            else
            {
                await GarminSignedInAsync();
            }
        }
        catch (Exception e) when (e is GarminException or HttpRequestException or TaskCanceledException or IOException)
        {
            GarminStatus = e.Message;
        }
        finally
        {
            GarminPassword = string.Empty;
            GarminBusy = false;
        }
    }

    [RelayCommand]
    private async Task GarminSubmitCodeAsync()
    {
        if (_garmin is null)
        {
            return;
        }

        GarminBusy = true;
        try
        {
            await _garmin.SubmitMfaAsync(GarminCode, CancellationToken.None);
            GarminNeedsCode = false;
            GarminCode = string.Empty;
            await GarminSignedInAsync();
        }
        catch (Exception e) when (e is GarminException or HttpRequestException or TaskCanceledException or IOException)
        {
            GarminStatus = e.Message;
        }
        finally
        {
            GarminBusy = false;
        }
    }

    [RelayCommand]
    private void GarminSignOut()
    {
        _garmin ??= new GarminClient(_config);
        _garmin.SignOut();

        GarminSignedIn = false;
        UploadGarmin = false;
        _config.Uploads.Garmin = false;
        _config.Save();
        GarminStatus = Strings.Settings_NotSignedIn;
    }

    /// <summary>Forgets one device on the second press; the first only asks.</summary>
    [RelayCommand]
    private void ForgetDevice(SavedDeviceSetting device)
    {
        if (!device.ConfirmForget)
        {
            device.ConfirmForget = true;
            return;
        }

        _main.Devices.Forget(device.Address);
        SavedDevices.Remove(device);
        HasDevices = SavedDevices.Count > 0;
    }

    [RelayCommand]
    private static void KeepDevice(SavedDeviceSetting device) => device.ConfirmForget = false;

    /// <summary>Forgets every device on the second press; the first only asks.</summary>
    [RelayCommand]
    private void ForgetDevices()
    {
        if (!ConfirmForgetAll)
        {
            ConfirmForgetAll = true;
            return;
        }

        _config.Devices = new DevicesConfig { Adapter = _config.Devices.Adapter };
        _config.Save();
        _main.Devices.SavedChanged();
        SavedDevices.Clear();
        HasDevices = false;
        ConfirmForgetAll = false;
    }

    [RelayCommand]
    private void KeepDevices() => ConfirmForgetAll = false;

    public void Dispose()
    {
        _garmin?.Dispose();
        _garmin = null;
    }

    private async Task GarminSignedInAsync()
    {
        var name = await _garmin!.GetNameAsync(CancellationToken.None);
        GarminSignedIn = true;
        GarminStatus = Loc.Format(Strings.Settings_SignedInAs, name);
        UploadGarmin = true;
        _config.Uploads.Garmin = true;
        _config.Save();
    }

    private async Task RefreshGarminAsync()
    {
        if (!GarminSignedIn)
        {
            return;
        }

        try
        {
            using var garmin = new GarminClient(_config);
            GarminStatus = Loc.Format(Strings.Settings_SignedInAs, await garmin.GetNameAsync(CancellationToken.None));
        }
        catch (Exception e) when (e is GarminException or HttpRequestException or TaskCanceledException or IOException)
        {
            GarminStatus = Loc.Format(Strings.Settings_NameFailed, e.Message);
        }
    }

    private void Apply(int ftp, double weight, double bike, int maxHr)
    {
        _config.SetFileApiKey(ApiKey.Trim());
        _config.SetFileAthleteId(string.IsNullOrWhiteSpace(AthleteId) ? "0" : AthleteId.Trim());
        _config.Rider.Ftp = ftp;
        _config.Rider.WeightKg = weight;
        _config.Rider.BikeWeightKg = bike;
        _config.Rider.MaxHeartRate = maxHr;
        _config.Units = UnitsChosen;
        _config.Uploads.Intervals = UploadIntervals;
        _config.Uploads.Garmin = UploadGarmin;
        _config.Uploads.Automatic = Automatic;
        foreach (var device in SavedDevices)
        {
            _config.Devices.Rename(device.Address, device.Name);
        }
        _config.Theme = Theme;
        _config.SoundCues = SoundCues;
        _config.StartCountdown = StartCountdown;
    }

    /// <summary>Re-shows the weights in the new units when the toggle flips.</summary>
    partial void OnImperialChanged(bool oldValue, bool newValue)
    {
        var from = oldValue ? UnitSystem.Imperial : UnitSystem.Metric;
        if (Weight is not null && double.TryParse(Weight, CultureInfo.InvariantCulture, out var w))
        {
            Weight = Show(from.WeightToKg(w));
        }

        if (BikeWeight is not null && double.TryParse(BikeWeight, CultureInfo.InvariantCulture, out var b))
        {
            BikeWeight = Show(from.WeightToKg(b));
        }
    }

    private string Show(double kg) => Math.Round(UnitsChosen.Weight(kg), 1).ToString(CultureInfo.InvariantCulture);

    private bool TryKg(string text, out double kg)
    {
        var ok = double.TryParse(text, CultureInfo.InvariantCulture, out var value);
        kg = UnitsChosen.WeightToKg(value);
        return ok;
    }

}
