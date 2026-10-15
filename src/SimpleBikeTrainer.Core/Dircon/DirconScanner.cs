using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SimpleBikeTrainer.Ble;

namespace SimpleBikeTrainer.Dircon;

/// <remarks>Queries go from an ephemeral port, so responders answer by unicast (RFC 6762 section 6.7): no port 5353 or multicast lock.</remarks>
public sealed class DirconScanner : IDeviceScanner
{
    public const string ServiceType = "_wahoo-fitness-tnp._tcp.local";

    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private static readonly TimeSpan[] QueryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly TimeSpan QueryEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManualTimeout = TimeSpan.FromSeconds(3);

    private readonly IReadOnlyList<string> _manual;
    private readonly Lock _lock = new();
    private readonly Dictionary<DeviceId, DiscoveredDevice> _found = [];
    private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Host, int Port)> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scan;
    private List<Task> _tasks = [];
    private List<Socket> _sockets = [];

    /// <param name="manual">Trainers typed in by hand, as "host" or "host:port".</param>
    public DirconScanner(IEnumerable<string>? manual = null)
    {
        _manual = [.. manual ?? []];
    }

    public event Action<DiscoveredDevice>? DeviceFound;

    public IReadOnlyList<DiscoveredDevice> Found
    {
        get
        {
            lock (_lock)
            {
                return [.. _found.Values];
            }
        }
    }

    /// <summary>A trainer at a typed address, "host" or "host:port", without checking it is there.</summary>
    public static DiscoveredDevice Manual(string hostAndPort)
    {
        var (host, port) = SplitHostPort(hostAndPort);
        return new DiscoveredDevice(new DirconPeripheral(host, port), DeviceId.Wifi($"{host}:{port}"), host, DeviceKind.Trainer, null);
    }

    /// <summary>A machine with no usable network finds nothing here, and still scans Bluetooth and ANT+.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_scan is not null)
        {
            return Task.CompletedTask;
        }

        Socket? query = null;
        try
        {
            query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            query.Bind(new IPEndPoint(IPAddress.Any, 0));
            query.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        }
        catch (SocketException)
        {
            query?.Dispose();
            return Task.CompletedTask;
        }
        catch
        {
            query?.Dispose();
            throw;
        }

        _scan = new CancellationTokenSource();
        var ct = _scan.Token;
        _sockets = [query];
        _tasks =
        [
            ReceiveLoopAsync(query, ct),
            QueryLoopAsync(query, ct),
            .. _manual.Select(m => CheckManualAsync(m, ct)),
        ];

        if (OpenListener() is { } listener)
        {
            _sockets.Add(listener);
            _tasks.Add(ReceiveLoopAsync(listener, ct));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _scan, null) is not { } scan)
        {
            return;
        }

        await scan.CancelAsync();
        try
        {
            await Task.WhenAll(_tasks);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        scan.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>The trainer an mDNS instance describes, once its address and port are known. Null until then.</summary>
    internal static DiscoveredDevice? Describe(string instance, int port, IPAddress address, IReadOnlyDictionary<string, string>? txt)
    {
        var name = instance.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase)
            ? instance[..^(ServiceType.Length + 1)]
            : instance;
        var services = txt?.GetValueOrDefault("ble-service-uuids")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeUuid)
            .ToList();
        if (BleScanner.Classify(name, services, null) != DeviceKind.Trainer)
        {
            return null;
        }

        // The MAC address stays put when the router hands out a new IP address.
        var id = txt?.GetValueOrDefault("mac-address") is { Length: > 0 } mac
            ? mac.Replace('-', ':').ToUpperInvariant()
            : $"{address}:{port}";
        return new DiscoveredDevice(new DirconPeripheral(address.ToString(), port), DeviceId.Wifi(id), name, DeviceKind.Trainer, null);
    }

    internal static (string Host, int Port) SplitHostPort(string hostAndPort)
    {
        var text = hostAndPort.Trim();
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? (text[..colon], port)
            : (text, DirconPeripheral.DefaultPort);
    }

    /// <summary>"0x1826", "1826" or a full UUID, as <see cref="GattUuids"/> spells it.</summary>
    private static string NormalizeUuid(string uuid) =>
        GattUuids.Normalize(uuid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uuid[2..] : uuid);

    /// <summary>A socket on port 5353 in the mDNS group, or null when the system will not share the port.</summary>
    private static Socket? OpenListener()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            var joined = false;
            foreach (var address in LocalAddresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsGroup, address));
                    joined = true;
                }
                catch (SocketException)
                {
                    // An interface that will not join still leaves the others.
                }
            }

            if (joined)
            {
                return socket;
            }
        }
        catch (SocketException)
        {
        }

        socket.Dispose();
        return null;
    }

    /// <summary>The IPv4 address of each network interface that is up and can multicast.</summary>
    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork),
            ];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private async Task QueryLoopAsync(Socket socket, CancellationToken ct)
    {
        for (var round = 0; !ct.IsCancellationRequested; round++)
        {
            var packet = Mdns.BuildQuery(Questions());
            var addresses = LocalAddresses();
            foreach (var address in addresses)
            {
                try
                {
                    // Out of each interface, so a trainer on Wi-Fi is asked even when Ethernet has the default route.
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(MdnsGroup, MdnsPort), ct);
                }
                catch (SocketException)
                {
                    // An interface that is going down, or a network with no route for multicast.
                }
            }

            if (addresses.Count == 0)
            {
                try
                {
                    await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(MdnsGroup, MdnsPort), ct);
                }
                catch (SocketException)
                {
                }
            }

            await Task.Delay(round < QueryDelays.Length ? QueryDelays[round] : QueryEvery, ct);
        }
    }

    /// <summary>The service type, plus what is still missing for the instances heard so far.</summary>
    private List<(string, ushort)> Questions()
    {
        var questions = new List<(string, ushort)> { (ServiceType, Mdns.Ptr) };
        lock (_lock)
        {
            foreach (var instance in _instances.Where(i => !_services.ContainsKey(i)))
            {
                questions.Add((instance, Mdns.Srv));
                questions.Add((instance, Mdns.TxtType));
            }

            foreach (var (host, _) in _services.Values.Where(s => !_hosts.ContainsKey(s.Host)))
            {
                questions.Add((host, Mdns.A));
            }
        }

        return questions;
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (SocketException)
            {
                // Windows reports an earlier send's ICMP "port unreachable" on the next receive.
                continue;
            }

            OnRecords(Mdns.Parse(buffer.AsSpan(0, result.ReceivedBytes)));
        }
    }

    private void OnRecords(List<MdnsRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var found = new List<DiscoveredDevice>();
        lock (_lock)
        {
            foreach (var record in records)
            {
                switch (record.Type)
                {
                    case Mdns.Ptr when string.Equals(record.Name, ServiceType, StringComparison.OrdinalIgnoreCase) && record.Target is { } instance:
                        _instances.Add(instance);
                        break;
                    case Mdns.Srv when record.Target is { } host:
                        _services[record.Name] = (host, record.Port);
                        break;
                    case Mdns.TxtType:
                        _txt[record.Name] = record.Txt;
                        break;
                    case Mdns.A when record.Address is { } address:
                        _hosts[record.Name] = address;
                        break;
                }
            }

            foreach (var instance in _instances)
            {
                if (!_services.TryGetValue(instance, out var service) || !_hosts.TryGetValue(service.Host, out var address))
                {
                    continue;
                }

                if (Describe(instance, service.Port, address, _txt.GetValueOrDefault(instance)) is not { } device)
                {
                    continue;
                }

                // Keep the same link while the trainer stays at the same place, so a connection made from it stays valid.
                if (_found.TryGetValue(device.Id, out var known) && known.Link is DirconPeripheral p
                    && p.Host == address.ToString() && p.Port == service.Port && known.Name == device.Name)
                {
                    continue;
                }

                _found[device.Id] = device;
                found.Add(device);
            }
        }

        foreach (var device in found)
        {
            DeviceFound?.Invoke(device);
        }
    }

    /// <summary>Reports a typed-in trainer once something answers on its port.</summary>
    private async Task CheckManualAsync(string hostAndPort, CancellationToken ct)
    {
        var device = Manual(hostAndPort);
        var peripheral = (DirconPeripheral)device.Link;
        while (!ct.IsCancellationRequested)
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ManualTimeout);
            try
            {
                await client.ConnectAsync(peripheral.Host, peripheral.Port, timeout.Token);
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                await Task.Delay(QueryEvery, ct);
                continue;
            }

            lock (_lock)
            {
                _found[device.Id] = device;
            }

            DeviceFound?.Invoke(device);
            return;
        }
    }
}
