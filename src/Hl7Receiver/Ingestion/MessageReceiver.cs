using System.Security.Cryptography;
using Hl7Receiver.Hl7;
using Hl7Receiver.Storage;

namespace Hl7Receiver.Ingestion;

/// <summary>What the receiver hands back to the HTTP layer: the row id and an ACK. Nothing about validity — that comes later.</summary>
public sealed record Receipt(long MessageId, MessageHeader Header, string Ack);

/// <summary>
/// The hot path. Does exactly one thing: get the bytes durably into <c>messages</c> (status = received) and wake the
/// worker. No parsing, no validation — "we either have the file or we don't". The only work beyond the INSERT is a
/// best-effort read of the MSH line so the row is attributed to a sender from the start (it never throws).
/// </summary>
public sealed class MessageReceiver(
    MessageRepository repository,
    ProcessingQueue queue,
    AckBuilder acks,
    TimeProvider clock,
    ILogger<MessageReceiver> logger)
{
    public Receipt Receive(byte[] payload)
    {
        var receivedAt = clock.GetUtcNow();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        var header = MessageHeader.Sniff(PayloadDecoder.Decode(payload).Text);

        var id = repository.InsertReceived(new MessageReceipt(receivedAt, payload, sha256, header));
        queue.Signal();

        logger.LogInformation("HL7 received id={MessageId} sender={Facility}/{Application} controlId={ControlId} type={MessageType} bytes={Bytes}",
            id, header.SendingFacility, header.SendingApplication, header.MessageControlId, header.MessageType, payload.Length);

        return new Receipt(id, header, acks.Received(header));
    }
}
