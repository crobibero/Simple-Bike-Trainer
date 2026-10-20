namespace SimpleBikeTrainer.AntPlus;

/// <summary>ANT serial messages, per the ANT Message Protocol and Usage document: sync, length, ID, data, checksum.</summary>
internal static class AntMessage
{
    public const byte Sync = 0xA4;

    public const byte ChannelEvent = 0x40;
    public const byte UnassignChannel = 0x41;
    public const byte AssignChannel = 0x42;
    public const byte ChannelPeriod = 0x43;
    public const byte SearchTimeout = 0x44;
    public const byte RfFrequency = 0x45;
    public const byte NetworkKey = 0x46;
    public const byte ResetSystem = 0x4A;
    public const byte OpenChannel = 0x4B;
    public const byte CloseChannel = 0x4C;
    public const byte RequestMessage = 0x4D;
    public const byte BroadcastData = 0x4E;
    public const byte AcknowledgedData = 0x4F;
    public const byte BurstData = 0x50;
    public const byte ChannelId = 0x51;
    public const byte Capabilities = 0x54;
    public const byte LowPrioritySearchTimeout = 0x63;
    public const byte LibConfig = 0x6E;
    public const byte StartupMessage = 0x6F;

    /// <summary>The message ID a <see cref="ChannelEvent"/> carries when it is an event rather than a response.</summary>
    public const byte EventMessageId = 0x01;

    public const byte EventTransferTxCompleted = 0x05;
    public const byte EventTransferTxFailed = 0x06;
    public const byte EventChannelClosed = 0x07;
    public const byte EventRxFailGoToSearch = 0x08;

    /// <summary>LibConfig flags: received data carries the sender's channel ID and the signal strength.</summary>
    public const byte ExtendedChannelId = 0x80;
    public const byte ExtendedRssi = 0x40;

    public static byte[] Encode(byte id, ReadOnlySpan<byte> data)
    {
        var message = new byte[data.Length + 4];
        message[0] = Sync;
        message[1] = (byte)data.Length;
        message[2] = id;
        data.CopyTo(message.AsSpan(3));
        message[^1] = Checksum(message.AsSpan(0, message.Length - 1));
        return message;
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes)
        {
            sum ^= b;
        }

        return sum;
    }
}

internal readonly record struct AntFrame(byte Id, byte[] Data);

/// <summary>A USB read can hold several messages or part of one; bytes before a sync byte, and bad checksums, are dropped.</summary>
internal sealed class AntFrameReader
{
    private readonly List<byte> _buffer = [];

    public List<AntFrame> Push(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes);
        var frames = new List<AntFrame>();
        while (true)
        {
            var start = _buffer.IndexOf(AntMessage.Sync);
            if (start < 0)
            {
                _buffer.Clear();
                return frames;
            }

            _buffer.RemoveRange(0, start);
            if (_buffer.Count < 2)
            {
                return frames;
            }

            var total = _buffer[1] + 4;
            if (_buffer.Count < total)
            {
                return frames;
            }

            var message = _buffer.GetRange(0, total).ToArray();
            if (AntMessage.Checksum(message.AsSpan(0, total - 1)) == message[^1])
            {
                frames.Add(new AntFrame(message[2], message[3..^1]));
                _buffer.RemoveRange(0, total);
            }
            else
            {
                // A sync byte inside data, or a corrupt message: look for the next sync byte.
                _buffer.RemoveAt(0);
            }
        }
    }
}

internal readonly record struct AntReceived(byte[] Page, int? DeviceNumber, byte DeviceType, byte TransmissionType, sbyte? Rssi)
{
    /// <summary>Reads a broadcast, acknowledged or burst message: channel, 8 bytes, then the extended data if flagged.</summary>
    internal static AntReceived? Parse(byte[] data)
    {
        if (data.Length < 9)
        {
            return null;
        }

        var page = data[1..9];
        if (data.Length < 10)
        {
            return new AntReceived(page, null, 0, 0, null);
        }

        var flags = data[9];
        var at = 10;
        int? number = null;
        byte type = 0, transmission = 0;
        if ((flags & AntMessage.ExtendedChannelId) != 0 && data.Length >= at + 4)
        {
            // The upper nibble of the transmission type extends the 16-bit device number to 20 bits.
            transmission = data[at + 3];
            number = data[at] | (data[at + 1] << 8) | ((transmission & 0xF0) << 12);
            type = data[at + 2];
            at += 4;
        }

        sbyte? rssi = null;
        // Measurement type, value, threshold. Only type 0x20 is dBm: an ANTUSB2 cannot measure and sends 0x10 with a value of 0.
        if ((flags & AntMessage.ExtendedRssi) != 0 && data.Length >= at + 3 && data[at] == 0x20)
        {
            rssi = (sbyte)data[at + 1];
        }

        return new AntReceived(page, number, type, transmission, rssi);
    }
}

public sealed class AntException(string message) : Exception(message);
