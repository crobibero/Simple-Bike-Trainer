using System.Buffers.Binary;

namespace SimpleBikeTrainer.Devices;

/// <summary>Heart Rate Measurement (0x2A37) decoding.</summary>
public static class HeartRateParser
{
    public static int? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return null;
        }

        // Flag bit 0 says whether the value is a uint8 or a uint16.
        if ((data[0] & 0x01) == 0)
        {
            return data[1];
        }

        return data.Length >= 3 ? BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) : null;
    }
}
