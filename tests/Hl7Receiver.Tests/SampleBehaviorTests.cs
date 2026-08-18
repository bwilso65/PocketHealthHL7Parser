namespace Hl7Receiver.Tests;

/// <summary>
/// The provider's sample files, posted in their numbered order into one server (04 is only a "duplicate" if 02
/// was accepted first). This is the executable form of the behaviour matrix in PLAN.md / README.md.
///
/// Every POST gets HTTP 200 (the bytes were stored). The ACK carries the validation verdict synchronously
/// (AA / AE / AR); for AA messages the reports are written by the background worker, so the final status is read
/// back from the database after the worker has run.
/// </summary>
public class SampleBehaviorTests
{
    public sealed record Expectation(string File, string Ack, string FinalStatus, string? RejectionCode, int Reports);

    public static readonly Expectation[] Matrix =
    [
        new("01_oru_valid_minimal.hl7",     "AA", "accepted",  null,                        1),
        new("02_oru_valid_01.hl7",          "AA", "accepted",  null,                        1),
        new("03_oru_valid_02.hl7",          "AA", "accepted",  null,                        1),
        new("04_oru_duplicate_retry.hl7",   "AA", "duplicate", null,                        0),
        new("05_malformed.hl7",             "AR", "rejected",  "UNPARSEABLE",               0),
        new("06_malformed_double_msh.hl7",  "AR", "rejected",  "MULTIPLE_MSH",              0),
        new("07_malformed_truncated.hl7",   "AE", "rejected",  "REQUIRED_FIELD_MISSING",    0),
        new("08_adt_wrong_type.hl7",        "AR", "rejected",  "UNSUPPORTED_MESSAGE_TYPE",  0),
    ];

    [Fact]
    public async Task All_samples_in_order_produce_the_documented_outcomes()
    {
        using var server = new TestServer();
        var failures = new List<string>();

        foreach (var e in Matrix)
        {
            var result = await server.PostSample(e.File);
            var row = server.Message(result.MessageId);

            void Check<T>(string what, T expected, T actual)
            {
                if (!Equals(expected, actual))
                {
                    failures.Add($"{e.File}: {what} expected '{expected}' but was '{actual}'");
                }
            }

            Check("HTTP status", 200, result.StatusCode);
            Check("MSA-1", e.Ack, result.AckCode);
            Check("messages.ack_code", e.Ack, (string?)row?.ack_code);
            Check("messages.status", e.FinalStatus, (string?)row?.status);
            Check("messages.rejection_code", e.RejectionCode, (string?)row?.rejection_code);
            Check("processed_at set", true, row?.processed_at is not null);

            var reports = server.Scalar<long>("SELECT COUNT(*) FROM reports WHERE message_id = @Id", new { Id = result.MessageId });
            Check("reports created", (long)e.Reports, reports);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Nothing is ever dropped: one messages row per POST, every row has the raw bytes, nothing left in the queue.
        Assert.Equal(Matrix.Length, server.Scalar<long>("SELECT COUNT(*) FROM messages"));
        Assert.Equal(0, server.Scalar<long>("SELECT COUNT(*) FROM messages WHERE raw_message IS NULL OR length(raw_message) = 0"));
        Assert.Equal(0, server.Scalar<long>("SELECT COUNT(*) FROM messages WHERE status = 'queued'"));
    }

    [Fact]
    public async Task Full_report_is_extracted_into_reports_and_observations()
    {
        using var server = new TestServer();
        var result = await server.PostSample("02_oru_valid_01.hl7");

        var report = server.Query("SELECT * FROM reports WHERE message_id = @Id", new { Id = result.MessageId }).Single();
        Assert.Equal("WOODBINE", (string)report.sending_facility);
        Assert.Equal("FIL0001", (string)report.accession_number);
        Assert.Equal("ORD0001", (string)report.placer_order_number);
        Assert.Equal("71020", (string)report.procedure_code);
        Assert.Equal("CHEST 2 VIEWS", (string)report.procedure_description);
        Assert.Equal("2026-01-01T11:55:00", (string)report.observation_datetime);
        Assert.Equal("PT00012345", (string)report.patient_identifier);
        Assert.Equal("WOODBINE", (string)report.patient_identifier_authority);
        Assert.Equal("MR", (string)report.patient_identifier_type);
        Assert.Equal("DOE", (string)report.patient_family_name);
        Assert.Equal("JANE", (string)report.patient_given_name);
        Assert.Equal("A", (string)report.patient_middle_name);
        Assert.Equal("1985-03-15", (string)report.patient_date_of_birth);
        Assert.Equal("F", (string)report.patient_sex);
        Assert.Equal("2026-01-01T12:00:00", (string)report.message_datetime);

        var text = (string)report.report_text;
        Assert.Contains("CLINICAL HISTORY: Cough, two weeks.", text);
        Assert.Contains("IMPRESSION: Normal chest radiograph.", text);
        Assert.Equal(3, text.Split('\n').Length);

        var observations = server.Query("SELECT * FROM observations WHERE report_id = @Id ORDER BY set_id", new { Id = (long)report.id }).ToList();
        Assert.Equal(3, observations.Count);
        Assert.Equal("TX", (string)observations[0].value_type);
        Assert.Equal("F", (string)observations[2].result_status);
        Assert.Equal("FINDINGS: Lungs are clear. No pleural effusion.", (string)observations[1].value);
    }

    [Fact]
    public async Task Duplicate_control_id_is_idempotent_and_flags_a_differing_payload()
    {
        using var server = new TestServer();
        var first = await server.PostSample("02_oru_valid_01.hl7");
        var retry = await server.PostSample("04_oru_duplicate_retry.hl7");

        Assert.Equal(200, retry.StatusCode);
        Assert.Equal("AA", retry.AckCode);                       // the sender's retry succeeds and stops
        Assert.Contains("MSA|AA|MSG00001|Duplicate", retry.Body);
        Assert.Equal("duplicate", retry.Status);

        var row = server.Message(retry.MessageId)!;
        Assert.Equal(long.Parse(first.MessageId!), (long)row.duplicate_of);
        Assert.Contains("DIFFERS", (string)row.detail);           // 04's body is not byte-identical to 02's

        // The original report is untouched and there is still exactly one.
        Assert.Equal(1, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
        var text = server.Scalar<string>("SELECT report_text FROM reports");
        Assert.DoesNotContain("Retry of MSG00001", text);
    }

    [Fact]
    public async Task Same_control_id_from_a_different_facility_is_not_a_duplicate()
    {
        using var server = new TestServer();
        await server.PostSample("02_oru_valid_01.hl7"); // WOODBINE / MSG00001
        var other = await server.PostText(
            "MSH|^~\\&|RIS|OTHER_HOSPITAL|POCKETHEALTH|POCKETHEALTH|20260101120000||ORU^R01|MSG00001|P|2.5\r" +
            "PID|1||X1^^^OTHER^MR||ROE^RICHARD\r" +
            "OBR|1|O1^RIS|F1^RIS|71020^CHEST^CPT|||20260101115500\r" +
            "OBX|1|TX|71020^CHEST^CPT||Different provider, same control id.||||||F\r");

        Assert.Equal("accepted", other.Status);
        Assert.Equal(2, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
    }

    [Fact]
    public async Task Rejected_messages_keep_raw_bytes_and_sender_attribution()
    {
        using var server = new TestServer();

        var malformed = await server.PostSample("05_malformed.hl7");
        Assert.Equal(200, malformed.StatusCode);                        // stored...
        Assert.Equal("AR", malformed.AckCode);                          // ...and honestly rejected
        Assert.StartsWith("MSH|^~\\&|POCKETHEALTH|POCKETHEALTH|RIS|HOSP|", malformed.Body);
        Assert.Contains("MSA|AR||", malformed.Body);                     // no control id to echo
        Assert.Contains("ERR|||100^Segment sequence error^HL70357|E|", malformed.Body);
        var row = server.Message(malformed.MessageId)!;
        Assert.Equal("rejected", (string)row.status);
        Assert.Equal("UNPARSEABLE", (string)row.rejection_code);
        Assert.Equal("RIS", (string)row.sending_application);           // sniffed from the broken MSH
        Assert.Equal("HOSP", (string)row.sending_facility);
        Assert.Null(row.message_control_id);
        Assert.Equal(TestServer.Sample("05_malformed.hl7"), (byte[])row.raw_message);

        var adt = await server.PostSample("08_adt_wrong_type.hl7");
        row = server.Message(adt.MessageId)!;
        Assert.Equal("ADT^A01", (string)row.message_type);
        Assert.Equal("MSG00200", (string)row.message_control_id);
        Assert.Contains("MSA|AR|MSG00200|", adt.Body);
        Assert.Contains("ERR|||200^Unsupported message type^HL70357|E|", adt.Body);

        var truncated = await server.PostSample("07_malformed_truncated.hl7");
        row = server.Message(truncated.MessageId)!;
        Assert.Contains("OBX-11", (string)row.detail);
        Assert.Contains("MSA|AE|MSG00400|", truncated.Body);
        Assert.Contains("ERR|||101^Required field missing^HL70357|E|", truncated.Body);

        var doubled = await server.PostSample("06_malformed_double_msh.hl7");
        row = server.Message(doubled.MessageId)!;
        Assert.Equal("MSG00500", (string)row.message_control_id);   // attributed to the first message's header
        Assert.Contains("2 MSH segments", (string)row.detail);
        Assert.Equal(0, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
    }
}
