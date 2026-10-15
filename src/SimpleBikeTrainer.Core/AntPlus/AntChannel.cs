using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.AntPlus;

/// <summary>Who a channel listens to; a device number, type or transmission type of 0 matches any.</summary>
internal readonly record struct AntChannelConfig(int DeviceNumber, byte DeviceType, byte TransmissionType, ushort Period, bool BackgroundScan = false);

internal sealed class AntChannel
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    private readonly AntStick _stick;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TaskCompletionSource<bool>? _ack;
    private TaskCompletionSource? _closedEvent;

    public AntChannel(AntStick stick, byte number, AntChannelConfig config)
    {
        _stick = stick;
        Number = number;
        Config = config;
    }

    public event Action<AntReceived>? DataReceived;

    /// <summary>The channel lost the device and went back to searching for it.</summary>
    public event Action? SignalLost;

    public byte Number { get; }

    public AntChannelConfig Config { get; }

    /// <summary>Also runs after the stick comes back from being unplugged.</summary>
    public async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var n = Number;
        var c = Config;

        // Extended assignment 0x01 makes a background scanning channel.
        byte[] assign = c.BackgroundScan ? [n, 0x00, 0, 0x01] : [n, 0x00, 0];
        await _stick.CommandAsync(AntMessage.AssignChannel, assign, cancellationToken);
        byte transmission = (byte)((c.TransmissionType & 0x0F) | ((c.DeviceNumber >> 12) & 0xF0));
        await _stick.CommandAsync(AntMessage.ChannelId, [n, (byte)c.DeviceNumber, (byte)(c.DeviceNumber >> 8), c.DeviceType, transmission], cancellationToken);
        await _stick.CommandAsync(AntMessage.ChannelPeriod, [n, (byte)c.Period, (byte)(c.Period >> 8)], cancellationToken);
        await _stick.CommandAsync(AntMessage.RfFrequency, [n, AntStick.AntPlusFrequency], cancellationToken);

        // High priority search off: it stops the other channels hearing their devices while it runs.
        await _stick.CommandAsync(AntMessage.SearchTimeout, [n, 0], cancellationToken);
        await _stick.CommandAsync(AntMessage.LowPrioritySearchTimeout, [n, 0xFF], cancellationToken);
        await _stick.CommandAsync(AntMessage.OpenChannel, [n], cancellationToken);
    }

    /// <summary>Best effort: the stick may be gone.</summary>
    public async Task CloseAsync()
    {
        try
        {
            if (_stick.IsAvailable)
            {
                var closed = _closedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _stick.CommandAsync(AntMessage.CloseChannel, [Number], CancellationToken.None);
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await _stick.CommandAsync(AntMessage.UnassignChannel, [Number], CancellationToken.None);
            }
        }
        catch (Exception e) when (e is AntException or TimeoutException)
        {
        }
        finally
        {
            _stick.Free(this);
        }
    }

    /// <summary>Tries three times; false when the device never acknowledged.</summary>
    public async Task<bool> SendAcknowledgedAsync(byte[] page, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var ack = _ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stick.Write(AntMessage.AcknowledgedData, [Number, .. page]);
                try
                {
                    if (await ack.Task.WaitAsync(AckTimeout, cancellationToken))
                    {
                        return true;
                    }
                }
                catch (TimeoutException)
                {
                }
            }

            return false;
        }
        finally
        {
            _ack = null;
            _sendLock.Release();
        }
    }

    public void OnData(AntReceived received) => DataReceived?.Invoke(received);

    public void OnEvent(byte messageId, byte code)
    {
        if (messageId != AntMessage.EventMessageId)
        {
            // A response to acknowledged data that the stick would not send, e.g. a transfer already in progress.
            if (messageId == AntMessage.AcknowledgedData && code != 0)
            {
                _ack?.TrySetResult(false);
            }

            return;
        }

        switch (code)
        {
            case AntMessage.EventTransferTxCompleted:
                _ack?.TrySetResult(true);
                break;
            case AntMessage.EventTransferTxFailed:
                _ack?.TrySetResult(false);
                break;
            case AntMessage.EventRxFailGoToSearch:
                SignalLost?.Invoke();
                break;
            case AntMessage.EventChannelClosed:
                _closedEvent?.TrySetResult();
                break;
        }
    }

    public override string ToString() => Loc.Format("{0} {1}", Number, Config);
}
