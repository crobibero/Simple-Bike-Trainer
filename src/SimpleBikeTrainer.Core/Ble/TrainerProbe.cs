using System.Buffers.Binary;
using Linux.Bluetooth;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

/// <summary>
/// A read-only trainer connection for <c>simple-bike-trainer-cli probe</c>: reads what the trainer supports and listens to its data,
/// without taking control, so it is safe while someone is riding.
/// </summary>
public sealed class TrainerProbe(Device device, string name, string address) : BleConnection(device, name, address)
{
    public event Action<IndoorBikeData>? DataReceived;

    public string Features { get; private set; } = CoreStrings.Probe_Unknown;

    public string PowerRange { get; private set; } = CoreStrings.Probe_Unknown;

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var feature = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineFeature);
        Features = DescribeFeatures(await feature.ReadValueAsync(new Dictionary<string, object>()));

        try
        {
            var range = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.SupportedPowerRange);
            var r = await range.ReadValueAsync(new Dictionary<string, object>());
            if (r.Length >= 6)
            {
                PowerRange = Loc.Format(CoreStrings.Probe_PowerRange, BinaryPrimitives.ReadInt16LittleEndian(r), BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(2)), BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(4)));
            }
        }
        catch (InvalidOperationException)
        {
            PowerRange = CoreStrings.Probe_NotReported;
        }

        var bikeData = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.IndoorBikeData);
        await SubscribeAsync(bikeData, bytes => DataReceived?.Invoke(Ftms.ParseIndoorBikeData(bytes)));
    }

    internal static string DescribeFeatures(byte[] value)
    {
        if (value.Length < 8)
        {
            return Loc.Format(CoreStrings.Probe_Unreadable, Convert.ToHexString(value));
        }

        var machine = BinaryPrimitives.ReadUInt32LittleEndian(value);
        var target = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4));
        var names = new List<string>();
        if ((machine & (1 << 1)) != 0)
        {
            names.Add(CoreStrings.Feature_Cadence);
        }

        if ((machine & (1 << 14)) != 0)
        {
            names.Add(CoreStrings.Feature_Power);
        }

        if ((target & (1 << 3)) != 0)
        {
            names.Add(CoreStrings.Feature_ErgTarget);
        }

        if ((target & (1 << 2)) != 0)
        {
            names.Add(CoreStrings.Feature_Resistance);
        }

        if ((target & (1 << 13)) != 0)
        {
            names.Add(CoreStrings.Feature_Simulation);
        }

        if ((target & (1 << 15)) != 0)
        {
            names.Add(CoreStrings.Feature_SpinDown);
        }

        return string.Join(", ", names);
    }
}
