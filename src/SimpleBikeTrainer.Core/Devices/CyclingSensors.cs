using System.Buffers.Binary;

namespace SimpleBikeTrainer.Devices;

/// <summary>A crank revolution counter and the time of the last revolution in 1/1024 s, both rolling over at 65536.</summary>
public readonly record struct CrankData(int Revolutions, int EventTime);

/// <summary>Cycling Power Measurement: power, and the crank revolutions when the meter sends them.</summary>
public readonly record struct PowerMeasurement(int PowerWatts, CrankData? Crank);

/// <summary>Bluetooth Cycling Power (0x2A63) and Cycling Speed and Cadence (0x2A5B) measurement decoding.</summary>
public static class CyclingSensors
{
    public static PowerMeasurement? ParsePowerMeasurement(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var power = BinaryPrimitives.ReadInt16LittleEndian(data[2..]);
        var offset = 4;

        // The optional fields before the crank data: pedal power balance, accumulated torque, wheel revolutions.
        if ((flags & 0x0001) != 0)
        {
            offset += 1;
        }

        if ((flags & 0x0004) != 0)
        {
            offset += 2;
        }

        if ((flags & 0x0010) != 0)
        {
            offset += 6;
        }

        CrankData? crank = null;
        if ((flags & 0x0020) != 0 && data.Length >= offset + 4)
        {
            crank = new CrankData(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]), BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]));
        }

        return new PowerMeasurement(Math.Max(0, (int)power), crank);
    }

    /// <summary>The crank revolutions of a CSC measurement; null for a speed-only sensor's.</summary>
    public static CrankData? ParseCscMeasurement(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || (data[0] & 0x02) == 0)
        {
            return null;
        }

        // Wheel revolutions (a 32-bit count and a 16-bit time) come first when bit 0 is set.
        var offset = (data[0] & 0x01) != 0 ? 7 : 1;
        return data.Length >= offset + 4
            ? new CrankData(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]), BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]))
            : null;
    }
}

/// <summary>
/// Cadence from successive <see cref="CrankData"/>. Sensors repeat the last revolution while the crank is still, so
/// cadence drops to 0 once no new one has come for a few seconds.
/// </summary>
public sealed class CrankCadence
{
    private const long StoppedMs = 3000;

    // Longer than this and the 16-bit event time may have wrapped, so the next revolution starts afresh.
    private const long StaleMs = 60_000;

    private CrankData? _last;
    private long _lastEventMs;
    private double? _rpm;

    /// <param name="nowMs">A monotonic clock in milliseconds, such as <see cref="Environment.TickCount64"/>.</param>
    /// <returns>Null until two revolutions have been seen.</returns>
    public double? Update(CrankData crank, long nowMs)
    {
        if (_last is not { } last || nowMs - _lastEventMs > StaleMs)
        {
            _last = crank;
            _lastEventMs = nowMs;
            return _rpm = _rpm is null ? null : 0;
        }

        var revolutions = (crank.Revolutions - last.Revolutions) & 0xFFFF;
        var ticks = (crank.EventTime - last.EventTime) & 0xFFFF;
        if (revolutions > 0 && ticks > 0)
        {
            _last = crank;
            _lastEventMs = nowMs;
            var rpm = revolutions * 60.0 * 1024 / ticks;

            // Over 250 rpm is a glitch, such as a counter reset: keep the last good value.
            if (rpm <= 250)
            {
                _rpm = rpm;
            }
        }
        else if (nowMs - _lastEventMs >= StoppedMs)
        {
            _rpm = 0;
        }

        return _rpm;
    }
}
