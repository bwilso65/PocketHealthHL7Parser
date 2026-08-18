namespace Hl7Receiver.Hl7;

/// <summary>
/// Why a message was not accepted. <see cref="Code"/> is a stable vocabulary (stored in the DB, returned by the API,
/// useful for dashboards); <see cref="Detail"/> is for a human.
/// </summary>
public sealed record Rejection(string Code, string Detail)
{
    public static Rejection Unparseable(string detail) =>
        new("UNPARSEABLE", detail);

    public static Rejection MultipleMsh(int count) =>
        new("MULTIPLE_MSH", $"Payload contains {count} MSH segments; one HL7 message per request is required");

    public static Rejection UnsupportedMessageType(string? messageType) =>
        new("UNSUPPORTED_MESSAGE_TYPE", $"Message type '{messageType ?? "(missing)"}' is not supported by this endpoint (expected ORU^R01)");

    public static Rejection SegmentSequence(string detail) =>
        new("SEGMENT_SEQUENCE", detail);

    public static Rejection RequiredSegmentMissing(string segment) =>
        new("REQUIRED_SEGMENT_MISSING", $"Required segment {segment} is missing");

    public static Rejection RequiredField(string field, string description) =>
        new("REQUIRED_FIELD_MISSING", $"{field} ({description}) is required but missing or empty");

    public static Rejection NoObservations(string accession) =>
        new("NO_OBSERVATIONS", $"Report {accession} has no OBX segments (no report content)");
}
