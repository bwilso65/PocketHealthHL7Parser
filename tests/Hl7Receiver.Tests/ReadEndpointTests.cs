using System.Net;
using System.Text.Json;

namespace Hl7Receiver.Tests;

/// <summary>GET /messages/{id}, GET /messages/{id}/raw, GET /messages?… — the "is it in there and correct?" path.</summary>
public class ReadEndpointTests
{
    private static async Task<(HttpStatusCode Status, JsonDocument? Json, string Body)> Get(TestServer server, string url)
    {
        using var response = await server.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        var json = response.Content.Headers.ContentType?.MediaType == "application/json" ? JsonDocument.Parse(body) : null;
        return (response.StatusCode, json, body);
    }

    [Fact]
    public async Task Post_then_get_by_id_returns_outcome_and_extracted_report()
    {
        using var server = new TestServer();
        var posted = await server.PostSample("02_oru_valid_01.hl7");

        var (status, json, _) = await Get(server, $"/messages/{posted.MessageId}");

        Assert.Equal(HttpStatusCode.OK, status);
        var m = json!.RootElement;
        Assert.Equal(long.Parse(posted.MessageId!), m.GetProperty("id").GetInt64());
        Assert.Equal("accepted", m.GetProperty("status").GetString());
        Assert.Equal("MSG00001", m.GetProperty("messageControlId").GetString());
        Assert.Equal("ORU^R01", m.GetProperty("messageType").GetString());
        Assert.Equal("WOODBINE", m.GetProperty("sender").GetProperty("facility").GetString());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("rejection").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("duplicateOf").ValueKind);

        var reports = m.GetProperty("reports");
        Assert.Equal(1, reports.GetArrayLength());
        var report = reports[0];
        Assert.Equal("FIL0001", report.GetProperty("accessionNumber").GetString());
        Assert.Equal("CHEST 2 VIEWS", report.GetProperty("procedure").GetProperty("description").GetString());
        Assert.Equal("2026-01-01T11:55:00", report.GetProperty("observationDateTime").GetString());

        var patient = report.GetProperty("patient");
        Assert.Equal("PT00012345", patient.GetProperty("identifier").GetString());
        Assert.Equal("MR", patient.GetProperty("identifierType").GetString());
        Assert.Equal("DOE", patient.GetProperty("familyName").GetString());
        Assert.Equal("JANE", patient.GetProperty("givenName").GetString());
        Assert.Equal("1985-03-15", patient.GetProperty("dateOfBirth").GetString());
        Assert.Equal("F", patient.GetProperty("sex").GetString());

        Assert.Contains("IMPRESSION: Normal chest radiograph.", report.GetProperty("reportText").GetString());
        var observations = report.GetProperty("observations");
        Assert.Equal(3, observations.GetArrayLength());
        Assert.Equal(2, observations[1].GetProperty("setId").GetInt32());
        Assert.Equal("F", observations[1].GetProperty("resultStatus").GetString());
        Assert.Equal("FINDINGS: Lungs are clear. No pleural effusion.", observations[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Rejected_message_shows_the_reason_and_no_reports()
    {
        using var server = new TestServer();
        var posted = await server.PostSample("07_malformed_truncated.hl7");

        var (status, json, _) = await Get(server, $"/messages/{posted.MessageId}");

        Assert.Equal(HttpStatusCode.OK, status);
        var m = json!.RootElement;
        Assert.Equal("rejected", m.GetProperty("status").GetString());
        Assert.Equal("REQUIRED_FIELD_MISSING", m.GetProperty("rejection").GetProperty("code").GetString());
        Assert.Contains("OBX-11", m.GetProperty("rejection").GetProperty("detail").GetString());
        Assert.Equal(0, m.GetProperty("reports").GetArrayLength());
    }

    [Fact]
    public async Task Duplicate_points_at_the_original()
    {
        using var server = new TestServer();
        var original = await server.PostSample("02_oru_valid_01.hl7");
        var retry = await server.PostSample("04_oru_duplicate_retry.hl7");

        var (_, json, _) = await Get(server, $"/messages/{retry.MessageId}");

        var m = json!.RootElement;
        Assert.Equal("duplicate", m.GetProperty("status").GetString());
        Assert.Equal(long.Parse(original.MessageId!), m.GetProperty("duplicateOf").GetInt64());
        Assert.Contains("DIFFERS", m.GetProperty("detail").GetString());
        Assert.Equal(0, m.GetProperty("reports").GetArrayLength());
    }

    [Fact]
    public async Task Raw_returns_the_exact_bytes_received()
    {
        using var server = new TestServer();
        var posted = await server.PostSample("06_malformed_double_msh.hl7"); // rejected, but the bytes are kept

        using var response = await server.Client.GetAsync($"/messages/{posted.MessageId}/raw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestServer.Sample("06_malformed_double_msh.hl7"), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Unknown_id_is_404()
    {
        using var server = new TestServer();

        Assert.Equal(HttpStatusCode.NotFound, (await Get(server, "/messages/424242")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(server, "/messages/424242/raw")).Status);
    }

    [Fact]
    public async Task Search_by_control_id_finds_every_receipt_newest_first()
    {
        using var server = new TestServer();
        await server.PostSample("02_oru_valid_01.hl7");            // WOODBINE MSG00001 accepted
        await server.PostSample("03_oru_valid_02.hl7");            // RIVERSIDE MSG00042
        await server.PostSample("04_oru_duplicate_retry.hl7");     // WOODBINE MSG00001 duplicate

        var (_, json, _) = await Get(server, "/messages?controlId=MSG00001");
        var list = json!.RootElement;
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("duplicate", list[0].GetProperty("status").GetString());   // newest first
        Assert.Equal("accepted", list[1].GetProperty("status").GetString());
        Assert.Equal(1, list[1].GetProperty("reportCount").GetInt32());

        (_, json, _) = await Get(server, "/messages?controlId=MSG00001&status=accepted");
        Assert.Equal(1, json!.RootElement.GetArrayLength());

        (_, json, _) = await Get(server, "/messages?facility=riverside_imaging");   // case-insensitive
        Assert.Equal(1, json!.RootElement.GetArrayLength());
        Assert.Equal("MSG00042", json.RootElement[0].GetProperty("messageControlId").GetString());

        (_, json, _) = await Get(server, "/messages");
        Assert.Equal(3, json!.RootElement.GetArrayLength());

        (_, json, _) = await Get(server, "/messages?limit=1");
        Assert.Equal(1, json!.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Search_rejects_an_unknown_status_filter()
    {
        using var server = new TestServer();

        Assert.Equal(HttpStatusCode.BadRequest, (await Get(server, "/messages?status=bogus")).Status);
    }

    [Fact]
    public async Task Post_response_links_to_the_message()
    {
        using var server = new TestServer();
        using var content = new StringContent(
            "MSH|^~\\&|RIS|WOODBINE|PH|PH|20260101120000||ORU^R01|LINK-1|P|2.5\rPID|1||P1^^^W^MR||A^B\rOBR|1|O^R|F^R|1^X^CPT|||20260101115500\rOBX|1|TX|1^X^CPT||t||||||F\r");
        using var response = await server.Client.PostAsync("/messages", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("X-Message-Id").Single();
        Assert.Equal($"/messages/{id}", response.Headers.Location?.ToString());
    }
}
