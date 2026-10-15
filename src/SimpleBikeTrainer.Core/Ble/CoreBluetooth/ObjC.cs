using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace SimpleBikeTrainer.Ble.CoreBluetooth;

/// <summary>The app targets plain .NET, not the macOS workload, so it calls the Objective-C runtime directly.</summary>
internal static unsafe partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private static readonly ConcurrentDictionary<string, nint> Selectors = new();
    private static readonly ConcurrentDictionary<string, nint> Classes = new();

    public static nint Sel(string name) => Selectors.GetOrAdd(name, sel_registerName);

    public static nint Class(string name) => Classes.GetOrAdd(name, objc_getClass);

    /// <summary>An autoreleased NSString.</summary>
    public static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static string? ToManagedString(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    /// <summary>An autoreleased NSData holding a copy of <paramref name="bytes"/>.</summary>
    public static nint NSData(byte[] bytes)
    {
        fixed (byte* p = bytes)
        {
            return Send(Class("NSData"), Sel("dataWithBytes:length:"), (nint)p, bytes.Length);
        }
    }

    public static byte[] ToBytes(nint nsData)
    {
        if (nsData == 0)
        {
            return [];
        }

        var length = (int)Send(nsData, Sel("length"));
        var bytes = new byte[length];
        if (length > 0)
        {
            new ReadOnlySpan<byte>((void*)Send(nsData, Sel("bytes")), length).CopyTo(bytes);
        }

        return bytes;
    }

    public static IEnumerable<nint> Items(nint nsArray)
    {
        var count = nsArray == 0 ? 0 : Send(nsArray, Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            yield return Send(nsArray, Sel("objectAtIndex:"), i);
        }
    }

    /// <summary>An NSError's message, or null for no error.</summary>
    public static string? ErrorText(nint nsError) =>
        nsError == 0 ? null : ToManagedString(Send(nsError, Sel("localizedDescription")));

    /// <summary>A CBUUID's string, spelled as BlueZ spells it (see <see cref="GattUuids"/>).</summary>
    public static string Uuid(nint cbUuid) => GattUuids.Normalize(ToManagedString(Send(cbUuid, Sel("UUIDString"))) ?? string.Empty);

    /// <summary>Waits for <paramref name="action"/>, inside an autorelease pool: CoreBluetooth objects belong to their manager's queue.</summary>
    public static void RunOn(nint queue, Action action)
    {
        Exception? error = null;
        var handle = GCHandle.Alloc(new Action(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        }));
        try
        {
            dispatch_sync_f(queue, GCHandle.ToIntPtr(handle), (nint)(delegate* unmanaged<nint, void>)&RunAction);
        }
        finally
        {
            handle.Free();
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    [UnmanagedCallersOnly]
    private static void RunAction(nint context)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            ((Action)GCHandle.FromIntPtr(context).Target!)();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [LibraryImport(LibObjC)]
    public static partial void objc_registerClassPair(nint cls);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(LibObjC)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(LibObjC)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint a);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint a, nint b);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint a, nint b, nint c);

    /// <summary>For selectors whose first argument is a BOOL, such as <c>setNotifyValue:forCharacteristic:</c>.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool a, nint b);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool a);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint dispatch_queue_create(string label, nint attributes);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_sync_f(nint queue, nint context, nint work);
}
