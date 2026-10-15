using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Dircon;

namespace SimpleBikeTrainer.Tests;

public sealed class DirconTests
{
    [Fact]
    public void UuidsTravelInTheOrderTheyAreWritten()
    {
        var payload = DirconMessage.UuidPayload(GattUuids.FitnessMachineControlPoint, 0x05, 0xC8);

        Assert.Equal("00002AD900001000800000805F9B34FB05C8", Convert.ToHexString(payload));
        Assert.Equal(GattUuids.FitnessMachineControlPoint, DirconMessage.ReadUuid(payload));
    }

    [Fact]
    public void AMessageIsAHeaderThenItsPayload()
    {
        var bytes = new DirconMessage(DirconMessage.WriteCharacteristic, 7, 0, [1, 2, 3]).Encode();

        Assert.Equal("010407000003010203", Convert.ToHexString(bytes));
        Assert.Equal(3, DirconMessage.PayloadLength(bytes));
    }

    [Fact]
    public void CharacteristicDiscoveryListsEachCharacteristicWithItsProperties()
    {
        byte[] payload =
        [
            .. DirconMessage.UuidPayload(GattUuids.FitnessMachine),
            .. DirconMessage.UuidPayload(GattUuids.IndoorBikeData, DirconMessage.PropertyNotify),
            .. DirconMessage.UuidPayload(GattUuids.FitnessMachineControlPoint, DirconMessage.PropertyWrite | DirconMessage.PropertyNotify),
        ];

        Assert.Equal(
            [(GattUuids.IndoorBikeData, DirconMessage.PropertyNotify), (GattUuids.FitnessMachineControlPoint, (byte)6)],
            DirconMessage.ParseCharacteristics(payload));
    }

    [Fact]
    public async Task APeripheralReadsWritesAndHearsNotificationsOverTcp()
    {
        await using var trainer = new FakeTrainer();
        var peripheral = new DirconPeripheral("127.0.0.1", trainer.Port);
        await peripheral.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(await peripheral.HasServiceAsync(GattUuids.FitnessMachine));
        Assert.False(await peripheral.HasServiceAsync(GattUuids.HeartRate));
        Assert.Null(await peripheral.GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.SupportedPowerRange));

        var feature = await peripheral.GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineFeature);
        Assert.Equal(FakeTrainer.Features, await feature!.ReadAsync());

        var data = (await peripheral.GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.IndoorBikeData))!;
        Assert.True(data.CanNotify);
        var heard = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (await data.SubscribeAsync(heard.SetResult))
        {
            Assert.Equal(FakeTrainer.BikeData, await heard.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        var control = (await peripheral.GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineControlPoint))!;
        await control.WriteAsync([0x05, 0xC8, 0x00], withResponse: true);
        Assert.Equal([0x05, 0xC8, 0x00], trainer.Written.Last());

        await peripheral.DisconnectAsync();
    }

    [Fact]
    public async Task AnErrorResponseFailsTheRequest()
    {
        await using var trainer = new FakeTrainer();
        var peripheral = new DirconPeripheral("127.0.0.1", trainer.Port);
        await peripheral.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var feature = (await peripheral.GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineFeature))!;

        trainer.FailWrites = true;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => feature.WriteAsync([1], withResponse: true));
        Assert.Contains("write failed", e.Message, StringComparison.Ordinal);
        await peripheral.DisconnectAsync();
    }

    [Fact]
    public async Task ATrainerThatHangsUpRaisesDisconnected()
    {
        await using var trainer = new FakeTrainer();
        var peripheral = new DirconPeripheral("127.0.0.1", trainer.Port);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peripheral.Disconnected += disconnected.SetResult;
        await peripheral.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        trainer.HangUp();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisconnectingOnPurposeIsNotADrop()
    {
        await using var trainer = new FakeTrainer();
        var peripheral = new DirconPeripheral("127.0.0.1", trainer.Port);
        var drops = 0;
        peripheral.Disconnected += () => drops++;
        await peripheral.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await peripheral.DisconnectAsync();

        Assert.Equal(0, drops);
    }

    [Fact]
    public void AnMdnsResponseNamesTheTrainerItsAddressAndItsServices()
    {
        var records = Mdns.Parse(MdnsResponse());
        var instance = Assert.Single(records, r => r.Type == Mdns.Ptr).Target!;
        var srv = Assert.Single(records, r => r.Type == Mdns.Srv);
        var txt = Assert.Single(records, r => r.Type == Mdns.TxtType).Txt;
        var a = Assert.Single(records, r => r.Type == Mdns.A);

        Assert.Equal("KICKR CORE 1A2B." + DirconScanner.ServiceType, instance);
        Assert.Equal(36866, srv.Port);
        Assert.Equal("kickr-1a2b.local", srv.Target);
        Assert.Equal(IPAddress.Parse("192.168.1.40"), a.Address);

        var device = DirconScanner.Describe(instance, srv.Port, a.Address!, txt)!;
        Assert.Equal("KICKR CORE 1A2B", device.Name);
        Assert.Equal(DeviceId.Wifi("F8:01:02:03:04:05"), device.Id);
        Assert.Equal(DeviceKind.Trainer, device.Kind);
        var link = Assert.IsType<DirconPeripheral>(device.Link);
        Assert.Equal(("192.168.1.40", 36866), (link.Host, link.Port));
    }

    [Fact]
    public void ADeviceWithoutFtmsOrATrainerNameIsLeftOut()
    {
        var txt = new Dictionary<string, string> { ["ble-service-uuids"] = "0x180D" };

        Assert.Null(DirconScanner.Describe("TICKR 1234." + DirconScanner.ServiceType, 36866, IPAddress.Loopback, txt));
        Assert.NotNull(DirconScanner.Describe("Trainer." + DirconScanner.ServiceType, 36866, IPAddress.Loopback, new Dictionary<string, string> { ["ble-service-uuids"] = "0x1826,0x1818" }));
    }

    [Fact]
    public void AQueryAsksForEachNameAndType()
    {
        var query = Mdns.BuildQuery([(DirconScanner.ServiceType, Mdns.Ptr)]);

        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal("\u0012_wahoo-fitness-tnp\u0004_tcp\u0005local\0", Encoding.ASCII.GetString(query, 12, query.Length - 16));
        Assert.Equal([0, 12, 0, 1], query[^4..]);
    }

    [Theory]
    [InlineData("192.168.1.40", "192.168.1.40", 36866)]
    [InlineData("kickr.local:4000", "kickr.local", 4000)]
    [InlineData(" 10.0.0.2:36866 ", "10.0.0.2", 36866)]
    public void ATypedAddressMayLeaveOutThePort(string text, string host, int port) =>
        Assert.Equal((host, port), DirconScanner.SplitHostPort(text));

    /// <summary>What a KICKR answers to a PTR query: the PTR, then SRV, TXT and A records, with compressed names.</summary>
    private static byte[] MdnsResponse()
    {
        var w = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 3 };
        void Name(params string[] labels)
        {
            foreach (var label in labels)
            {
                w.Add((byte)label.Length);
                w.AddRange(Encoding.UTF8.GetBytes(label));
            }

            w.Add(0);
        }

        void Header(ushort type, int length) => w.AddRange([(byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0x11, 0x94, (byte)(length >> 8), (byte)length]);

        // PTR: _wahoo-fitness-tnp._tcp.local -> KICKR CORE 1A2B.<pointer to the service type>
        var serviceAt = w.Count;
        Name("_wahoo-fitness-tnp", "_tcp", "local");
        Header(Mdns.Ptr, 16 + 2);
        var instanceAt = w.Count;
        w.Add(15);
        w.AddRange(Encoding.UTF8.GetBytes("KICKR CORE 1A2B"));
        w.AddRange([0xC0, (byte)serviceAt]);

        // SRV: port 36866 on kickr-1a2b.local
        w.AddRange([0xC0, (byte)instanceAt]);
        Header(Mdns.Srv, 6 + 18);
        w.AddRange([0, 0, 0, 0, 0x90, 0x02]);
        var hostAt = w.Count;
        Name("kickr-1a2b", "local");

        // TXT
        w.AddRange([0xC0, (byte)instanceAt]);
        string[] entries = ["ble-service-uuids=0x1826,0x1818", "mac-address=F8-01-02-03-04-05", "serial-number=123"];
        Header(Mdns.TxtType, entries.Sum(e => e.Length + 1));
        foreach (var entry in entries)
        {
            w.Add((byte)entry.Length);
            w.AddRange(Encoding.UTF8.GetBytes(entry));
        }

        // A
        w.AddRange([0xC0, (byte)hostAt]);
        Header(Mdns.A, 4);
        w.AddRange([192, 168, 1, 40]);
        return [.. w];
    }

    /// <summary>A Direct Connect server with the FTMS service, on a loopback port.</summary>
    private sealed class FakeTrainer : IAsyncDisposable
    {
        public static readonly byte[] Features = [0x02, 0x40, 0, 0, 0x0C, 0x20, 0, 0];
        public static readonly byte[] BikeData = [0x44, 0x00, 0x10, 0x0E, 0xB4, 0x00, 0xC8, 0x00];

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serving;
        private TcpClient? _client;

        public FakeTrainer()
        {
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<byte[]> Written { get; } = [];

        public bool FailWrites { get; set; }

        public void HangUp() => _client?.Dispose();

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _client?.Dispose();
            try
            {
                await _serving;
            }
            catch
            {
            }

            _cts.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                _client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = _client.GetStream();
                var header = new byte[DirconMessage.HeaderLength];
                try
                {
                    while (true)
                    {
                        await stream.ReadExactlyAsync(header, _cts.Token);
                        var payload = new byte[DirconMessage.PayloadLength(header)];
                        await stream.ReadExactlyAsync(payload, _cts.Token);
                        var request = DirconMessage.Decode(header, payload);
                        var (code, answer) = Answer(request);
                        await stream.WriteAsync(new DirconMessage(request.Type, request.Sequence, code, answer).Encode(), _cts.Token);
                        if (request.Type == DirconMessage.EnableNotifications && payload[^1] == 1)
                        {
                            var notification = DirconMessage.UuidPayload(GattUuids.IndoorBikeData, BikeData);
                            await stream.WriteAsync(new DirconMessage(DirconMessage.Notification, 0, 0, notification).Encode(), _cts.Token);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
                {
                }
            }
        }

        private (byte Code, byte[] Payload) Answer(DirconMessage request)
        {
            switch (request.Type)
            {
                case DirconMessage.DiscoverServices:
                    return (0, DirconMessage.UuidPayload(GattUuids.FitnessMachine));
                case DirconMessage.DiscoverCharacteristics:
                    return (0,
                    [
                        .. request.Payload,
                        .. DirconMessage.UuidPayload(GattUuids.FitnessMachineFeature, DirconMessage.PropertyRead),
                        .. DirconMessage.UuidPayload(GattUuids.IndoorBikeData, DirconMessage.PropertyNotify),
                        .. DirconMessage.UuidPayload(GattUuids.FitnessMachineControlPoint, DirconMessage.PropertyWrite | DirconMessage.PropertyNotify),
                    ]);
                case DirconMessage.ReadCharacteristic:
                    return (0, [.. request.Payload, .. Features]);
                case DirconMessage.WriteCharacteristic when FailWrites:
                    return (0x06, request.Payload[..DirconMessage.UuidLength]);
                case DirconMessage.WriteCharacteristic:
                    lock (Written)
                    {
                        Written.Add(request.Payload[DirconMessage.UuidLength..]);
                    }

                    return (0, request.Payload[..DirconMessage.UuidLength]);
                default:
                    return (0, request.Payload);
            }
        }
    }
}
