using System.Text;
using System.Text.Json;
using Hl7Receiver.Hl7;
using Microsoft.Extensions.DependencyInjection;

namespace Hl7Receiver.Tests;

/// <summary>Behaviour beyond the provided samples: transport edge cases, leniency we chose, and the provider seam.</summary>
public class EndpointTests
{
    private const string ValidOru =
        "MSH|^~\\&|RIS|WOODBINE|POCKETHEALTH|POCKETHEALTH|20260101120000||ORU^R01|CTRL-1|P|2.5\r" +
        "PID|1||PT1^^^WOODBINE^MR||DOE^JANE\r" +
        "OBR|1|ORD1^RIS|ACC1^RIS|71020^CHEST^CPT|||20260101115500\r" +
        "OBX|1|TX|71020^CHEST^CPT||Normal.||||||F\r";

    [Fact]
    public async Task Empty_body_is_400_and_stores_nothing()
    {
        using var server = new TestServer();
        var result = await server.Post(Encoding.UTF8.GetBytes("  \r\n"));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(0, server.Scalar<long>("SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task Accept_json_returns_a_json_summary_instead_of_an_ack()
    {
        using var server = new TestServer();
        var result = await server.PostText(ValidOru, accept: "application/json");

        Assert.Equal(200, result.StatusCode);
        using var json = JsonDocument.Parse(result.Body);
        var root = json.RootElement;
        Assert.Equal("accepted", root.GetProperty("status").GetString());
        Assert.Equal("AA", root.GetProperty("ackCode").GetString());
        Assert.Equal("CTRL-1", root.GetProperty("messageControlId").GetString());
        Assert.Equal("WOODBINE", root.GetProperty("sender").GetProperty("facility").GetString());
        Assert.Equal(1, root.GetProperty("reports").GetInt32());
        Assert.Equal(result.MessageId, root.GetProperty("messageId").GetInt64().ToString());
    }

    [Fact]
    public async Task Ack_is_well_formed_and_addressed_back_to_the_sender()
    {
        using var server = new TestServer();
        var result = await server.PostText(ValidOru);

        var segments = result.Body.TrimEnd('\r').Split('\r');
        Assert.Equal(2, segments.Length); // MSH + MSA, no ERR on success
        var msh = segments[0].Split('|');
        Assert.Equal("MSH", msh[0]);
        Assert.Equal("^~\\&", msh[1]);
        Assert.Equal("POCKETHEALTH", msh[2]);      // us
        Assert.Equal("RIS", msh[4]);               // back to their application
        Assert.Equal("WOODBINE", msh[5]);          // and facility
        Assert.Equal("ACK^R01^ACK", msh[8]);
        Assert.Equal("P", msh[10]);
        Assert.Equal("2.5", msh[11]);
        Assert.Equal("MSA|AA|CTRL-1|", segments[1]);
    }

    [Theory]
    [InlineData("\n")]     // LF only (Unix tools, some engines)
    [InlineData("\r\n")]   // CRLF (Windows tools)
    public async Task Non_standard_segment_terminators_are_accepted(string terminator)
    {
        using var server = new TestServer();
        var result = await server.PostText(ValidOru.Replace("\r", terminator));

        Assert.Equal("AA", result.AckCode);
        Assert.Equal("accepted", (string)server.Message(result.MessageId)!.status);
    }

    [Fact]
    public async Task Invalid_utf8_falls_back_to_latin1_instead_of_corrupting_names()
    {
        using var server = new TestServer();
        // "CÔTÉ" in ISO-8859-1: 0xD4 = Ô, 0xC9 = É — invalid as UTF-8.
        var body = Encoding.Latin1.GetBytes(ValidOru.Replace("DOE^JANE", "CÔTÉ^RENÉ"));
        var result = await server.Post(body);

        Assert.Equal("AA", result.AckCode);
        Assert.Equal("CÔTÉ", server.Scalar<string>("SELECT patient_family_name FROM reports"));
        Assert.Equal("RENÉ", server.Scalar<string>("SELECT patient_given_name FROM reports"));
    }

    [Fact]
    public async Task Declared_charset_in_MSH18_is_honoured()
    {
        using var server = new TestServer();
        var withCharset = ValidOru.Replace("|P|2.5\r", "|P|2.5||||||8859/1\r"); // MSH-18
        var body = Encoding.Latin1.GetBytes(withCharset.Replace("DOE^JANE", "CÔTÉ^RENÉ"));
        var result = await server.Post(body);

        Assert.Equal("AA", result.AckCode);
        Assert.Equal("CÔTÉ", server.Scalar<string>("SELECT patient_family_name FROM reports"));
    }

    [Fact]
    public async Task Escape_sequences_and_formatted_text_line_breaks_are_decoded()
    {
        using var server = new TestServer();
        var text = ValidOru.Replace("||Normal.||||||F", "||Line one\\.br\\Line two \\T\\ pipe \\F\\ done||||||F");
        var result = await server.PostText(text);

        Assert.Equal("AA", result.AckCode);
        Assert.Equal("Line one\nLine two & pipe | done", server.Scalar<string>("SELECT value FROM observations"));
    }

    [Fact]
    public async Task Multiple_OBR_in_one_message_become_multiple_reports()
    {
        using var server = new TestServer();
        var text = ValidOru +
            "OBR|2|ORD2^RIS|ACC2^RIS|71046^CHEST 2 VIEWS^CPT|||20260101120500\r" +
            "OBX|1|TX|71046^CHEST 2 VIEWS^CPT||Second study, first line.||||||F\r" +
            "OBX|2|TX|71046^CHEST 2 VIEWS^CPT||Second study, second line.||||||F\r";
        var result = await server.PostText(text);

        Assert.Equal("AA", result.AckCode);
        var reports = server.Query("SELECT * FROM reports ORDER BY sequence").ToList();
        Assert.Equal(2, reports.Count);
        Assert.Equal("ACC1", (string)reports[0].accession_number);
        Assert.Equal("ACC2", (string)reports[1].accession_number);
        Assert.Equal("Second study, first line.\nSecond study, second line.", (string)reports[1].report_text);
        Assert.Equal(3, server.Scalar<long>("SELECT COUNT(*) FROM observations"));
    }

    [Fact]
    public async Task Repeating_patient_identifiers_use_the_first_repetition()
    {
        using var server = new TestServer();
        var text = ValidOru.Replace("PT1^^^WOODBINE^MR", "PT1^^^WOODBINE^MR~1234567890^^^ON^JHN");
        var result = await server.PostText(text);

        Assert.Equal("AA", result.AckCode);
        Assert.Equal("PT1", server.Scalar<string>("SELECT patient_identifier FROM reports"));
        Assert.Equal("MR", server.Scalar<string>("SELECT patient_identifier_type FROM reports"));
    }

    [Theory]
    [InlineData("PID|1||PT1^^^WOODBINE^MR||DOE^JANE\r", "PID|1||||DOE^JANE\r", "REQUIRED_FIELD_MISSING", "PID-3")]
    [InlineData("PID|1||PT1^^^WOODBINE^MR||DOE^JANE\r", "PID|1||PT1^^^WOODBINE^MR||\r", "REQUIRED_FIELD_MISSING", "PID-5")]
    [InlineData("OBR|1|ORD1^RIS|ACC1^RIS|71020^CHEST^CPT|||20260101115500\r", "OBR|1|ORD1^RIS||71020^CHEST^CPT|||20260101115500\r", "REQUIRED_FIELD_MISSING", "OBR-3")]
    [InlineData("OBX|1|TX|71020^CHEST^CPT||Normal.||||||F\r", "", "NO_OBSERVATIONS", "no OBX")]
    [InlineData("MSH|^~\\&|RIS|WOODBINE|", "MSH|^~\\&|RIS||", "REQUIRED_FIELD_MISSING", "MSH-4")]
    public async Task Missing_required_content_is_rejected_with_a_specific_reason(string find, string replace, string code, string detailContains)
    {
        using var server = new TestServer();
        var result = await server.PostText(ValidOru.Replace(find, replace));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("AE", result.AckCode);
        var row = server.Message(result.MessageId)!;
        Assert.Equal("rejected", (string)row.status);
        Assert.Equal(code, (string)row.rejection_code);
        Assert.Contains(detailContains, (string)row.detail);
    }

    [Fact]
    public async Task OBX_before_any_OBR_is_a_structural_rejection()
    {
        using var server = new TestServer();
        var text =
            "MSH|^~\\&|RIS|WOODBINE|POCKETHEALTH|POCKETHEALTH|20260101120000||ORU^R01|CTRL-2|P|2.5\r" +
            "PID|1||PT1^^^WOODBINE^MR||DOE^JANE\r" +
            "OBX|1|TX|71020^CHEST^CPT||Orphan.||||||F\r" +
            "OBR|1|ORD1^RIS|ACC1^RIS|71020^CHEST^CPT|||20260101115500\r";
        var result = await server.PostText(text);

        Assert.Equal("AE", result.AckCode);
        Assert.Equal("SEGMENT_SEQUENCE", (string)server.Message(result.MessageId)!.rejection_code);
    }

    [Fact]
    public async Task Unmodelled_segments_are_tolerated()
    {
        using var server = new TestServer();
        var text = ValidOru
            .Replace("OBR|1|", "PV1|1|O|RAD^^^WOODBINE\rORC|RE|ORD1^RIS|ACC1^RIS\rOBR|1|")
            + "NTE|1||Dictated by Dr. Smith.\rZDS|1.2.840.113619.2.55.3^WOODBINE^APPLICATION^DICOM\r";
        var result = await server.PostText(text);

        Assert.Equal("AA", result.AckCode);
        Assert.Equal(1, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
    }

    [Fact]
    public async Task Provider_profile_override_changes_field_mapping_for_that_sender_only()
    {
        // A hypothetical provider that puts the accession number in OBR-2 (placer) instead of OBR-3 (filler).
        var overrides = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["QUIRKY_HOSP"] = ProviderProfile.Default with { Name = "quirky", AccessionNumber = new FieldRef(2, 1) },
        };
        using var server = new TestServer(services =>
            services.AddSingleton<IProviderProfileRegistry>(new ProviderProfileRegistry(overrides)));

        await server.PostText(ValidOru);                                                   // WOODBINE → default profile
        await server.PostText(ValidOru.Replace("|RIS|WOODBINE|", "|RIS|QUIRKY_HOSP|"));     // → override

        var rows = server.Query("SELECT sending_facility, accession_number FROM reports ORDER BY id").ToList();
        Assert.Equal("ACC1", (string)rows[0].accession_number);
        Assert.Equal("ORD1", (string)rows[1].accession_number);
    }
}
