using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>
/// The asynchronous half, run by the worker for one queued message: re-evaluate the stored bytes (deterministic;
/// the receiver already found them valid) and write the reports/observations. This is where anything slower or
/// riskier than a validation belongs later — patient matching, embedded documents, notifications.
/// </summary>
public sealed class MessageProcessor(
    MessageEvaluator evaluator,
    MessageRepository repository,
    TimeProvider clock,
    ILogger<MessageProcessor> logger)
{
    /// <returns>The resulting status, or null if the message was not queued (already processed, or unknown).</returns>
    public MessageStatus? Process(long messageId)
    {
        var pending = repository.LoadPending(messageId);
        if (pending is null)
        {
            return null;
        }

        var evaluation = evaluator.Evaluate(pending.Raw);
        if (!evaluation.IsValid)
        {
            // Can only happen if the validation rules changed between receipt and processing (e.g. a deploy).
            // The sender was told AA, so this is ours to sort out — keep it, flag it, move on.
            var detail = $"Message was valid at receipt but not at processing time: {evaluation.Rejection!.Code}: {evaluation.Rejection.Detail}";
            repository.MarkFailed(messageId, detail, clock.GetUtcNow());
            logger.LogError("HL7 processing id={MessageId} sender={Facility} controlId={ControlId}: {Detail}",
                messageId, evaluation.Header.SendingFacility, evaluation.Header.MessageControlId, detail);
            return MessageStatus.Failed;
        }

        if (!repository.MarkAccepted(messageId, evaluation.Header, evaluation.Oru!, clock.GetUtcNow()))
        {
            return null; // someone else completed it first
        }

        logger.LogInformation("HL7 processed id={MessageId} accepted sender={Facility}/{Application} controlId={ControlId} profile={Profile} reports={Reports}",
            messageId, evaluation.Header.SendingFacility, evaluation.Header.SendingApplication, evaluation.Header.MessageControlId,
            evaluation.Profile.Name, evaluation.Oru!.Reports.Count);
        return MessageStatus.Accepted;
    }
}
