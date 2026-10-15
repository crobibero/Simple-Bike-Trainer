using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using SimpleBikeTrainer.AntPlus;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Android asks the user before the app may open the stick: on plug-in with the app's USB filter, else at the first scan.</summary>
internal sealed class AndroidUsbTransport : IAntTransport
{
    private const int Dynastream = 0x0FCF;

    // ANTUSB2 and ANTUSB-m, as Resources/xml/ant_usb.xml lists them.
    private static readonly int[] Products = [0x1008, 0x1009];

    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromSeconds(60);
    private static readonly string PermissionAction = $"{Application.Context.PackageName}.USB_PERMISSION";

    // A stick the user said no to is not asked about again until it is plugged in again.
    private static readonly HashSet<string> Refused = [];

    private readonly UsbManager _manager;
    private readonly UsbDevice _device;
    private readonly UsbInterface _interface;
    private readonly UsbEndpoint _in;
    private readonly UsbEndpoint _out;
    private UsbDeviceConnection? _connection;

    private AndroidUsbTransport(UsbManager manager, UsbDevice device, UsbDeviceConnection connection, UsbInterface usbInterface, UsbEndpoint input, UsbEndpoint output)
    {
        _manager = manager;
        _device = device;
        _connection = connection;
        _interface = usbInterface;
        _in = input;
        _out = output;
    }

    /// <summary>Null when no stick is plugged in; throws <see cref="AntException"/> for one the user refused, or that will not open.</summary>
    public static IAntTransport? TryOpen()
    {
        if (Application.Context.GetSystemService(Context.UsbService) is not UsbManager manager)
        {
            return null;
        }

        var sticks = manager.DeviceList?.Values.Where(d => d.VendorId == Dynastream && Products.Contains(d.ProductId)).ToList() ?? [];
        lock (Refused)
        {
            // Unplugged sticks are forgotten, so plugging one in again asks again.
            Refused.IntersectWith(sticks.Select(d => d.DeviceName));
        }

        if (sticks.FirstOrDefault() is not { } device)
        {
            return null;
        }

        if (!manager.HasPermission(device) && !AskPermission(manager, device))
        {
            throw new AntException(Strings.Android_AntDenied);
        }

        var usbInterface = device.InterfaceCount > 0 ? device.GetInterface(0) : null;
        UsbEndpoint? input = null, output = null;
        for (var i = 0; i < (usbInterface?.EndpointCount ?? 0); i++)
        {
            var endpoint = usbInterface!.GetEndpoint(i);
            if (endpoint?.Type != UsbAddressing.XferBulk)
            {
                continue;
            }

            if (endpoint.Direction == UsbAddressing.In)
            {
                input ??= endpoint;
            }
            else
            {
                output ??= endpoint;
            }
        }

        if (usbInterface is null || input is null || output is null)
        {
            throw new AntException(Loc.Format(CoreStrings.Ant_OpenFailed, "no bulk endpoints"));
        }

        var connection = manager.OpenDevice(device) ?? throw new AntException(Loc.Format(CoreStrings.Ant_OpenFailed, "open failed"));
        if (!connection.ClaimInterface(usbInterface, true))
        {
            connection.Close();
            throw new AntException(Loc.Format(CoreStrings.Ant_OpenFailed, "claim failed"));
        }

        return new AndroidUsbTransport(manager, device, connection, usbInterface, input, output);
    }

    /// <summary>Whether a stick is plugged in that the app may open.</summary>
    public static bool HasUsableStick() =>
        Application.Context.GetSystemService(Context.UsbService) is UsbManager manager
        && manager.DeviceList?.Values.Any(d => d.VendorId == Dynastream && Products.Contains(d.ProductId) && manager.HasPermission(d)) == true;

    public void Write(byte[] message)
    {
        var connection = _connection;
        ObjectDisposedException.ThrowIf(connection is null, this);
        if (connection.BulkTransfer(_out, message, message.Length, 1000) < 0)
        {
            throw new AntException(Loc.Format(CoreStrings.Ant_UsbFailed, "write failed"));
        }
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        var connection = _connection;
        ObjectDisposedException.ThrowIf(connection is null, this);
        var read = connection.BulkTransfer(_in, buffer, buffer.Length, timeoutMs);
        if (read >= 0)
        {
            return read;
        }

        // Android reports a timeout and an unplugged stick alike; only the second takes the stick off the bus.
        return IsPluggedIn()
            ? 0
            : throw new AntException(Loc.Format(CoreStrings.Ant_UsbFailed, "unplugged"));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
        {
            connection.ReleaseInterface(_interface);
            connection.Close();
        }
    }

    private bool IsPluggedIn() => _manager.DeviceList?.ContainsKey(_device.DeviceName) == true;

    /// <summary>Shows Android's "allow the app to access the stick" dialog and waits for the answer.</summary>
    private static bool AskPermission(UsbManager manager, UsbDevice device)
    {
        lock (Refused)
        {
            if (Refused.Contains(device.DeviceName))
            {
                return false;
            }
        }

        var context = Application.Context;
        var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new PermissionReceiver(answered);
        var filter = new IntentFilter(PermissionAction);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        }
        else
        {
            context.RegisterReceiver(receiver, filter);
        }

        try
        {
            // Android adds the answer to the intent, so it has to be mutable; naming the package keeps it explicit.
            var intent = new Intent(PermissionAction).SetPackage(context.PackageName);
            var pending = PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent);
            manager.RequestPermission(device, pending);
            var granted = answered.Task.Wait(PermissionTimeout) && answered.Task.Result;
            if (!granted)
            {
                lock (Refused)
                {
                    Refused.Add(device.DeviceName);
                }
            }

            return granted && manager.HasPermission(device);
        }
        finally
        {
            context.UnregisterReceiver(receiver);
        }
    }

    private sealed class PermissionReceiver(TaskCompletionSource<bool> answered) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) =>
            answered.TrySetResult(intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) == true);
    }
}
