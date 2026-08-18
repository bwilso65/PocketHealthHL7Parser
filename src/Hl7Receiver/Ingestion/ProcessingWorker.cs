using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>
/// Drains the <c>messages</c> queue (status = received) in receipt order, one message at a time.
///
/// Why one worker: SQLite has a single writer anyway, per-message work is ~1 ms, and FIFO preserves per-sender
/// ordering (a correction must not overtake its original). Bursts don't slow the *receipt* path — the receiver only
/// does an INSERT — but a burst from provider A does delay processing of provider B's messages by however long A's
/// queue takes; per-facility lanes are the next step if that ever matters (see README).
///
/// Durability: on startup it sweeps whatever was received but not processed before the last shutdown/crash; at
/// runtime it wakes on <see cref="ProcessingQueue.Signal"/> and additionally sweeps every <see cref="SweepInterval"/>
/// as a safety net. A message whose processing throws is marked <c>failed</c> (kept for replay) and the worker moves on.
/// </summary>
public sealed class ProcessingWorker(
    MessageRepository repository,
    MessageProcessor processor,
    ProcessingQueue queue,
    TimeProvider clock,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup isn't blocked by a large backlog sweep.
        await Task.Yield();

        var backlog = repository.CountPending();
        logger.LogInformation("Processing worker started; {Pending} message(s) pending from before startup", backlog);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Drain(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // e.g. the database is briefly unavailable — nothing is lost, everything is still 'received'.
                logger.LogError(ex, "Processing loop error; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }

            await queue.WaitAsync(SweepInterval, stoppingToken);
        }

        logger.LogInformation("Processing worker stopped");
    }

    /// <summary>Processes everything currently pending. Public so tests (and a future replay command) can drive it directly.</summary>
    public void Drain(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var batch = repository.NextPending(BatchSize);
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var id in batch)
            {
                ct.ThrowIfCancellationRequested();
                ProcessOne(id);
            }
        }
    }

    private void ProcessOne(long id)
    {
        try
        {
            processor.Process(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HL7 processing failed id={MessageId}; marking as failed", id);
            try
            {
                repository.MarkFailed(id, $"{ex.GetType().Name}: {ex.Message}", clock.GetUtcNow());
            }
            catch (Exception inner)
            {
                // Can't even record the failure (storage down). Leave it 'received'; the next sweep retries it.
                logger.LogError(inner, "Could not mark id={MessageId} as failed; it stays pending", id);
                throw;
            }
        }
    }
}
