using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Tests;

public class FtmsTests
{
    [Fact]
    public void ParsesSpeedCadenceAndPower()
    {
        // Flags 0x0044: speed (bit 0 clear), cadence, power. 30.00 km/h, 85 rpm (170 half-rpm), 200 W.
        byte[] packet = [0x44, 0x00, 0xB8, 0x0B, 0xAA, 0x00, 0xC8, 0x00];

        var data = Ftms.ParseIndoorBikeData(packet);

        Assert.Equal(30.0, data.SpeedKmh);
        Assert.Equal(85.0, data.CadenceRpm);
        Assert.Equal(200, data.PowerWatts);
        Assert.Null(data.HeartRate);
    }

    [Fact]
    public void SkipsFieldsItDoesNotUse()
    {
        // Flags: average speed, cadence, distance, resistance, power, heart rate.
        // 0x0002 | 0x0004 | 0x0010 | 0x0020 | 0x0040 | 0x0200 = 0x0276
        byte[] packet =
        [
            0x76, 0x02,
            0xE8, 0x03, // speed 10.00 km/h
            0x00, 0x00, // average speed
            0x64, 0x00, // cadence 50 rpm
            0x10, 0x27, 0x00, // distance 10000 m
            0x05, 0x00, // resistance 5
            0x2C, 0x01, // power 300 W
            0x8C, // heart rate 140
        ];

        var data = Ftms.ParseIndoorBikeData(packet);

        Assert.Equal(10.0, data.SpeedKmh);
        Assert.Equal(50.0, data.CadenceRpm);
        Assert.Equal(10000, data.TotalDistanceMeters);
        Assert.Equal(5, data.ResistanceLevel);
        Assert.Equal(300, data.PowerWatts);
        Assert.Equal(140, data.HeartRate);
    }

    [Fact]
    public void TruncatedPacketGivesNoValues()
    {
        byte[] packet = [0x44, 0x00, 0xB8, 0x0B, 0xAA];

        var data = Ftms.ParseIndoorBikeData(packet);

        Assert.Null(data.PowerWatts);
        Assert.Null(data.CadenceRpm);
    }

    [Fact]
    public void EncodesCommands()
    {
        Assert.Equal([0x05, 0xC8, 0x00], Ftms.SetTargetPower(200));
        Assert.Equal([0x05, 0x00, 0x00], Ftms.SetTargetPower(-5));
        Assert.Equal([0x00], Ftms.RequestControl());
        Assert.Equal([0x07], Ftms.Start());

        // 2.5% grade = 250 (0x00FA), Crr 0.004 = 40, Cw 0.51 = 51.
        Assert.Equal([0x11, 0x00, 0x00, 0xFA, 0x00, 40, 51], Ftms.SetSimulation(2.5, 0.004, 0.51));
    }

    [Fact]
    public void ParsesControlPointResponses()
    {
        var ok = Ftms.ParseControlPointResponse([0x80, 0x05, 0x01]);
        Assert.Equal(new ControlPointResponse(0x05, 0x01), ok);
        Assert.True(ok!.Value.Succeeded);

        var denied = Ftms.ParseControlPointResponse([0x80, 0x05, 0x05]);
        Assert.Equal("control not permitted", denied!.Value.ResultName);

        Assert.Null(Ftms.ParseControlPointResponse([0x05, 0x01]));
    }
}

public class HeartRateParserTests
{
    [Fact]
    public void ReadsEightAndSixteenBitValues()
    {
        Assert.Equal(72, HeartRateParser.Parse([0x00, 72]));
        Assert.Equal(300, HeartRateParser.Parse([0x01, 0x2C, 0x01]));
        Assert.Null(HeartRateParser.Parse([0x00]));
    }
}

public class ZwiftClickDecoderTests
{
    [Fact]
    public void ClickV1PlusAndMinus()
    {
        var decoder = new ZwiftClickDecoder();

        // Field 1 (plus) = 0 means pressed; field 2 (minus) = 1 released.
        Assert.Equal(ClickButtons.Plus, decoder.Decode([0x37, 0x08, 0x00, 0x10, 0x01]));

        // Still held: no new press.
        Assert.Equal(ClickButtons.None, decoder.Decode([0x37, 0x08, 0x00, 0x10, 0x01]));

        // Released, then minus.
        Assert.Equal(ClickButtons.None, decoder.Decode([0x37, 0x08, 0x01, 0x10, 0x01]));
        Assert.Equal(ClickButtons.Minus, decoder.Decode([0x37, 0x08, 0x01, 0x10, 0x00]));
    }

    [Fact]
    public void RideBitmapIsActiveLow()
    {
        var decoder = new ZwiftClickDecoder();

        // The released frame: every bit set.
        Assert.Equal(ClickButtons.None, decoder.Decode([0x23, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]));

        // Click V2 right puck "+" is SHFT_UP_R (0x1000).
        Assert.Equal(ClickButtons.Plus, decoder.Decode(RideFrame(0xFFFFFFFF & ~0x1000u)));
        Assert.Equal(ClickButtons.None, decoder.Decode(RideFrame(0xFFFFFFFF & ~0x1000u)));

        // Let go of "+" and press B (0x20) and the left puck's "-" (SHFT_UP_L, 0x100) together.
        Assert.Equal(ClickButtons.B | ClickButtons.Minus, decoder.Decode(RideFrame(0xFFFFFFFF & ~0x120u)));
    }

    [Fact]
    public void SkipsOtherFieldsBeforeTheBitmap()
    {
        var decoder = new ZwiftClickDecoder();

        // An analog paddle (field 3, length-delimited) ahead of the bitmap must not confuse the parser.
        byte[] frame = [0x23, 0x1A, 0x04, 0x08, 0x00, 0x10, 0x00, .. RideFrame(0xFFFFFFFF & ~0x1000u)[1..]];

        Assert.Equal(ClickButtons.Plus, decoder.Decode(frame));
    }

    [Fact]
    public void ReadsBatteryAndIgnoresHandshake()
    {
        var decoder = new ZwiftClickDecoder();

        Assert.Equal(ClickButtons.None, decoder.Decode([.. "RideOn"u8, 0x02, 0x03]));
        Assert.Equal(ClickButtons.None, decoder.Decode([0x19, 0x08, 85]));
        Assert.Equal(85, decoder.Battery);
        Assert.Equal(ClickButtons.None, decoder.Decode([0x15]));
    }

    private static byte[] RideFrame(uint map)
    {
        var bytes = new List<byte> { 0x23, 0x08 };
        ulong v = map;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            bytes.Add(v != 0 ? (byte)(b | 0x80) : b);
        }
        while (v != 0);

        return [.. bytes];
    }
}
