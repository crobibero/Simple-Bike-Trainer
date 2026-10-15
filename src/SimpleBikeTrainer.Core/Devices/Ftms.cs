using System.Buffers.Binary;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Devices;

/// <summary>One Indoor Bike Data notification. Fields are null when the trainer did not send them.</summary>
public readonly record struct IndoorBikeData(
    double? SpeedKmh,
    double? CadenceRpm,
    int? PowerWatts,
    int? HeartRate,
    int? TotalDistanceMeters,
    int? ResistanceLevel);

/// <summary>A Fitness Machine Control Point indication: the result of an earlier request.</summary>
public readonly record struct ControlPointResponse(byte RequestOpCode, byte Result)
{
    public bool Succeeded => Result == 0x01;

    public string ResultName => Result switch
    {
        0x01 => CoreStrings.Ftms_Success,
        0x02 => CoreStrings.Ftms_NotSupported,
        0x03 => CoreStrings.Ftms_InvalidParameter,
        0x04 => CoreStrings.Ftms_OperationFailed,
        0x05 => CoreStrings.Ftms_NotPermitted,
        _ => $"0x{Result:x2}",
    };
}

public enum SpinDownState
{
    /// <summary>The trainer accepted the request and wants the rider above <see cref="SpinDownUpdate.TargetHighKmh"/>.</summary>
    SpeedUp,

    /// <summary>Fast enough: stop pedaling and let the flywheel coast.</summary>
    StopPedaling,
    Success,
    Error,
}

/// <summary>One spin-down step. The speed range comes with <see cref="SpinDownState.SpeedUp"/>.</summary>
public readonly record struct SpinDownUpdate(SpinDownState State, double? TargetLowKmh = null, double? TargetHighKmh = null);

/// <summary>Bluetooth Fitness Machine Service (FTMS) encoding, per the FTMS 1.0 specification.</summary>
public static class Ftms
{
    public const byte OpRequestControl = 0x00;
    public const byte OpReset = 0x01;
    public const byte OpSetTargetResistance = 0x04;
    public const byte OpSetTargetPower = 0x05;
    public const byte OpStartOrResume = 0x07;
    public const byte OpStopOrPause = 0x08;
    public const byte OpSetSimulation = 0x11;
    public const byte OpSpinDownControl = 0x13;
    public const byte StatusSpinDown = 0x14;
    public const byte OpResponse = 0x80;

    public static IndoorBikeData ParseIndoorBikeData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return default;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var r = new Reader(data[2..]);
        double? speed = null, cadence = null;
        int? power = null, hr = null, distance = null, resistance = null;

        // Bit 0 is "More Data": when it is clear, the instantaneous speed is present.
        if ((flags & 0x0001) == 0)
        {
            speed = r.U16() / 100.0;
        }

        if ((flags & 0x0002) != 0)
        {
            r.Skip(2); // average speed
        }

        if ((flags & 0x0004) != 0)
        {
            cadence = r.U16() / 2.0;
        }

        if ((flags & 0x0008) != 0)
        {
            r.Skip(2); // average cadence
        }

        if ((flags & 0x0010) != 0)
        {
            distance = r.U24();
        }

        if ((flags & 0x0020) != 0)
        {
            resistance = r.S16();
        }

        if ((flags & 0x0040) != 0)
        {
            power = r.S16();
        }

        if ((flags & 0x0080) != 0)
        {
            r.Skip(2); // average power
        }

        if ((flags & 0x0100) != 0)
        {
            r.Skip(5); // total energy, energy per hour, energy per minute
        }

        if ((flags & 0x0200) != 0)
        {
            hr = r.U8();
        }

        // A truncated packet leaves the reader's values null; reading it does not throw.
        return new IndoorBikeData(
            r.Ok ? speed : null,
            r.Ok ? cadence : null,
            r.Ok ? power : null,
            r.Ok ? hr : null,
            r.Ok ? distance : null,
            r.Ok ? resistance : null);
    }

    public static ControlPointResponse? ParseControlPointResponse(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == OpResponse ? new ControlPointResponse(data[1], data[2]) : null;

    /// <summary>Target Setting Features bit 15 of the Fitness Machine Feature characteristic.</summary>
    public static bool SupportsSpinDown(ReadOnlySpan<byte> features) =>
        features.Length >= 8 && (BinaryPrimitives.ReadUInt32LittleEndian(features[4..]) & (1u << 15)) != 0;

    public static byte[] StartSpinDown() => [OpSpinDownControl, 0x01];

    /// <summary>
    /// The control point's answer to <see cref="StartSpinDown"/>: on success it carries the speed range to reach, as
    /// two uint16 values in 0.01 km/h.
    /// </summary>
    public static SpinDownUpdate? ParseSpinDownResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != OpResponse || data[1] != OpSpinDownControl)
        {
            return null;
        }

        if (data[2] != 0x01)
        {
            return new SpinDownUpdate(SpinDownState.Error);
        }

        return data.Length >= 7
            ? new SpinDownUpdate(SpinDownState.SpeedUp, BinaryPrimitives.ReadUInt16LittleEndian(data[3..]) / 100.0, BinaryPrimitives.ReadUInt16LittleEndian(data[5..]) / 100.0)
            : new SpinDownUpdate(SpinDownState.SpeedUp);
    }

    /// <summary>A Fitness Machine Status notification about a spin-down (op code 0x14), or null for any other status.</summary>
    public static SpinDownUpdate? ParseSpinDownStatus(ReadOnlySpan<byte> data) =>
        data.Length < 2 || data[0] != StatusSpinDown ? null : data[1] switch
        {
            0x01 => new SpinDownUpdate(SpinDownState.SpeedUp),
            0x02 => new SpinDownUpdate(SpinDownState.Success),
            0x03 => new SpinDownUpdate(SpinDownState.Error),
            0x04 => new SpinDownUpdate(SpinDownState.StopPedaling),
            _ => null,
        };

    public static byte[] RequestControl() => [OpRequestControl];

    public static byte[] Reset() => [OpReset];

    public static byte[] Start() => [OpStartOrResume];

    public static byte[] SetTargetPower(int watts)
    {
        var w = (short)Math.Clamp(watts, 0, 4000);
        var bytes = new byte[3];
        bytes[0] = OpSetTargetPower;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(1), w);
        return bytes;
    }

    /// <param name="gradePercent">Road grade in percent.</param>
    /// <param name="crr">Rolling resistance coefficient, e.g. 0.004.</param>
    /// <param name="cw">Wind resistance coefficient (0.5 * air density * CdA) in kg/m, e.g. 0.51.</param>
    public static byte[] SetSimulation(double gradePercent, double crr, double cw, double windSpeedMs = 0)
    {
        var bytes = new byte[7];
        bytes[0] = OpSetSimulation;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(1), (short)Math.Round(windSpeedMs * 1000));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(3), (short)Math.Round(Math.Clamp(gradePercent, -40, 40) * 100));
        bytes[5] = (byte)Math.Clamp(Math.Round(crr * 10000), 0, 255);
        bytes[6] = (byte)Math.Clamp(Math.Round(cw * 100), 0, 255);
        return bytes;
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public bool Ok { get; private set; } = true;

        public int U8() => Take(1) ? _data[_pos - 1] : 0;

        public int U16() => Take(2) ? BinaryPrimitives.ReadUInt16LittleEndian(_data[(_pos - 2)..]) : 0;

        public int S16() => Take(2) ? BinaryPrimitives.ReadInt16LittleEndian(_data[(_pos - 2)..]) : 0;

        public int U24() => Take(3) ? _data[_pos - 3] | (_data[_pos - 2] << 8) | (_data[_pos - 1] << 16) : 0;

        public void Skip(int n) => Take(n);

        private bool Take(int n)
        {
            if (_pos + n > _data.Length)
            {
                Ok = false;
                return false;
            }

            _pos += n;
            return true;
        }
    }
}
