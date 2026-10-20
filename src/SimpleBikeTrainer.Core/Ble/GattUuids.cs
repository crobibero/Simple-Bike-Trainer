namespace SimpleBikeTrainer.Ble;

/// <summary>The BLE services and characteristics this app talks to, as BlueZ spells them (lowercase, 128-bit).</summary>
public static class GattUuids
{
    public static readonly string FitnessMachine = Short(0x1826);
    public static readonly string FitnessMachineFeature = Short(0x2ACC);
    public static readonly string IndoorBikeData = Short(0x2AD2);
    public static readonly string SupportedPowerRange = Short(0x2AD8);
    public static readonly string FitnessMachineControlPoint = Short(0x2AD9);
    public static readonly string FitnessMachineStatus = Short(0x2ADA);

    public static readonly string HeartRate = Short(0x180D);
    public static readonly string HeartRateMeasurement = Short(0x2A37);

    public static readonly string CyclingPower = Short(0x1818);
    public static readonly string CyclingPowerMeasurement = Short(0x2A63);
    public static readonly string CyclingSpeedCadence = Short(0x1816);
    public static readonly string CscMeasurement = Short(0x2A5B);

    public static readonly string Battery = Short(0x180F);
    public static readonly string BatteryLevel = Short(0x2A19);

    // Zwift Click, Click V2, Play and Ride controllers. The older ones expose the 0001 service, newer firmware FC82.
    public const string ZwiftService = "00000001-19ca-4651-86e5-fa29dcdd09d1";
    public static readonly string ZwiftRideService = Short(0xFC82);
    public const string ZwiftAsync = "00000002-19ca-4651-86e5-fa29dcdd09d1";
    public const string ZwiftSyncRx = "00000003-19ca-4651-86e5-fa29dcdd09d1";
    public const string ZwiftSyncTx = "00000004-19ca-4651-86e5-fa29dcdd09d1";

    /// <summary>Zwift, Inc. in BLE manufacturer data.</summary>
    public const ushort ZwiftManufacturerId = 0x094A;

    public static string Short(ushort id) => $"0000{id:x4}-0000-1000-8000-00805f9b34fb";

    /// <summary>A UUID spelled as BlueZ spells it. CoreBluetooth shortens the standard ones to "180D", in capitals.</summary>
    public static string Normalize(string uuid) => uuid.Length switch
    {
        4 => Short(Convert.ToUInt16(uuid, 16)),
        8 => $"{uuid.ToLowerInvariant()}-0000-1000-8000-00805f9b34fb",
        _ => uuid.ToLowerInvariant(),
    };
}
