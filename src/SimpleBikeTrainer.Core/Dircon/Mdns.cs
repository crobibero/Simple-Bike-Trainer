using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SimpleBikeTrainer.Dircon;

/// <summary>One answer in an mDNS response. Which of the fields are set depends on <see cref="Type"/>.</summary>
internal sealed record MdnsRecord(string Name, ushort Type)
{
    /// <summary>A PTR's instance name, or an SRV's host name.</summary>
    public string? Target { get; init; }

    public int Port { get; init; }

    public IPAddress? Address { get; init; }

    public IReadOnlyDictionary<string, string> Txt { get; init; } = new Dictionary<string, string>();
}

/// <summary>The few parts of DNS (RFC 1035) that mDNS service discovery (RFC 6762, 6763) needs.</summary>
internal static class Mdns
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort TxtType = 16;
    public const ushort Srv = 33;

    private const ushort ClassIn = 1;

    public static byte[] BuildQuery(IEnumerable<(string Name, ushort Type)> questions)
    {
        var list = questions.ToList();
        var bytes = new List<byte>(12) { 0, 0, 0, 0 };
        bytes.AddRange([(byte)(list.Count >> 8), (byte)list.Count, 0, 0, 0, 0, 0, 0]);
        foreach (var (name, type) in list)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var encoded = Encoding.UTF8.GetBytes(label);
                bytes.Add((byte)encoded.Length);
                bytes.AddRange(encoded);
            }

            bytes.AddRange([0, (byte)(type >> 8), (byte)type, 0, (byte)ClassIn]);
        }

        return [.. bytes];
    }

    /// <summary>Every answer, authority and additional record in a response. Empty for a query or a malformed packet.</summary>
    public static List<MdnsRecord> Parse(ReadOnlySpan<byte> packet)
    {
        var records = new List<MdnsRecord>();
        try
        {
            if (packet.Length < 12 || (packet[2] & 0x80) == 0)
            {
                return records;
            }

            var questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            var count = BinaryPrimitives.ReadUInt16BigEndian(packet[6..])
                + BinaryPrimitives.ReadUInt16BigEndian(packet[8..])
                + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
            var at = 12;
            for (var i = 0; i < questions; i++)
            {
                ReadName(packet, ref at);
                at += 4;
            }

            for (var i = 0; i < count; i++)
            {
                var name = ReadName(packet, ref at);
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 8)..]);
                at += 10;
                var data = packet.Slice(at, length);
                var record = new MdnsRecord(name, type);
                switch (type)
                {
                    case Ptr:
                        var ptrAt = at;
                        records.Add(record with { Target = ReadName(packet, ref ptrAt) });
                        break;
                    case Srv:
                        var srvAt = at + 6;
                        records.Add(record with { Port = BinaryPrimitives.ReadUInt16BigEndian(data[4..]), Target = ReadName(packet, ref srvAt) });
                        break;
                    case A when length == 4:
                        records.Add(record with { Address = new IPAddress(data) });
                        break;
                    case TxtType:
                        records.Add(record with { Txt = ParseTxt(data) });
                        break;
                }

                at += length;
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A truncated packet: keep the records read before the end.
        }

        return records;
    }

    /// <summary>A TXT record's key=value strings. Keys are case-insensitive.</summary>
    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < data.Length;)
        {
            var length = data[i];
            var entry = Encoding.UTF8.GetString(data.Slice(i + 1, length));
            i += 1 + length;
            var eq = entry.IndexOf('=');
            if (eq > 0)
            {
                txt[entry[..eq]] = entry[(eq + 1)..];
            }
            else if (entry.Length > 0)
            {
                txt[entry] = string.Empty;
            }
        }

        return txt;
    }

    /// <summary>A name, following compression pointers, as dotted text without the final dot.</summary>
    private static string ReadName(ReadOnlySpan<byte> packet, ref int at)
    {
        var labels = new List<string>();
        var position = at;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var length = packet[position];
            if (length == 0)
            {
                if (!jumped)
                {
                    at = position + 1;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (!jumped)
                {
                    at = position + 2;
                }

                jumped = true;
                position = ((length & 0x3F) << 8) | packet[position + 1];
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }

        throw new ArgumentOutOfRangeException(nameof(packet), "Name compression loop");
    }
}
