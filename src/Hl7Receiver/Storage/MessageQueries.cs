using Dapper;

namespace Hl7Receiver.Storage;

/// <summary>
/// Read side for GET /messages... Plain SQL, mapped onto the read models. Rows come back with snake_case
/// column names and Dapper matches them to PascalCase properties (see <see cref="Database"/>).
/// </summary>
public sealed class MessageQueries(Database database)
{
    public static readonly string[] Statuses = ["queued", "accepted", "duplicate", "rejected", "failed"];

    private const string SummaryColumns = """
        m.id, m.received_at, m.processed_at, m.sending_application, m.sending_facility, m.message_control_id, m.message_type,
        m.processing_id, m.hl7_version, m.status, m.ack_code, m.rejection_code, m.detail, m.duplicate_of, m.raw_sha256,
        (SELECT COUNT(*) FROM reports r WHERE r.message_id = m.id) AS report_count
        """;

    /// <summary>One message with its extracted reports (empty for rejected/duplicate), or null.</summary>
    public MessageView? GetById(long id)
    {
        using var connection = database.Open();

        var m = connection.QuerySingleOrDefault<MessageRow>(
            $"SELECT {SummaryColumns} FROM messages m WHERE m.id = @Id", new { Id = id });
        if (m is null)
        {
            return null;
        }

        var reportRows = connection.Query<ReportRow>(
            "SELECT * FROM reports WHERE message_id = @Id ORDER BY sequence", new { Id = id }).ToList();
        var observationRows = connection.Query<ObservationRow>(
            """
            SELECT o.* FROM observations o
            JOIN reports r ON r.id = o.report_id
            WHERE r.message_id = @Id
            ORDER BY o.report_id, o.set_id, o.id
            """, new { Id = id }).ToLookup(o => o.ReportId);

        var reports = reportRows.Select(r => new ReportView(
            Id: r.Id,
            Sequence: r.Sequence,
            AccessionNumber: r.AccessionNumber,
            PlacerOrderNumber: r.PlacerOrderNumber,
            Procedure: new ProcedureView(r.ProcedureCode, r.ProcedureDescription, r.ProcedureCodingSystem),
            ObservationDateTime: r.ObservationDatetime,
            ResultStatus: r.ResultStatus,
            Patient: new PatientView(r.PatientIdentifier, r.PatientIdentifierAuthority, r.PatientIdentifierType,
                r.PatientFamilyName, r.PatientGivenName, r.PatientMiddleName, r.PatientDateOfBirth, r.PatientSex),
            ReportText: r.ReportText,
            MessageDateTime: r.MessageDatetime,
            Observations: observationRows[r.Id]
                .Select(o => new ObservationView((int?)o.SetId, o.ValueType, o.Identifier, o.IdentifierText, o.Value, o.Units, o.ResultStatus))
                .ToList()))
            .ToList();

        return new MessageView(m.Id, m.ReceivedAt, m.ProcessedAt, m.Status, m.AckCode, new SenderView(m.SendingApplication, m.SendingFacility),
            m.MessageControlId, m.MessageType, m.ProcessingId, m.Hl7Version, Rejection(m), m.DuplicateOf, m.Detail,
            m.RawSha256, reports);
    }

    /// <summary>The exact bytes that were received, or null.</summary>
    public byte[]? GetRaw(long id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<byte[]>("SELECT raw_message FROM messages WHERE id = @Id", new { Id = id });
    }

    /// <summary>
    /// Newest first. Filters are optional and combine with AND. <paramref name="controlId"/> is what the sender knows
    /// (MSH-10); it is only unique per sender, so pass <paramref name="facility"/> too when it matters.
    /// </summary>
    public IReadOnlyList<MessageSummaryView> Search(string? controlId, string? facility, string? status, int limit)
    {
        using var connection = database.Open();

        var rows = connection.Query<MessageRow>(
            $"""
            SELECT {SummaryColumns} FROM messages m
            WHERE (@ControlId IS NULL OR m.message_control_id = @ControlId)
              AND (@Facility  IS NULL OR m.sending_facility = @Facility COLLATE NOCASE)
              AND (@Status    IS NULL OR m.status = @Status)
            ORDER BY m.id DESC
            LIMIT @Limit
            """,
            new { ControlId = controlId, Facility = facility, Status = status, Limit = limit });

        return rows.Select(m => new MessageSummaryView(m.Id, m.ReceivedAt, m.ProcessedAt, m.Status, m.AckCode,
                new SenderView(m.SendingApplication, m.SendingFacility), m.MessageControlId, m.MessageType,
                m.ProcessingId, m.Hl7Version, Rejection(m), m.DuplicateOf, m.Detail, m.RawSha256, m.ReportCount))
            .ToList();
    }

    private static RejectionView? Rejection(MessageRow m) =>
        m.RejectionCode is null ? null : new RejectionView(m.RejectionCode, m.Detail ?? string.Empty);

    // ---- row shapes (snake_case columns → PascalCase via Dapper's MatchNamesWithUnderscores) ----

    private sealed class MessageRow
    {
        public long Id { get; set; }
        public string ReceivedAt { get; set; } = string.Empty;
        public string? ProcessedAt { get; set; }
        public string? SendingApplication { get; set; }
        public string? SendingFacility { get; set; }
        public string? MessageControlId { get; set; }
        public string? MessageType { get; set; }
        public string? ProcessingId { get; set; }
        public string? Hl7Version { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? AckCode { get; set; }
        public string? RejectionCode { get; set; }
        public string? Detail { get; set; }
        public long? DuplicateOf { get; set; }
        public string RawSha256 { get; set; } = string.Empty;
        public int ReportCount { get; set; }
    }

    private sealed class ReportRow
    {
        public long Id { get; set; }
        public long MessageId { get; set; }
        public int Sequence { get; set; }
        public string? AccessionNumber { get; set; }
        public string? PlacerOrderNumber { get; set; }
        public string? ProcedureCode { get; set; }
        public string? ProcedureDescription { get; set; }
        public string? ProcedureCodingSystem { get; set; }
        public string? ObservationDatetime { get; set; }
        public string? ResultStatus { get; set; }
        public string? PatientIdentifier { get; set; }
        public string? PatientIdentifierAuthority { get; set; }
        public string? PatientIdentifierType { get; set; }
        public string? PatientFamilyName { get; set; }
        public string? PatientGivenName { get; set; }
        public string? PatientMiddleName { get; set; }
        public string? PatientDateOfBirth { get; set; }
        public string? PatientSex { get; set; }
        public string ReportText { get; set; } = string.Empty;
        public string? MessageDatetime { get; set; }
    }

    private sealed class ObservationRow
    {
        public long Id { get; set; }
        public long ReportId { get; set; }
        public long? SetId { get; set; }
        public string? ValueType { get; set; }
        public string? Identifier { get; set; }
        public string? IdentifierText { get; set; }
        public string? Value { get; set; }
        public string? Units { get; set; }
        public string? ResultStatus { get; set; }
    }
}
