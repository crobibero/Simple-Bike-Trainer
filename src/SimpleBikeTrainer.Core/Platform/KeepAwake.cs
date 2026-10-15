using Tmds.DBus;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Platform;

/// <summary>The freedesktop screensaver: KDE, Xfce, and others.</summary>
[DBusInterface("org.freedesktop.ScreenSaver")]
public interface IScreenSaver : IDBusObject
{
    Task<uint> InhibitAsync(string applicationName, string reason);

    Task UnInhibitAsync(uint cookie);
}

/// <summary>The desktop portal's inhibit, for desktops without the screensaver interface (GNOME).</summary>
[DBusInterface("org.freedesktop.portal.Inhibit")]
public interface IPortalInhibit : IDBusObject
{
    Task<ObjectPath> InhibitAsync(string window, uint flags, IDictionary<string, object> options);
}

/// <summary>
/// Keeps the screen on and the computer awake during a ride, through the session D-Bus. Desktops end an inhibit when
/// the connection that asked for it closes, so disposing (or the app exiting) always lets the screen sleep again.
/// </summary>
public sealed class KeepAwake : IAsyncDisposable
{
    // Portal flags: 4 = suspend, 8 = idle (screen blanking and the screensaver).
    private const uint PortalSuspendAndIdle = 4 | 8;

    private readonly Connection? _connection;
    private readonly IScreenSaver? _screenSaver;
    private readonly uint _cookie;

    private KeepAwake(Connection? connection, IScreenSaver? screenSaver, uint cookie, string how)
    {
        _connection = connection;
        _screenSaver = screenSaver;
        _cookie = cookie;
        How = how;
    }

    /// <summary>Whether the desktop agreed to keep the screen on.</summary>
    public bool IsActive => _connection is not null;

    /// <summary>Which interface held the screen on, or why nothing did.</summary>
    public string How { get; }

    /// <summary>Asks the desktop to keep the screen on. Never throws: without a session bus it returns an inactive lock.</summary>
    /// <param name="address">The bus address; the session bus when null.</param>
    public static async Task<KeepAwake> AcquireAsync(string reason, string? address = null)
    {
        address ??= Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            return new KeepAwake(null, null, 0, CoreStrings.Awake_NoBus);
        }

        var connection = new Connection(address);
        try
        {
            await connection.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            connection.Dispose();
            return new KeepAwake(null, null, 0, Loc.Format(CoreStrings.Awake_BusFailed, e.Message));
        }

        try
        {
            var screenSaver = connection.CreateProxy<IScreenSaver>("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver");
            var cookie = await screenSaver.InhibitAsync(AppInfo.Name, reason).WaitAsync(TimeSpan.FromSeconds(3));
            return new KeepAwake(connection, screenSaver, cookie, "org.freedesktop.ScreenSaver");
        }
        catch (Exception e) when (e is DBusException or TimeoutException)
        {
            // Fall through to the portal.
        }

        try
        {
            var portal = connection.CreateProxy<IPortalInhibit>("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop");
            await portal.InhibitAsync(string.Empty, PortalSuspendAndIdle, new Dictionary<string, object> { ["reason"] = reason })
                .WaitAsync(TimeSpan.FromSeconds(3));
            return new KeepAwake(connection, null, 0, "org.freedesktop.portal.Inhibit");
        }
        catch (Exception e) when (e is DBusException or TimeoutException)
        {
            connection.Dispose();
            return new KeepAwake(null, null, 0, Loc.Format(CoreStrings.Awake_NoInterface, e.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_screenSaver is not null)
        {
            try
            {
                await _screenSaver.UnInhibitAsync(_cookie).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is DBusException or TimeoutException or ObjectDisposedException)
            {
                // Closing the connection below releases it anyway.
            }
        }

        _connection?.Dispose();
    }
}
