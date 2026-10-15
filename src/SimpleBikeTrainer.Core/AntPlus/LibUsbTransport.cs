using System.Runtime.InteropServices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.AntPlus;

internal interface IAntTransport : IDisposable
{
    void Write(byte[] message);

    /// <summary>Reads what the stick sent, waiting up to <paramref name="timeoutMs"/>. 0 when nothing came; throws when the stick is gone.</summary>
    int Read(byte[] buffer, int timeoutMs);
}

/// <summary>libusb loads on first use, so without it the app runs Bluetooth only.</summary>
internal sealed unsafe class LibUsbTransport : IAntTransport
{
    private const ushort Dynastream = 0x0FCF;

    // Both sticks have one interface with a bulk endpoint each way.
    private const byte OutEndpoint = 0x01;
    private const byte InEndpoint = 0x81;

    private const int ErrorTimeout = -7;

    // ANTUSB2 and ANTUSB-m. The first Garmin stick (0x1004) is a serial adapter and is not supported.
    private static readonly ushort[] Products = [0x1008, 0x1009];

    private static readonly Lazy<LibUsb?> Library = new(LibUsb.TryLoad);

    private readonly LibUsb _usb;
    private nint _handle;

    private LibUsbTransport(LibUsb usb, nint handle)
    {
        _usb = usb;
        _handle = handle;
    }

    /// <summary>Null when there is no stick, or no libusb; throws <see cref="AntException"/> for a stick that will not open.</summary>
    public static IAntTransport? TryOpen()
    {
        if (Library.Value is not { } usb)
        {
            return null;
        }

        nint* list;
        var count = usb.GetDeviceList(usb.Context, &list);
        if (count < 0)
        {
            return null;
        }

        nint handle = 0;
        var error = 0;
        try
        {
            var descriptor = stackalloc byte[18];
            for (var i = 0; i < count && handle == 0; i++)
            {
                // libusb_device_descriptor: idVendor at byte 8, idProduct at byte 10.
                if (usb.GetDeviceDescriptor(list[i], descriptor) < 0
                    || *(ushort*)(descriptor + 8) != Dynastream
                    || !Products.Contains(*(ushort*)(descriptor + 10)))
                {
                    continue;
                }

                nint opened;
                error = usb.Open(list[i], &opened);
                if (error == 0)
                {
                    handle = opened;
                }
            }
        }
        finally
        {
            usb.FreeDeviceList(list, 1);
        }

        if (handle == 0)
        {
            return error == 0 ? null : throw new AntException(Loc.Format(CoreStrings.Ant_OpenFailed, usb.ErrorName(error)));
        }

        // Linux binds the stick to its usb_serial_simple driver; libusb has to take it back. Elsewhere this does nothing.
        usb.SetAutoDetachKernelDriver(handle, 1);
        error = usb.ClaimInterface(handle, 0);
        if (error < 0)
        {
            usb.Close(handle);
            throw new AntException(Loc.Format(CoreStrings.Ant_OpenFailed, usb.ErrorName(error)));
        }

        return new LibUsbTransport(usb, handle);
    }

    public void Write(byte[] message)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (byte* p = message)
        {
            int written;
            var error = _usb.BulkTransfer(_handle, OutEndpoint, p, message.Length, &written, 1000);
            if (error < 0)
            {
                throw new AntException(Loc.Format(CoreStrings.Ant_UsbFailed, _usb.ErrorName(error)));
            }
        }
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (byte* p = buffer)
        {
            int read;
            var error = _usb.BulkTransfer(_handle, InEndpoint, p, buffer.Length, &read, (uint)timeoutMs);
            return error >= 0 || error == ErrorTimeout
                ? read
                : throw new AntException(Loc.Format(CoreStrings.Ant_UsbFailed, _usb.ErrorName(error)));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _handle, 0) is var handle and not 0)
        {
            _usb.ReleaseInterface(handle, 0);
            _usb.Close(handle);
        }
    }

    /// <summary>The libusb-1.0 functions this uses, looked up at run time.</summary>
    private sealed class LibUsb
    {
        public nint Context;
        public delegate* unmanaged<nint, nint**, nint> GetDeviceList;
        public delegate* unmanaged<nint*, int, void> FreeDeviceList;
        public delegate* unmanaged<nint, byte*, int> GetDeviceDescriptor;
        public delegate* unmanaged<nint, nint*, int> Open;
        public delegate* unmanaged<nint, void> Close;
        public delegate* unmanaged<nint, int, int> SetAutoDetachKernelDriver;
        public delegate* unmanaged<nint, int, int> ClaimInterface;
        public delegate* unmanaged<nint, int, int> ReleaseInterface;
        public delegate* unmanaged<nint, byte, byte*, int, int*, uint, int> BulkTransfer;
        private delegate* unmanaged<int, nint> _errorName;

        public string ErrorName(int error) => Marshal.PtrToStringUTF8(_errorName(error)) ?? error.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static LibUsb? TryLoad()
        {
            string[] names = OperatingSystem.IsWindows() ? ["libusb-1.0.dll"]
                : OperatingSystem.IsMacOS() ? ["libusb-1.0.0.dylib", "/opt/homebrew/lib/libusb-1.0.0.dylib", "/usr/local/lib/libusb-1.0.0.dylib"]
                : ["libusb-1.0.so.0", "libusb-1.0.so"];
            nint library = 0;
            if (!names.Any(name => NativeLibrary.TryLoad(name, typeof(LibUsb).Assembly, null, out library)))
            {
                return null;
            }

            try
            {
                var usb = new LibUsb
                {
                    GetDeviceList = (delegate* unmanaged<nint, nint**, nint>)NativeLibrary.GetExport(library, "libusb_get_device_list"),
                    FreeDeviceList = (delegate* unmanaged<nint*, int, void>)NativeLibrary.GetExport(library, "libusb_free_device_list"),
                    GetDeviceDescriptor = (delegate* unmanaged<nint, byte*, int>)NativeLibrary.GetExport(library, "libusb_get_device_descriptor"),
                    Open = (delegate* unmanaged<nint, nint*, int>)NativeLibrary.GetExport(library, "libusb_open"),
                    Close = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "libusb_close"),
                    SetAutoDetachKernelDriver = (delegate* unmanaged<nint, int, int>)NativeLibrary.GetExport(library, "libusb_set_auto_detach_kernel_driver"),
                    ClaimInterface = (delegate* unmanaged<nint, int, int>)NativeLibrary.GetExport(library, "libusb_claim_interface"),
                    ReleaseInterface = (delegate* unmanaged<nint, int, int>)NativeLibrary.GetExport(library, "libusb_release_interface"),
                    BulkTransfer = (delegate* unmanaged<nint, byte, byte*, int, int*, uint, int>)NativeLibrary.GetExport(library, "libusb_bulk_transfer"),
                    _errorName = (delegate* unmanaged<int, nint>)NativeLibrary.GetExport(library, "libusb_error_name"),
                };

                var init = (delegate* unmanaged<nint*, int>)NativeLibrary.GetExport(library, "libusb_init");
                nint context;
                if (init(&context) < 0)
                {
                    return null;
                }

                usb.Context = context;
                return usb;
            }
            catch (EntryPointNotFoundException)
            {
                // A libusb too old for this; ANT+ stays off.
                return null;
            }
        }
    }
}
