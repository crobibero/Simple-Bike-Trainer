using System.Buffers.Binary;

namespace SimpleBikeTrainer.Dircon;

/// <summary>A 6-byte header (version 1, type, sequence, response code, big-endian payload length), then the payload.</summary>
internal readonly record struct DirconMessage(byte Type, byte Sequence, byte Response, byte[] Payload)
{
    public const int HeaderLength = 6;
    public const int UuidLength = 16;

    public const byte DiscoverServices = 0x01;
    public const byte DiscoverCharacteristics = 0x02;
    public const byte ReadCharacteristic = 0x03;
    public const byte WriteCharacteristic = 0x04;
    public const byte EnableNotifications = 0x05;
    public const byte Notification = 0x06;
    public const byte Error = 0xFF;

    public const byte Success = 0x00;

    // Characteristic properties in a characteristic discovery response.
    public const byte PropertyRead = 0x01;
    public const byte PropertyWrite = 0x02;
    public const byte PropertyNotify = 0x04;

    private const byte Version = 1;

    public byte[] Encode()
    {
        var bytes = new byte[HeaderLength + Payload.Length];
        bytes[0] = Version;
        bytes[1] = Type;
        bytes[2] = Sequence;
        bytes[3] = Response;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)Payload.Length);
        Payload.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    /// <summary>The payload length a header announces. Throws on a version this does not speak.</summary>
    public static int PayloadLength(ReadOnlySpan<byte> header) =>
        header[0] == Version
            ? BinaryPrimitives.ReadUInt16BigEndian(header[4..])
            : throw new InvalidDataException($"DIRCON version {header[0]}");

    public static DirconMessage Decode(ReadOnlySpan<byte> header, byte[] payload) => new(header[1], header[2], header[3], payload);

    /// <summary>UUIDs travel as 16 bytes in written order: 00001826-… is 00 00 18 26 ….</summary>
    public static byte[] UuidPayload(string uuid, params ReadOnlySpan<byte> rest)
    {
        var payload = new byte[UuidLength + rest.Length];
        Convert.FromHexString(uuid.Replace("-", string.Empty, StringComparison.Ordinal)).CopyTo(payload, 0);
        rest.CopyTo(payload.AsSpan(UuidLength));
        return payload;
    }

    /// <summary>A UUID spelled as <see cref="Ble.GattUuids"/> spells them: lowercase, 128-bit, with dashes.</summary>
    public static string ReadUuid(ReadOnlySpan<byte> bytes)
    {
        var hex = Convert.ToHexStringLower(bytes[..UuidLength]);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static List<string> ParseServices(byte[] payload)
    {
        var services = new List<string>();
        for (var i = 0; i + UuidLength <= payload.Length; i += UuidLength)
        {
            services.Add(ReadUuid(payload.AsSpan(i)));
        }

        return services;
    }

    /// <summary>The characteristics in a characteristic discovery response: the service, then UUID and properties each.</summary>
    public static List<(string Uuid, byte Properties)> ParseCharacteristics(byte[] payload)
    {
        var characteristics = new List<(string, byte)>();
        for (var i = UuidLength; i + UuidLength + 1 <= payload.Length; i += UuidLength + 1)
        {
            characteristics.Add((ReadUuid(payload.AsSpan(i)), payload[i + UuidLength]));
        }

        return characteristics;
    }

    public static string DescribeResponse(byte code) => code switch
    {
        0x01 => "unknown message type",
        0x02 => "unexpected error",
        0x03 => "service not found",
        0x04 => "characteristic not found",
        0x05 => "operation not supported",
        0x06 => "write failed",
        0x07 => "unknown protocol",
        _ => $"error 0x{code:X2}",
    };
}
