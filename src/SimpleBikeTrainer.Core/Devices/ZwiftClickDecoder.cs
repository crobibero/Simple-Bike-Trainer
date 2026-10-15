namespace SimpleBikeTrainer.Devices;

[Flags]
public enum ClickButtons
{
    None = 0,
    Plus = 1 << 0,
    Minus = 1 << 1,
    A = 1 << 2,
    B = 1 << 3,
    Y = 1 << 4,
    Z = 1 << 5,
    Up = 1 << 6,
    Down = 1 << 7,
    Left = 1 << 8,
    Right = 1 << 9,
}

/// <summary>
/// Decodes Zwift controller notifications: a type byte, then a protobuf body whose buttons are active-low (0 = pressed).
/// The protocol is undocumented; this follows ajchellew/zwiftplay and jonasbark/swiftcontrol.
/// </summary>
public sealed class ZwiftClickDecoder
{
    public static readonly byte[] RideOn = "RideOn"u8.ToArray();

    private const byte ClickNotification = 0x37; // Click (v1): field 1 = plus, field 2 = minus
    private const byte RideNotification = 0x23; // Click V2 and Ride: field 1 = button bitmap
    private const byte BatteryNotification = 0x19;

    // Zwift Ride / Click V2 bitmap. On a Click V2 the right puck's "+" is SHFT_UP_R and the left puck's "-" is
    // SHFT_UP_L. Zwift treats every right-side shifter as harder and every left-side one as easier.
    private static readonly (int Mask, ClickButtons Button)[] RideMasks =
    [
        (0x00001, ClickButtons.Left),
        (0x00002, ClickButtons.Up),
        (0x00004, ClickButtons.Right),
        (0x00008, ClickButtons.Down),
        (0x00010, ClickButtons.A),
        (0x00020, ClickButtons.B),
        (0x00040, ClickButtons.Y),
        (0x00080, ClickButtons.Z),
        (0x00100, ClickButtons.Minus), // shift up, left
        (0x00200, ClickButtons.Minus), // shift down, left
        (0x01000, ClickButtons.Plus), // shift up, right
        (0x02000, ClickButtons.Plus), // shift down, right
    ];

    private ClickButtons _held;

    public int? Battery { get; private set; }

    /// <summary>Decodes one notification and returns the buttons that went down with it (not ones still held).</summary>
    public ClickButtons Decode(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty || message.StartsWith(RideOn))
        {
            return ClickButtons.None;
        }

        var body = message[1..];
        ClickButtons held;
        switch (message[0])
        {
            case ClickNotification:
                held = DecodeClick(body);
                break;
            case RideNotification:
                held = DecodeRide(body);
                break;
            case BatteryNotification:
                if (TryReadVarintField(body, 1, out var level))
                {
                    Battery = (int)Math.Clamp(level, 0, 100);
                }

                return ClickButtons.None;
            default:
                return ClickButtons.None;
        }

        var pressed = held & ~_held;
        _held = held;
        return pressed;
    }

    private static ClickButtons DecodeClick(ReadOnlySpan<byte> body)
    {
        var held = ClickButtons.None;
        if (TryReadVarintField(body, 1, out var plus) && plus == 0)
        {
            held |= ClickButtons.Plus;
        }

        if (TryReadVarintField(body, 2, out var minus) && minus == 0)
        {
            held |= ClickButtons.Minus;
        }

        return held;
    }

    private static ClickButtons DecodeRide(ReadOnlySpan<byte> body)
    {
        if (!TryReadVarintField(body, 1, out var map))
        {
            return ClickButtons.None;
        }

        var held = ClickButtons.None;
        foreach (var (mask, button) in RideMasks)
        {
            if ((map & (ulong)mask) == 0)
            {
                held |= button;
            }
        }

        return held;
    }

    /// <summary>Finds a top-level varint field in a protobuf message, skipping fields of other wire types.</summary>
    internal static bool TryReadVarintField(ReadOnlySpan<byte> data, int field, out ulong value)
    {
        value = 0;
        var pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out var tag))
            {
                return false;
            }

            var number = (int)(tag >> 3);
            switch ((int)(tag & 0x7))
            {
                case 0:
                    if (!TryReadVarint(data, ref pos, out var v))
                    {
                        return false;
                    }

                    if (number == field)
                    {
                        value = v;
                        return true;
                    }

                    break;
                case 1:
                    pos += 8;
                    break;
                case 2:
                    if (!TryReadVarint(data, ref pos, out var len))
                    {
                        return false;
                    }

                    pos += (int)len;
                    break;
                case 5:
                    pos += 4;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && pos < data.Length; shift += 7)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
