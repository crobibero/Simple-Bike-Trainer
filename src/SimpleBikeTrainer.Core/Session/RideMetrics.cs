namespace SimpleBikeTrainer.Session;

/// <summary>Running totals for a whole ride, fed one sample per riding second.</summary>
public sealed class RideMetrics
{
    /// <summary>Intervals.icu's default power zones, as upper bounds in %FTP.</summary>
    public static readonly int[] DefaultPowerZones = [55, 75, 90, 105, 120, 150, 999];

    public static readonly int[] PeakWindows = [5, 60, 300, 1200];

    private const int NpWindow = 30;

    // Prefix sums of power make every rolling average O(1).
    private readonly List<long> _prefix = [0];
    private readonly Dictionary<int, double> _peaks = [];
    private readonly int[] _powerZones;
    private readonly int[]? _hrZones;
    private double _np4Sum;
    private int _np4Count;
    private long _hrSum;
    private int _hrCount;
    private long _cadenceSum;
    private int _cadenceCount;

    public RideMetrics(int ftp, int wPrime, int[]? powerZones = null, int[]? hrZones = null)
    {
        Ftp = Math.Max(ftp, 1);
        WPrime = Math.Max(wPrime, 1);
        WBalance = WPrime;
        _powerZones = powerZones is { Length: > 0 } ? powerZones : DefaultPowerZones;
        _hrZones = hrZones is { Length: > 0 } ? hrZones : null;
        TimeInPowerZones = new int[_powerZones.Length];
        TimeInHrZones = new int[_hrZones?.Length ?? 0];
    }

    public int Ftp { get; }

    public int WPrime { get; }

    public int Seconds { get; private set; }

    public long TotalWorkJoules { get; private set; }

    public double KiloJoules => TotalWorkJoules / 1000.0;

    /// <summary>Food energy, assuming the usual 24% gross efficiency: about 1 kcal per kJ of work.</summary>
    public double Kilocalories => KiloJoules / 4.184 / 0.24;

    public double AveragePower => Seconds == 0 ? 0 : (double)TotalWorkJoules / Seconds;

    public int MaxPower { get; private set; }

    /// <summary>Normalized Power: the 4th-power mean of 30 s rolling averages. The plain average until 30 s are in.</summary>
    public double NormalizedPower => _np4Count == 0 ? AveragePower : Math.Pow(_np4Sum / _np4Count, 0.25);

    public double IntensityFactor => NormalizedPower / Ftp;

    public double TrainingStressScore => Seconds * NormalizedPower * IntensityFactor / (Ftp * 3600.0) * 100;

    public double? AverageHeartRate => _hrCount == 0 ? null : (double)_hrSum / _hrCount;

    public int? MaxHeartRate { get; private set; }

    /// <summary>Average cadence while pedaling, like most head units.</summary>
    public double? AverageCadence => _cadenceCount == 0 ? null : (double)_cadenceSum / _cadenceCount;

    public int? MaxCadence { get; private set; }

    public double DistanceMeters { get; private set; }

    public double AverageSpeedMs => Seconds == 0 ? 0 : DistanceMeters / Seconds;

    public double MaxSpeedMs { get; private set; }

    /// <summary>Climbing in meters, from the virtual grade.</summary>
    public double ElevationGain { get; private set; }

    public double ElevationLoss { get; private set; }

    private double? _lastAltitude;

    /// <summary>W' balance in joules (Skiba differential model, CP = FTP).</summary>
    public double WBalance { get; private set; }

    public double MinWBalance { get; private set; } = double.MaxValue;

    public int[] TimeInPowerZones { get; }

    public int[] TimeInHrZones { get; }

    public IReadOnlyList<int> PowerZoneBounds => _powerZones;

    /// <summary>Best average power for each window in <see cref="PeakWindows"/> that the ride has reached.</summary>
    public IReadOnlyDictionary<int, double> Peaks => _peaks;

    public double RollingAverage(int seconds)
    {
        var n = _prefix.Count - 1;
        var w = Math.Min(seconds, n);
        return w == 0 ? 0 : (double)(_prefix[n] - _prefix[n - w]) / w;
    }

    public void Add(Sample s)
    {
        var p = Math.Max(0, s.Power);
        Seconds++;
        TotalWorkJoules += p;
        MaxPower = Math.Max(MaxPower, p);
        _prefix.Add(_prefix[^1] + p);
        var n = _prefix.Count - 1;

        if (n >= NpWindow)
        {
            var avg = (_prefix[n] - _prefix[n - NpWindow]) / (double)NpWindow;
            _np4Sum += Math.Pow(avg, 4);
            _np4Count++;
        }

        foreach (var window in PeakWindows)
        {
            if (n >= window)
            {
                var avg = (_prefix[n] - _prefix[n - window]) / (double)window;
                if (!_peaks.TryGetValue(window, out var best) || avg > best)
                {
                    _peaks[window] = avg;
                }
            }
        }

        if (s.AltitudeMeters is { } altitude)
        {
            if (_lastAltitude is { } last)
            {
                ElevationGain += Math.Max(0, altitude - last);
                ElevationLoss += Math.Max(0, last - altitude);
            }

            _lastAltitude = altitude;
        }

        if (s.HeartRate is > 0 and var hr)
        {
            _hrSum += hr;
            _hrCount++;
            MaxHeartRate = Math.Max(MaxHeartRate ?? 0, hr);
            if (_hrZones is not null)
            {
                TimeInHrZones[ZoneIndex(_hrZones, hr)]++;
            }
        }

        if (s.Cadence is > 0 and var cadence)
        {
            _cadenceSum += cadence;
            _cadenceCount++;
            MaxCadence = Math.Max(MaxCadence ?? 0, cadence);
        }

        DistanceMeters = s.DistanceMeters;
        MaxSpeedMs = Math.Max(MaxSpeedMs, s.SpeedMs);
        TimeInPowerZones[ZoneIndex(_powerZones, p * 100.0 / Ftp)]++;

        if (p > Ftp)
        {
            WBalance -= p - Ftp;
        }
        else
        {
            WBalance += (WPrime - WBalance) * (Ftp - p) / WPrime;
        }

        MinWBalance = Math.Min(MinWBalance, WBalance);
    }

    private static int ZoneIndex(int[] upperBounds, double value)
    {
        for (var i = 0; i < upperBounds.Length; i++)
        {
            if (value <= upperBounds[i])
            {
                return i;
            }
        }

        return upperBounds.Length - 1;
    }
}

/// <summary>Totals for one lap, which is one workout step unless the rider laps by hand.</summary>
public sealed class LapMetrics(DateTime start, int index)
{
    private long _work;
    private long _hrSum;
    private int _hrCount;
    private long _cadenceSum;
    private int _cadenceCount;
    private int _onTarget;
    private int _withTarget;
    private double _startDistance = double.NaN;

    public int Index { get; } = index;

    public DateTime Start { get; } = start;

    public DateTime End { get; private set; } = start;

    public int Seconds { get; private set; }

    public long WorkJoules => _work;

    public double AveragePower => Seconds == 0 ? 0 : (double)_work / Seconds;

    public int MaxPower { get; private set; }

    public double? AverageHeartRate => _hrCount == 0 ? null : (double)_hrSum / _hrCount;

    public int? MaxHeartRate { get; private set; }

    public double? AverageCadence => _cadenceCount == 0 ? null : (double)_cadenceSum / _cadenceCount;

    public int? MaxCadence { get; private set; }

    public double DistanceMeters { get; private set; }

    public double MaxSpeedMs { get; private set; }

    /// <summary>Share of seconds within 5% (at least 10 W) of the target, or null in free ride.</summary>
    public double? Compliance => _withTarget == 0 ? null : (double)_onTarget / _withTarget;

    public void Add(Sample s)
    {
        if (double.IsNaN(_startDistance))
        {
            _startDistance = s.DistanceMeters - s.SpeedMs;
        }

        Seconds++;
        End = s.Time;
        _work += Math.Max(0, s.Power);
        MaxPower = Math.Max(MaxPower, s.Power);
        DistanceMeters = s.DistanceMeters - _startDistance;
        MaxSpeedMs = Math.Max(MaxSpeedMs, s.SpeedMs);

        if (s.HeartRate is > 0 and var hr)
        {
            _hrSum += hr;
            _hrCount++;
            MaxHeartRate = Math.Max(MaxHeartRate ?? 0, hr);
        }

        if (s.Cadence is > 0 and var cadence)
        {
            _cadenceSum += cadence;
            _cadenceCount++;
            MaxCadence = Math.Max(MaxCadence ?? 0, cadence);
        }

        if (s.TargetWatts is > 0 and var target)
        {
            _withTarget++;
            if (Math.Abs(s.Power - target) <= Math.Max(10, target * 0.05))
            {
                _onTarget++;
            }
        }
    }
}
