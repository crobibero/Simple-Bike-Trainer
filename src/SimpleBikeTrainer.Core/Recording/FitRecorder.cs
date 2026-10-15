using Dynastream.Fit;
using SimpleBikeTrainer.Session;
using DateTime = System.DateTime;
using FitDateTime = Dynastream.Fit.DateTime;

namespace SimpleBikeTrainer.Recording;

/// <summary>
/// Collects a ride and writes it as a FIT activity file: one record per riding second, one lap per workout step,
/// timer events around pauses, and a session summary. Intervals.icu reads the laps as intervals.
/// </summary>
public sealed class FitRecorder(DateTime startUtc)
{
    private readonly List<Mesg> _timeline = [];
    private readonly List<LapMesg> _laps = [];

    public DateTime StartUtc { get; } = startUtc;

    public int RecordCount { get; private set; }

    public void AddRecord(Sample s)
    {
        var record = new RecordMesg();
        record.SetTimestamp(Fit(s.Time));
        record.SetPower((ushort)Math.Clamp(s.Power, 0, ushort.MaxValue - 1));
        if (s.Cadence is { } cadence)
        {
            record.SetCadence((byte)Math.Clamp(cadence, 0, 254));
        }

        if (s.HeartRate is > 0 and var hr)
        {
            record.SetHeartRate((byte)Math.Clamp(hr, 0, 254));
        }

        record.SetSpeed((float)s.SpeedMs);
        record.SetDistance((float)s.DistanceMeters);
        if (s.AltitudeMeters is { } altitude)
        {
            record.SetEnhancedAltitude((float)altitude);
            record.SetAltitude((float)altitude);
        }

        _timeline.Add(record);
        RecordCount++;
    }

    public void AddTimerEvent(DateTime time, bool start)
    {
        var evt = new EventMesg();
        evt.SetTimestamp(Fit(time));
        evt.SetEvent(Event.Timer);
        evt.SetEventType(start ? EventType.Start : EventType.StopAll);
        evt.SetTimerTrigger((byte)TimerTrigger.Manual);
        _timeline.Add(evt);
    }

    public void AddLap(LapMetrics lap, bool manual)
    {
        if (lap.Seconds == 0)
        {
            return;
        }

        var mesg = new LapMesg();
        mesg.SetMessageIndex((ushort)_laps.Count);
        mesg.SetTimestamp(Fit(lap.End));
        mesg.SetStartTime(Fit(lap.Start));
        mesg.SetEvent(Event.Lap);
        mesg.SetEventType(EventType.Stop);
        mesg.SetLapTrigger(manual ? LapTrigger.Manual : LapTrigger.Time);
        mesg.SetSport(Sport.Cycling);
        mesg.SetSubSport(SubSport.VirtualActivity);
        mesg.SetTotalElapsedTime((float)(lap.End - lap.Start).TotalSeconds);
        mesg.SetTotalTimerTime(lap.Seconds);
        mesg.SetTotalDistance((float)lap.DistanceMeters);
        mesg.SetTotalWork((uint)lap.WorkJoules);
        mesg.SetTotalCalories((ushort)Math.Round(lap.WorkJoules / 1000.0 / 4.184 / 0.24));
        mesg.SetAvgPower((ushort)Math.Round(lap.AveragePower));
        mesg.SetMaxPower((ushort)lap.MaxPower);
        mesg.SetAvgSpeed(lap.Seconds == 0 ? 0 : (float)(lap.DistanceMeters / lap.Seconds));
        mesg.SetMaxSpeed((float)lap.MaxSpeedMs);
        if (lap.AverageHeartRate is { } hr)
        {
            mesg.SetAvgHeartRate((byte)Math.Round(hr));
            mesg.SetMaxHeartRate((byte)lap.MaxHeartRate!.Value);
        }

        if (lap.AverageCadence is { } cadence)
        {
            mesg.SetAvgCadence((byte)Math.Round(cadence));
            mesg.SetMaxCadence((byte)lap.MaxCadence!.Value);
        }

        _laps.Add(mesg);
    }

    /// <summary>Writes the activity. Can be called more than once, e.g. for a checkpoint mid-ride.</summary>
    public void Save(string path, RideMetrics metrics, DateTime endUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
        {
            Write(stream, metrics, endUtc);
        }

        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public void Write(Stream stream, RideMetrics metrics, DateTime endUtc)
    {
        var encoder = new Encode(stream, ProtocolVersion.V20);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(1);
        fileId.SetTimeCreated(Fit(StartUtc));
        fileId.SetProductName(AppInfo.Name);
        encoder.Write(fileId);

        var device = new DeviceInfoMesg();
        device.SetTimestamp(Fit(StartUtc));
        device.SetDeviceIndex(DeviceIndex.Creator);
        device.SetManufacturer(Manufacturer.Development);
        device.SetProduct(1);
        device.SetProductName(AppInfo.Name);
        device.SetSoftwareVersion(1.0f);
        encoder.Write(device);

        AddStart(encoder);
        foreach (var mesg in _timeline)
        {
            encoder.Write(mesg);
        }

        var stop = new EventMesg();
        stop.SetTimestamp(Fit(endUtc));
        stop.SetEvent(Event.Timer);
        stop.SetEventType(EventType.StopAll);
        encoder.Write(stop);

        foreach (var lap in _laps)
        {
            encoder.Write(lap);
        }

        var session = new SessionMesg();
        session.SetMessageIndex(0);
        session.SetTimestamp(Fit(endUtc));
        session.SetStartTime(Fit(StartUtc));
        session.SetEvent(Event.Session);
        session.SetEventType(EventType.Stop);
        session.SetTrigger(SessionTrigger.ActivityEnd);
        session.SetSport(Sport.Cycling);
        session.SetSubSport(SubSport.VirtualActivity);
        session.SetTotalElapsedTime((float)(endUtc - StartUtc).TotalSeconds);
        session.SetTotalTimerTime(metrics.Seconds);
        session.SetTotalDistance((float)metrics.DistanceMeters);
        session.SetTotalAscent((ushort)Math.Round(Math.Min(metrics.ElevationGain, ushort.MaxValue - 1)));
        session.SetTotalDescent((ushort)Math.Round(Math.Min(metrics.ElevationLoss, ushort.MaxValue - 1)));
        session.SetTotalWork((uint)metrics.TotalWorkJoules);
        session.SetTotalCalories((ushort)Math.Round(metrics.Kilocalories));
        session.SetAvgPower((ushort)Math.Round(metrics.AveragePower));
        session.SetMaxPower((ushort)metrics.MaxPower);
        session.SetNormalizedPower((ushort)Math.Round(metrics.NormalizedPower));
        session.SetThresholdPower((ushort)metrics.Ftp);
        session.SetIntensityFactor((float)metrics.IntensityFactor);
        session.SetTrainingStressScore((float)metrics.TrainingStressScore);
        session.SetAvgSpeed((float)metrics.AverageSpeedMs);
        session.SetMaxSpeed((float)metrics.MaxSpeedMs);
        if (metrics.AverageHeartRate is { } hr)
        {
            session.SetAvgHeartRate((byte)Math.Round(hr));
            session.SetMaxHeartRate((byte)metrics.MaxHeartRate!.Value);
        }

        if (metrics.AverageCadence is { } cadence)
        {
            session.SetAvgCadence((byte)Math.Round(cadence));
            session.SetMaxCadence((byte)metrics.MaxCadence!.Value);
        }

        session.SetFirstLapIndex(0);
        session.SetNumLaps((ushort)_laps.Count);
        encoder.Write(session);

        var activity = new ActivityMesg();
        activity.SetTimestamp(Fit(endUtc));
        activity.SetTotalTimerTime(metrics.Seconds);
        activity.SetNumSessions(1);
        activity.SetType(Activity.Manual);
        activity.SetEvent(Event.Activity);
        activity.SetEventType(EventType.Stop);
        var offset = TimeZoneInfo.Local.GetUtcOffset(endUtc);
        activity.SetLocalTimestamp((uint)((long)new FitDateTime(endUtc).GetTimeStamp() + (long)offset.TotalSeconds));
        encoder.Write(activity);

        encoder.Close();
    }

    private void AddStart(Encode encoder)
    {
        var start = new EventMesg();
        start.SetTimestamp(Fit(StartUtc));
        start.SetEvent(Event.Timer);
        start.SetEventType(EventType.Start);
        encoder.Write(start);
    }

    private static FitDateTime Fit(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
