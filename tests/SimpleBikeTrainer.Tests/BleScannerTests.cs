using SimpleBikeTrainer.Ble;

namespace SimpleBikeTrainer.Tests;

public sealed class BleScannerTests
{
    [Fact]
    public async Task AdvertisementsThatSplitANameFromItsServicesAreMerged()
    {
        var adapter = new FakeAdapter();
        await using var scanner = new BleScanner(adapter);
        var peripheral = new FakePeripheral();

        // Windows and macOS: the name in the scan response, the service in the advertisement.
        adapter.Advertise(new BleAdvertisement(peripheral, "E1:22:33:44:55:66", "HRM-Pro:123456", [], new Dictionary<ushort, byte[]>(), -70));
        Assert.Empty(scanner.Found);
        adapter.Advertise(new BleAdvertisement(peripheral, "E1:22:33:44:55:66", null, [GattUuids.HeartRate], new Dictionary<ushort, byte[]>(), -68));

        var strap = Assert.Single(scanner.Found);
        Assert.Equal(DeviceKind.HeartRate, strap.Kind);
        Assert.Equal("HRM-Pro:123456", strap.Name);
        Assert.Equal((short)-68, strap.Rssi);
        Assert.Same(peripheral, strap.Link);
    }

    [Fact]
    public void ZwiftControllersAreKnownByTheirManufacturerData()
    {
        var click = new Dictionary<ushort, byte[]> { [GattUuids.ZwiftManufacturerId] = [0x09, 0x01] };
        var other = new Dictionary<ushort, byte[]> { [GattUuids.ZwiftManufacturerId] = [0x20] };

        Assert.Equal(DeviceKind.Shifter, BleScanner.Classify(null, null, click));
        Assert.Equal(DeviceKind.Other, BleScanner.Classify(null, null, other));
    }

    [Theory]
    [InlineData("180D", "0000180d-0000-1000-8000-00805f9b34fb")]
    [InlineData("0000FC82", "0000fc82-0000-1000-8000-00805f9b34fb")]
    [InlineData("00000001-19CA-4651-86E5-FA29DCDD09D1", "00000001-19ca-4651-86e5-fa29dcdd09d1")]
    public void CoreBluetoothUuidsAreSpelledAsBlueZSpellsThem(string uuid, string expected) =>
        Assert.Equal(expected, GattUuids.Normalize(uuid));

    private sealed class FakeAdapter : BleAdapter
    {
        public void Advertise(BleAdvertisement ad) => OnAdvertised(ad);

        public override Task StartScanAsync() => Task.CompletedTask;

        public override Task StopScanAsync() => Task.CompletedTask;
    }

    private sealed class FakePeripheral : BlePeripheral
    {
        public override Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task<bool> HasServiceAsync(string service) => Task.FromResult(false);

        public override Task<BleCharacteristic?> GetCharacteristicAsync(string service, string characteristic) => Task.FromResult<BleCharacteristic?>(null);
    }
}
