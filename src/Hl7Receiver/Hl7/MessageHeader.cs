namespace Hl7Receiver.Hl7;

/// <summary>
/// The MSH fields this service cares about, as sent (no escape decoding).
/// Populated best-effort by <see cref="Sniff"/> even when the message fails to parse, so that
/// rejected payloads can still be attributed to a sender and acknowledged with their control ID.
/// </summary>
public sealed record MessageHeader(
    string? SendingApplication,   // MSH-3
    string? SendingFacility,      // MSH-4
    string? ReceivingApplication, // MSH-5
    string? ReceivingFacility,    // MSH-6
    string? MessageDateTime,      // MSH-7
    string? MessageType,          // MSH-9 verbatim, e.g. "ORU^R01" or "ORU^R01^ORU_R01"
    string? MessageCode,          // MSH-9.1, e.g. "ORU"
    string? TriggerEvent,         // MSH-9.2, e.g. "R01"
    string? MessageControlId,     // MSH-10
    string? ProcessingId,         // MSH-11
    string? VersionId,            // MSH-12
    string? CharacterSet)         // MSH-18
{
    public static readonly MessageHeader Empty = new(null, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>"ORU^R01" style key used for message-type policy checks (null when either part is missing).</summary>
    public string? MessageTypeKey =>
        MessageCode is null || TriggerEvent is null ? null : $"{MessageCode}^{TriggerEvent}";

    /// <summary>
    /// Reads MSH from raw text using only the delimiters declared in MSH-1/MSH-2. Never throws.
    /// Returns <see cref="Empty"/> if the text does not start with an MSH segment.
    /// </summary>
    public static MessageHeader Sniff(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 4 || !text.StartsWith("MSH", StringComparison.Ordinal))
        {
            return Empty;
        }

        var end = text.IndexOfAny(['\r', '\n']);
        var firstSegment = end < 0 ? text : text[..end];

        var fieldSeparator = firstSegment[3];
        var componentSeparator = firstSegment.Length > 4 ? firstSegment[4] : '^';

        // parts[0] = "MSH", parts[1] = encoding characters (MSH-2), parts[2] = MSH-3, ... parts[n] = MSH-(n+1)
        var parts = firstSegment.Split(fieldSeparator);
        string? At(int mshField) => mshField - 1 < parts.Length && parts[mshField - 1].Length > 0 ? parts[mshField - 1] : null;

        var messageType = At(9);
        string? code = null, trigger = null;
        if (messageType is not null)
        {
            var comps = messageType.Split(componentSeparator);
            code = comps.Length > 0 && comps[0].Length > 0 ? comps[0] : null;
            trigger = comps.Length > 1 && comps[1].Length > 0 ? comps[1] : null;
        }

        return new MessageHeader(
            SendingApplication: At(3),
            SendingFacility: At(4),
            ReceivingApplication: At(5),
            ReceivingFacility: At(6),
            MessageDateTime: At(7),
            MessageType: messageType,
            MessageCode: code,
            TriggerEvent: trigger,
            MessageControlId: At(10),
            ProcessingId: At(11),
            VersionId: At(12),
            CharacterSet: At(18));
    }
}
