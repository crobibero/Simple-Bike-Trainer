using System.Collections.Concurrent;
using SimpleBikeTrainer.AntPlus;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Tests;

/// <summary>ANT+ against a fake stick that answers the way the ANT Message Protocol says a real one does.</summary>
public sealed class AntPlusTests : IDisposable
{
    private readonly List<FakeStick> _sticks = [];

    public AntPlusTests()
    {
        AntStick.RetryDelay = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
    {
        AntStick.OpenTransport = () => null;
        foreach (var stick in _sticks)
        {
            stick.Dispose();
        }
    }

    [Fact]
    public void MessagesAreFoundAcrossReadsAndAfterJunk()
    {
        var message = AntMessage.Encode(AntMessage.BroadcastData, [0, 1, 2, 3, 4, 5, 6, 7, 8]);
        var corrupt = AntMessage.Encode(AntMessage.ChannelEvent, [0, 1, 2]);
        corrupt[^1] ^= 0xFF;
        var reader = new AntFrameReader();

        Assert.Empty(reader.Push([0x00, 0x13, .. corrupt, .. message.AsSpan(0, 5)]));
        var frames = reader.Push(message.AsSpan(5));

        var frame = Assert.Single(frames);
        Assert.Equal(AntMessage.BroadcastData, frame.Id);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Data);
    }

    [Fact]
    public void ExtendedDataSaysWhoSentItAndHowStrong()
    {
        // Device number 0x3039 with 1 in the transmission type's upper nibble: 0x13039 = 77881.
        var received = AntReceived.Parse([2, 0x19, 0, 90, 0, 0, 0xC8, 0x00, 0, 0xC0, 0x39, 0x30, 17, 0x15, 0x20, unchecked((byte)-61), 0x80]);

        Assert.NotNull(received);
        Assert.Equal(77881, received.Value.DeviceNumber);
        Assert.Equal(17, received.Value.DeviceType);
        Assert.Equal((sbyte)-61, received.Value.Rssi);
        Assert.Equal(0x19, received.Value.Page[0]);
    }

    [Fact]
    public void AStickThatCannotMeasureSignalGivesNoRssi()
    {
        // Heard from a heart rate strap through an ANTUSB2: the RSSI block is 0x10 0x00 0x68, not a dBm reading.
        var received = AntReceived.Parse([0, 0x04, 0x21, 0xAD, 0xF8, 0xB3, 0xF9, 0x5B, 0x00, 0xC0, 0x5C, 0xAB, 0x78, 0x01, 0x10, 0x00, 0x68, 0x00]);

        Assert.NotNull(received);
        Assert.Equal(43868, received.Value.DeviceNumber);
        Assert.Equal(0x78, received.Value.DeviceType);
        Assert.Null(received.Value.Rssi);
    }

    [Fact]
    public void TrainerPagesBecomeOneReading()
    {
        var decoder = new FecDecoder();

        // General page: 250 m, 8.333 m/s, 142 bpm, distance counted.
        Assert.Null(decoder.Decode([0x10, 25, 0, 250, 0x8D, 0x20, 142, 0x24]));
        var data = decoder.Decode([0x19, 1, 91, 0, 0, 0x2C, 0x31, 0x30]);

        Assert.NotNull(data);
        Assert.Equal(300, data.Value.PowerWatts);
        Assert.Equal(91, data.Value.CadenceRpm);
        Assert.Equal(30.0, data.Value.SpeedKmh!.Value, 1);
        Assert.Equal(142, data.Value.HeartRate);
        Assert.Equal(0, data.Value.TotalDistanceMeters);

        // The distance byte rolls over at 256; invalid power and cadence are null.
        decoder.Decode([0x10, 25, 0, 10, 0x8D, 0x20, 0xFF, 0x24]);
        data = decoder.Decode([0x19, 2, 0xFF, 0, 0, 0xFF, 0x0F, 0x30]);
        Assert.Equal(16, data!.Value.TotalDistanceMeters);
        Assert.Null(data.Value.PowerWatts);
        Assert.Null(data.Value.CadenceRpm);
        Assert.Null(data.Value.HeartRate);
    }

    [Fact]
    public void TargetPowerAndResistanceAreEncodedInTheirUnits()
    {
        Assert.Equal(new byte[] { 0x31, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xE8, 0x03 }, AntPlusPages.TargetPower(250));
        Assert.Equal(new byte[] { 0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 }, AntPlusPages.BasicResistance(0));
        Assert.Equal(0, AntPlusPages.TargetPower(-5)[6]);
    }

    [Fact]
    public void HeartRatePagesGiveBpmAndBattery()
    {
        Assert.Equal(128, AntPlusPages.HeartRate([0x84, 0xFF, 0, 0, 0, 0, 7, 128]));
        Assert.Null(AntPlusPages.HeartRate([0x04, 0xFF, 0, 0, 0, 0, 7, 0]));
        Assert.Equal(64, AntPlusPages.HeartRateBattery([0x87, 64, 0, 0, 0, 0, 7, 128]));
        Assert.Null(AntPlusPages.HeartRateBattery([0x07, 0xFF, 0, 0, 0, 0, 7, 128]));
    }

    [Fact]
    public async Task AScanListsTrainersAndStrapsAndLeavesOtherDevicesOut()
    {
        var stick = Plug();
        await using (var scanner = await AntScanner.CreateAsync(TestContext.Current.CancellationToken))
        {
            Assert.NotNull(scanner);
            await scanner.StartAsync(TestContext.Current.CancellationToken);
            Assert.Contains(stick.Sent, f => f.Id == AntMessage.AssignChannel && f.Data is [0, 0x00, 0, 0x01]);

            stick.Broadcast(0, [0x19, 0, 0, 0, 0, 0, 0, 0], number: 12345, type: 17, rssi: -55);
            stick.Broadcast(0, [0x04, 0, 0, 0, 0, 0, 0, 120], number: 777, type: 120 | 0x80);
            stick.Broadcast(0, [0x10, 0, 0, 0, 0, 0, 0, 0], number: 4242, type: 11);
            await Until(() => scanner.Found.Count == 2);

            var trainer = Assert.Single(scanner.Found, d => d.Kind == DeviceKind.Trainer);
            Assert.Equal(DeviceId.AntPlus(12345), trainer.Id);
            Assert.Equal("ANT+ 12345", trainer.Id.ToString());
            Assert.Equal((short)-55, trainer.Rssi);
            Assert.Equal(DeviceId.AntPlus(777), Assert.Single(scanner.Found, d => d.Kind == DeviceKind.HeartRate).Id);
        }

        // The last user resets the stick and lets it go.
        Assert.Equal(AntMessage.ResetSystem, stick.Sent[^1].Id);
        Assert.True(stick.Disposed);
    }

    [Fact]
    public async Task ATrainerConnectsOnItsDataHoldsTargetsAndLetsGoOfErg()
    {
        var stick = Plug();
        stick.Stream = [0x19, 1, 90, 0, 0, 0x96, 0x30, 0x30];
        var trainer = (AntFecTrainer)new AntDeviceLink(12345, 17, 5).Open(Found(DeviceKind.Trainer));
        var readings = new ConcurrentQueue<IndoorBikeData>();
        trainer.DataReceived += readings.Enqueue;

        await trainer.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.True(trainer.IsConnected);
        Assert.Contains(stick.Sent, f => f.Id == AntMessage.ChannelId && f.Data is [0, 0x39, 0x30, 17, 5]);
        Assert.Contains(stick.Sent, f => f.Id == AntMessage.ChannelPeriod && f.Data is [0, 0x00, 0x20]);
        await Until(() => !readings.IsEmpty);
        Assert.Equal(150, readings.First().PowerWatts);

        await trainer.SetTargetPowerAsync(200, TestContext.Current.CancellationToken);
        Assert.Null(trainer.LastError);
        Assert.Contains(stick.Sent, f => f.Id == AntMessage.AcknowledgedData && f.Data.AsSpan(1).SequenceEqual(AntPlusPages.TargetPower(200)));

        stick.AcknowledgeData = false;
        await trainer.SetTargetPowerAsync(210, TestContext.Current.CancellationToken);
        Assert.NotNull(trainer.LastError);
        stick.AcknowledgeData = true;

        await trainer.DisposeAsync();
        Assert.Contains(stick.Sent, f => f.Id == AntMessage.AcknowledgedData && f.Data.AsSpan(1).SequenceEqual(AntPlusPages.BasicResistance(0)));
        Assert.Contains(stick.Sent, f => f.Id == AntMessage.UnassignChannel);
        Assert.True(stick.Disposed);
    }

    [Fact]
    public async Task ADeviceThatSendsNothingDoesNotConnect()
    {
        var stick = Plug();
        var strap = new AntDeviceLink(777, 120, 1).Open(Found(DeviceKind.HeartRate));

        // The connect waits 20 s for data; cancelling stands in for the wait running out.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => strap.ConnectAsync(cancel.Token));

        Assert.True(stick.Disposed);
    }

    [Fact]
    public async Task AnUnpluggedStickComesBackWithItsChannelsAndTheTrainersTarget()
    {
        var first = Plug();
        first.Stream = [0x19, 1, 90, 0, 0, 0x96, 0x30, 0x30];
        await using var trainer = (AntFecTrainer)new AntDeviceLink(12345, 17, 5).Open(Found(DeviceKind.Trainer));
        await trainer.ConnectAsync(TestContext.Current.CancellationToken);
        await trainer.SetTargetPowerAsync(180, TestContext.Current.CancellationToken);

        AntStick.OpenTransport = () => null;
        first.Unplug();
        await Until(() => !trainer.IsConnected && trainer.Status is not null);

        var second = Plug();
        second.Stream = first.Stream;
        await Until(() => trainer.IsConnected);
        Assert.Contains(second.Sent, f => f.Id == AntMessage.OpenChannel);
        await Until(() => second.Sent.Any(f => f.Id == AntMessage.AcknowledgedData && f.Data.AsSpan(1).SequenceEqual(AntPlusPages.TargetPower(180))));
    }

    private static DiscoveredDevice Found(DeviceKind kind) => new(null!, DeviceId.AntPlus(1), "ANT+ device", kind, null);

    private FakeStick Plug()
    {
        var stick = new FakeStick();
        _sticks.Add(stick);
        AntStick.OpenTransport = () => stick;
        return stick;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Answers commands with "no error", and once a device channel is open, streams a page on it.</summary>
    private sealed class FakeStick : IAntTransport
    {
        private readonly BlockingCollection<byte[]> _toHost = [];
        private readonly ConcurrentDictionary<byte, bool> _open = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<AntFrame> _sent = [];
        private volatile bool _unplugged;

        public FakeStick()
        {
            Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(50);
                    if (Stream is { } page)
                    {
                        foreach (var channel in _open.Keys)
                        {
                            Broadcast(channel, page);
                        }
                    }
                }
            });
        }

        /// <summary>A page every device channel sends once open.</summary>
        public byte[]? Stream { get; set; }

        public bool AcknowledgeData { get; set; } = true;

        public bool Disposed { get; private set; }

        public List<AntFrame> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public void Unplug() => _unplugged = true;

        public void Broadcast(byte channel, byte[] page, int? number = null, byte type = 0, sbyte? rssi = null)
        {
            byte[] data = number is { } n
                ? [channel, .. page, (byte)(0x80 | (rssi is null ? 0 : 0x40)), (byte)n, (byte)(n >> 8), type, 1, .. rssi is { } r ? new byte[] { 0x20, (byte)r, 0x80 } : []]
                : [channel, .. page];
            _toHost.Add(AntMessage.Encode(AntMessage.BroadcastData, data));
        }

        public void Write(byte[] message)
        {
            if (_unplugged)
            {
                throw new AntException("unplugged");
            }

            var frame = new AntFrame(message[2], message[3..^1]);
            lock (_sent)
            {
                _sent.Add(frame);
            }

            var d = frame.Data;
            switch (frame.Id)
            {
                case AntMessage.ResetSystem:
                    _open.Clear();
                    Reply(AntMessage.StartupMessage, [0x20]);
                    break;
                case AntMessage.RequestMessage when d[1] == AntMessage.Capabilities:
                    Reply(AntMessage.Capabilities, [8, 3, 0, 0, 0, 0]);
                    break;
                case AntMessage.AcknowledgedData:
                    Reply(AntMessage.ChannelEvent, [d[0], AntMessage.EventMessageId, AcknowledgeData ? AntMessage.EventTransferTxCompleted : AntMessage.EventTransferTxFailed]);
                    break;
                case AntMessage.OpenChannel:
                    Reply(AntMessage.ChannelEvent, [d[0], frame.Id, 0]);
                    _open[d[0]] = true;
                    break;
                case AntMessage.CloseChannel:
                    _open.TryRemove(d[0], out _);
                    Reply(AntMessage.ChannelEvent, [d[0], frame.Id, 0]);
                    Reply(AntMessage.ChannelEvent, [d[0], AntMessage.EventMessageId, AntMessage.EventChannelClosed]);
                    break;
                default:
                    Reply(AntMessage.ChannelEvent, [d[0], frame.Id, 0]);
                    break;
            }
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            if (_unplugged)
            {
                throw new AntException("unplugged");
            }

            if (!_toHost.TryTake(out var message, timeoutMs))
            {
                return 0;
            }

            message.CopyTo(buffer, 0);
            return message.Length;
        }

        public void Dispose()
        {
            Disposed = true;
            _stop.Cancel();
        }

        private void Reply(byte id, byte[] data) => _toHost.Add(AntMessage.Encode(id, data));
    }
}
