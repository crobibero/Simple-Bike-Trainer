using System.Text.Json.Nodes;
using SimpleBikeTrainer.Ble;

namespace SimpleBikeTrainer.Tests;

public sealed class DeviceIdTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-device-id").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AddressesMatchIgnoringCaseButTransportsMustMatch()
    {
        Assert.Equal(DeviceId.Bluetooth("ce:d1:96:5e:7a:bb"), DeviceId.Bluetooth("CE:D1:96:5E:7A:BB"));
        Assert.Equal(DeviceId.Bluetooth("ce:d1:96:5e:7a:bb").GetHashCode(), DeviceId.Bluetooth("CE:D1:96:5E:7A:BB").GetHashCode());
        Assert.NotEqual(DeviceId.Bluetooth("DC:06:75:B6:E8:20"), DeviceId.Wifi("DC:06:75:B6:E8:20"));
        Assert.True(DeviceId.AntPlus(12345) == new DeviceId(DeviceTransport.AntPlus, "12345"));
        Assert.Equal("Wi-Fi DC:06:75:B6:E8:20", DeviceId.Wifi("DC:06:75:B6:E8:20").ToString());
    }

    [Fact]
    public void ADeviceIsSavedAsItsFieldsNotAString()
    {
        var path = Path.Combine(_dir, "config.json");
        var config = AppConfig.Load(path);
        config.Devices.Pick(DeviceKind.Trainer, DeviceId.Wifi("DC:06:75:B6:E8:20"), "KICKR CORE 6107");
        config.Devices.Pick(DeviceKind.HeartRate, DeviceId.AntPlus(12345), "HRM-Dual");
        config.Devices.Rename(DeviceId.AntPlus(12345), "Chest strap");
        config.Save();

        var devices = JsonNode.Parse(File.ReadAllText(path))!["devices"]!;
        Assert.Equal(
            """[{"kind":"trainer","transport":"wifi","address":"DC:06:75:B6:E8:20","name":"KICKR CORE 6107","alias":""},"""
            + """{"kind":"heartRate","transport":"antPlus","address":"12345","name":"HRM-Dual","alias":"Chest strap"}]""",
            devices["saved"]!.ToJsonString());
        Assert.Null(devices["trainer"]);
        Assert.Null(devices["names"]);

        var loaded = AppConfig.Load(path).Devices;
        Assert.Equal(DeviceId.Wifi("DC:06:75:B6:E8:20"), loaded.Trainer);
        Assert.Equal("Chest strap", loaded.NameOf(DeviceId.AntPlus(12345)));
    }

    [Fact]
    public void PickingATrainerReplacesTheOldOneAndControllersAddUp()
    {
        var saved = new DevicesConfig();
        var core = DeviceId.Bluetooth("CE:D1:96:5E:7A:BB");
        saved.Pick(DeviceKind.Trainer, core, "KICKR CORE 6107");
        saved.Rename(core, "Garage");
        saved.Pick(DeviceKind.Shifter, DeviceId.Bluetooth("C8:A3:BD:AE:3B:B6"), "Zwift Click");
        saved.Pick(DeviceKind.Shifter, DeviceId.Bluetooth("C8:00:00:00:00:03"), "Zwift Click");

        // The same trainer over Wi-Fi is another device: it takes the trainer's place.
        saved.Pick(DeviceKind.Trainer, DeviceId.Wifi("DC:06:75:B6:E8:20"), "KICKR CORE 6107");
        Assert.Equal(DeviceId.Wifi("DC:06:75:B6:E8:20"), saved.Trainer);
        Assert.Null(saved.Find(core));
        Assert.Equal(2, saved.Shifters.Count);

        // Picking a saved device again keeps the rider's name for it.
        saved.Rename(DeviceId.Wifi("DC:06:75:B6:E8:20"), "Garage");
        saved.Pick(DeviceKind.Trainer, DeviceId.Wifi("dc:06:75:b6:e8:20"), "KICKR CORE 6107");
        Assert.Equal("Garage", saved.NameOf(saved.Trainer!));
        Assert.Equal(3, saved.Saved.Count);

        saved.Unpick(DeviceKind.Shifter);
        Assert.Empty(saved.Shifters);
    }
}
