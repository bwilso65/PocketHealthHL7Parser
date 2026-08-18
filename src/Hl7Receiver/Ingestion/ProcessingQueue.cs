using System.Threading.Channels;

namespace Hl7Receiver.Ingestion;

/// <summary>
/// The wake-up signal between the receiver and the worker. The *queue itself* is the <c>messages</c> table
/// (status = received) — durable across restarts; this channel only tells the worker "there's something new",
/// so a burst of signals collapses into one and a missed signal is caught by the worker's periodic sweep.
/// </summary>
public sealed class ProcessingQueue
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Signal() => _signal.Writer.TryWrite(true);

    /// <summary>Waits until signalled or <paramref name="timeout"/> elapses (whichever first). Drains pending signals.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await _signal.Reader.WaitToReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // periodic sweep
        }

        while (_signal.Reader.TryRead(out _)) { }
    }
}
