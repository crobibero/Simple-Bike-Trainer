namespace SimpleBikeTrainer.Devices;

/// <summary>ANT+ data pages, per the Fitness Equipment (FE-C) and Heart Rate device profiles. Each page is 8 bytes.</summary>
public static class AntPlusPages
{
    public const byte FitnessEquipmentType = 17;
    public const byte HeartRateType = 120;

    /// <summary>Channel periods in 1/32768 s: about 4 Hz each.</summary>
    public const ushort FitnessEquipmentPeriod = 8192;
    public const ushort HeartRatePeriod = 8070;

    public const byte GeneralFePage = 0x10;
    public const byte TrainerPage = 0x19;
    public const byte BasicResistancePage = 0x30;
    public const byte TargetPowerPage = 0x31;
    public const byte WindResistancePage = 0x32;
    public const byte TrackResistancePage = 0x33;
    public const byte UserConfigurationPage = 0x37;

    /// <summary>Page 49: hold <paramref name="watts"/> (ERG), in 0.25 W.</summary>
    public static byte[] TargetPower(int watts)
    {
        var quarterWatts = (ushort)Math.Clamp(watts * 4, 0, 4000 * 4);
        return [TargetPowerPage, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, (byte)quarterWatts, (byte)(quarterWatts >> 8)];
    }

    /// <summary>Page 48: a fixed resistance in percent of the trainer's maximum, in 0.5 %. 0 takes the trainer out of ERG.</summary>
    public static byte[] BasicResistance(double percent) =>
        [BasicResistancePage, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, (byte)Math.Clamp(Math.Round(percent * 2), 0, 200)];

    /// <summary>Page 51, simulation: the grade in 0.01 % from −200 %, and the rolling resistance in 0.00005.</summary>
    public static byte[] TrackResistance(double gradePercent, double crr)
    {
        var grade = (ushort)Math.Round((Math.Clamp(gradePercent, -200, 200) + 200) * 100);
        var rolling = (byte)Math.Clamp(Math.Round(crr / 0.00005), 0, 254);
        return [TrackResistancePage, 0xFF, 0xFF, 0xFF, 0xFF, (byte)grade, (byte)(grade >> 8), rolling];
    }

    /// <summary>Page 50, simulation: the wind resistance coefficient in 0.01 kg/m, still air, no drafting.</summary>
    public static byte[] WindResistance(double windResistance) =>
        [WindResistancePage, 0xFF, 0xFF, 0xFF, 0xFF, (byte)Math.Clamp(Math.Round(windResistance * 100), 0, 186), 0, 100];

    /// <summary>Page 55: rider in 0.01 kg, bike in 0.05 kg (12 bits over bytes 4 and 5), a 700c wheel (0.70 m), no gear ratio.</summary>
    public static byte[] UserConfiguration(double riderKg, double bikeKg)
    {
        var rider = (ushort)Math.Clamp(Math.Round(riderKg * 100), 0, 65534);
        var bike = (int)Math.Clamp(Math.Round(bikeKg / 0.05), 0, 1000);
        return [UserConfigurationPage, (byte)rider, (byte)(rider >> 8), 0xFF, (byte)((bike & 0x0F) << 4), (byte)(bike >> 4), 70, 0];
    }

    /// <summary>Every heart rate page carries the computed heart rate in its last byte; 0 means none.</summary>
    public static int? HeartRate(ReadOnlySpan<byte> page) => page.Length >= 8 && page[7] > 0 ? page[7] : null;

    /// <summary>Heart rate page 7, battery status: the level in percent, when the strap reports one.</summary>
    public static int? HeartRateBattery(ReadOnlySpan<byte> page) =>
        page.Length >= 8 && (page[0] & 0x7F) == 7 && page[1] <= 100 ? page[1] : null;
}

/// <summary>Speed, heart rate and distance come in page 16, power and cadence in page 25; each page 25 gives one reading.</summary>
public sealed class FecDecoder
{
    private double? _speedKmh;
    private int? _heartRate;
    private int _distance;
    private byte? _lastDistance;

    public IndoorBikeData? Decode(ReadOnlySpan<byte> page)
    {
        if (page.Length < 8)
        {
            return null;
        }

        switch (page[0])
        {
            case AntPlusPages.GeneralFePage:
            {
                // Speed in mm/s; 0xFFFF is invalid.
                var speed = page[4] | (page[5] << 8);
                _speedKmh = speed == 0xFFFF ? null : speed * 0.0036;
                _heartRate = page[6] is 0 or 0xFF ? null : page[6];

                // Distance in metres, rolling over at 256, when capability bit 2 says the trainer counts it.
                if ((page[7] & 0x04) != 0)
                {
                    if (_lastDistance is { } last)
                    {
                        _distance += (byte)(page[3] - last);
                    }

                    _lastDistance = page[3];
                }

                return null;
            }

            case AntPlusPages.TrainerPage:
            {
                int? cadence = page[2] == 0xFF ? null : page[2];

                // 12 bits: byte 5, then the low nibble of byte 6. 0xFFF is invalid.
                var power = page[5] | ((page[6] & 0x0F) << 8);
                return new IndoorBikeData(_speedKmh, cadence, power == 0xFFF ? null : power, _heartRate, _lastDistance is null ? null : _distance, null);
            }

            default:
                return null;
        }
    }
}
