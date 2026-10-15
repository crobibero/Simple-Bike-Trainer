using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.AntPlus;

internal sealed class AntStick
{
    /// <summary>The ANT+ managed network's public key and radio frequency (2457 MHz).</summary>
    private static readonly byte[] AntPlusNetworkKey = [0xB9, 0xA5, 0x21, 0xFB, 0xBD, 0x72, 0xC3, 0x45];

    public const byte AntPlusFrequency = 57;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim SharedLock = new(1, 1);
    private static AntStick? _shared;

    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Lock _writeLock = new();
    private readonly Lock _waitLock = new();
    private readonly CancellationTokenSource _closed = new();
    private AntChannel?[] _channels = new AntChannel?[8];
    private Session? _session;
    private Func<AntFrame, bool>? _waitFor;
    private TaskCompletionSource<AntFrame>? _waiting;
    private int _users;

    private AntStick()
    {
    }

    /// <summary>Null when no stick is plugged in. Tests swap it for a fake stick.</summary>
    internal static Func<IAntTransport?> OpenTransport { get; set; } = LibUsbTransport.TryOpen;

    /// <summary>How often to look for the stick again after it was unplugged.</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised when the stick is unplugged, and again once it is back and its channels are open.</summary>
    public event Action<bool>? Available;

    public bool IsAvailable { get; private set; }

    /// <summary>The shared stick, opened if no one has it open yet. Null when none is plugged in.</summary>
    public static async Task<AntStick?> AcquireAsync(CancellationToken cancellationToken)
    {
        await SharedLock.WaitAsync(cancellationToken);
        try
        {
            if (_shared is null)
            {
                if (await Task.Run(OpenTransport, cancellationToken) is not { } transport)
                {
                    return null;
                }

                var stick = new AntStick();
                try
                {
                    await stick.StartAsync(transport, cancellationToken);
                }
                catch
                {
                    await stick._closed.CancelAsync();
                    stick._session?.Close();
                    throw;
                }

                _shared = stick;
            }

            _shared._users++;
            return _shared;
        }
        finally
        {
            SharedLock.Release();
        }
    }

    /// <summary>The last user resets the stick, which closes every channel, and lets go of the USB device.</summary>
    public async Task ReleaseAsync()
    {
        await SharedLock.WaitAsync();
        try
        {
            if (--_users > 0)
            {
                return;
            }

            _shared = null;
            await _closed.CancelAsync();
            try
            {
                Write(AntMessage.ResetSystem, [0]);
            }
            catch
            {
                // Unplugged already.
            }

            _session?.Close();
        }
        finally
        {
            SharedLock.Release();
        }
    }

    public async Task<AntChannel> OpenChannelAsync(AntChannelConfig config, CancellationToken cancellationToken)
    {
        AntChannel channel;
        lock (_waitLock)
        {
            var number = Array.IndexOf(_channels, null);
            if (number < 0)
            {
                throw new AntException(CoreStrings.Ant_NoChannel);
            }

            channel = new AntChannel(this, (byte)number, config);
            _channels[number] = channel;
        }

        try
        {
            await channel.ConfigureAsync(cancellationToken);
            return channel;
        }
        catch
        {
            Free(channel);
            throw;
        }
    }

    public void Free(AntChannel channel)
    {
        lock (_waitLock)
        {
            if (channel.Number < _channels.Length && _channels[channel.Number] == channel)
            {
                _channels[channel.Number] = null;
            }
        }
    }

    /// <summary>A response other than "no error" throws.</summary>
    public async Task CommandAsync(byte id, byte[] data, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            id,
            data,
            f => f.Id == AntMessage.ChannelEvent && f.Data.Length >= 3 && f.Data[0] == data[0] && f.Data[1] == id,
            cancellationToken);
        if (response.Data[2] != 0)
        {
            throw new AntException(Loc.Format(CoreStrings.Ant_Refused, id, response.Data[2]));
        }
    }

    public async Task<AntFrame> RequestAsync(byte id, byte[] data, Func<AntFrame, bool> isReply, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            var waiting = new TaskCompletionSource<AntFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waitLock)
            {
                _waitFor = isReply;
                _waiting = waiting;
            }

            Write(id, data);
            try
            {
                return await waiting.Task.WaitAsync(CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new AntException(Loc.Format(CoreStrings.Ant_NoResponse, id));
            }
        }
        finally
        {
            lock (_waitLock)
            {
                _waitFor = null;
                _waiting = null;
            }

            _commandLock.Release();
        }
    }

    /// <summary>Sends a message that gets no direct response, such as acknowledged data.</summary>
    public void Write(byte id, byte[] data)
    {
        lock (_writeLock)
        {
            if (_session is not { IsOpen: true } session)
            {
                throw new AntException(CoreStrings.Ant_StickLost);
            }

            session.Transport.Write(AntMessage.Encode(id, data));
        }
    }

    private async Task StartAsync(IAntTransport transport, CancellationToken cancellationToken)
    {
        _session = new Session(this, transport);

        // A reset answers with a startup message, which some sticks skip; either way the stick is ready after it.
        try
        {
            await RequestAsync(AntMessage.ResetSystem, [0], f => f.Id == AntMessage.StartupMessage, cancellationToken);
        }
        catch (AntException)
        {
        }

        var capabilities = await RequestAsync(AntMessage.RequestMessage, [0, AntMessage.Capabilities], f => f.Id == AntMessage.Capabilities, cancellationToken);
        if (capabilities.Data is [var channels and > 0, ..])
        {
            lock (_waitLock)
            {
                if (_channels.Length != channels)
                {
                    var resized = new AntChannel?[channels];
                    Array.Copy(_channels, resized, Math.Min(channels, _channels.Length));
                    _channels = resized;
                }
            }
        }

        await CommandAsync(AntMessage.NetworkKey, [0, .. AntPlusNetworkKey], cancellationToken);
        await CommandAsync(AntMessage.LibConfig, [0, AntMessage.ExtendedChannelId | AntMessage.ExtendedRssi], cancellationToken);
        IsAvailable = true;
    }

    private void Dispatch(AntFrame frame)
    {
        AntChannel? channel;
        lock (_waitLock)
        {
            if (_waitFor?.Invoke(frame) == true)
            {
                _waiting?.TrySetResult(frame);
                return;
            }

            channel = frame.Data.Length > 0 && frame.Data[0] < _channels.Length ? _channels[frame.Data[0]] : null;
        }

        if (channel is null)
        {
            return;
        }

        switch (frame.Id)
        {
            case AntMessage.ChannelEvent when frame.Data.Length >= 3:
                channel.OnEvent(frame.Data[1], frame.Data[2]);
                break;
            case AntMessage.BroadcastData or AntMessage.AcknowledgedData or AntMessage.BurstData:
                if (AntReceived.Parse(frame.Data) is { } received)
                {
                    channel.OnData(received);
                }

                break;
        }
    }

    private async Task RecoverAsync(Session lost)
    {
        IsAvailable = false;
        lost.Close();
        Available?.Invoke(false);
        while (!_closed.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryDelay, _closed.Token);
                if (OpenTransport() is not { } transport)
                {
                    continue;
                }

                await StartAsync(transport, _closed.Token);
                AntChannel[] channels;
                lock (_waitLock)
                {
                    channels = [.. _channels.OfType<AntChannel>()];
                }

                foreach (var channel in channels)
                {
                    await channel.ConfigureAsync(_closed.Token);
                }

                Available?.Invoke(true);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AntException)
            {
                // Back but not ready, or not back yet: try again.
                IsAvailable = false;
                _session?.Close();
            }
        }
    }

    /// <summary>One opening of the USB device, and the thread that reads from it.</summary>
    private sealed class Session
    {
        private readonly AntStick _stick;
        private readonly Thread _reader;
        private volatile bool _open = true;

        public Session(AntStick stick, IAntTransport transport)
        {
            _stick = stick;
            Transport = transport;
            _reader = new Thread(Read) { IsBackground = true, Name = "ANT+ stick" };
            _reader.Start();
        }

        public IAntTransport Transport { get; }

        public bool IsOpen => _open;

        /// <summary>Stops the reader first: closing the USB device under a read is not safe.</summary>
        public void Close()
        {
            _open = false;
            if (Thread.CurrentThread != _reader)
            {
                _reader.Join(TimeSpan.FromSeconds(2));
            }

            lock (_stick._writeLock)
            {
                Transport.Dispose();
            }
        }

        private void Read()
        {
            var buffer = new byte[64];
            var frames = new AntFrameReader();
            try
            {
                while (_open)
                {
                    var read = Transport.Read(buffer, 250);
                    foreach (var frame in frames.Push(buffer.AsSpan(0, read)))
                    {
                        _stick.Dispatch(frame);
                    }
                }
            }
            catch (Exception) when (_open && !_stick._closed.IsCancellationRequested)
            {
                _open = false;
                Task.Run(() => _stick.RecoverAsync(this));
            }
            catch
            {
                // Closed while reading.
            }
        }
    }
}
