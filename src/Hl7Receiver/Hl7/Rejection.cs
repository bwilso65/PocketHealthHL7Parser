namespace Hl7Receiver.Hl7;

/// <summary>HL7 acknowledgement codes (MSA-1, table 0008).</summary>
public enum AckCode
{
    /// <summary>Application Accept — validated and queued (or an idempotent duplicate).</summary>
    AA,
    /// <summary>Application Error — the message was understood but its content is not acceptable (e.g. required field missing).</summary>
    AE,
    /// <summary>Application Reject — we can't or won't process this kind of message (unparseable, unsupported type, structural violation).</summary>
    AR,
}

/// <summary>
/// Why a message was not accepted. <see cref="Code"/> is our own stable vocabulary (stored in the DB, returned by
/// the API, useful for dashboards); <see cref="Hl7ErrorCode"/> is the HL7 table-0357 code carried back to the sender
/// in ERR-3; <see cref="Ack"/> is the MSA-1 code.
/// </summary>
public sealed record Rejection(string Code, AckCode Ack, string Hl7ErrorCode, string Detail)
{
    // HL7 table 0357 codes we use
    private const string Hl7SegmentSequenceError = "100";
    private const string Hl7RequiredFieldMissing = "101";
    private const string Hl7UnsupportedMessageType = "200";
    private const string Hl7ApplicationInternalError = "207";

    public static Rejection Unparseable(string detail) =>
        new("UNPARSEABLE", AckCode.AR, Hl7SegmentSequenceError, detail);

    public static Rejection MultipleMsh(int count) =>
        new("MULTIPLE_MSH", AckCode.AR, Hl7SegmentSequenceError,
            $"Payload contains {count} MSH segments; one HL7 message per request is required");

    public static Rejection UnsupportedMessageType(string? messageType) =>
        new("UNSUPPORTED_MESSAGE_TYPE", AckCode.AR, Hl7UnsupportedMessageType,
            $"Message type '{messageType ?? "(missing)"}' is not supported by this endpoint (expected ORU^R01)");

    public static Rejection SegmentSequence(string detail) =>
        new("SEGMENT_SEQUENCE", AckCode.AE, Hl7SegmentSequenceError, detail);

    public static Rejection RequiredSegmentMissing(string segment) =>
        new("REQUIRED_SEGMENT_MISSING", AckCode.AE, Hl7RequiredFieldMissing, $"Required segment {segment} is missing");

    public static Rejection RequiredField(string field, string description) =>
        new("REQUIRED_FIELD_MISSING", AckCode.AE, Hl7RequiredFieldMissing, $"{field} ({description}) is required but missing or empty");

    public static Rejection NoObservations(string accession) =>
        new("NO_OBSERVATIONS", AckCode.AE, Hl7RequiredFieldMissing, $"Report {accession} has no OBX segments (no report content)");

    /// <summary>Our fault, not the message's: validation itself threw. Retrying won't help; the payload is kept for replay.</summary>
    public static Rejection InternalError(string detail) =>
        new("PROCESSING_ERROR", AckCode.AE, Hl7ApplicationInternalError, $"Internal error while validating the message: {detail}");
}
