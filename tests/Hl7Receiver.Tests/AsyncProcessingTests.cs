using System.Net;
using System.Text.Json;
using Hl7Receiver.Ingestion;
using Hl7Receiver.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Hl7Receiver.Tests;

/// <summary>
/// Validation and the ACK are synchronous; writing reports is not. These pin down the seam between the two.
/// </summary>
public class AsyncProcessingTests
{
    [Fact]
    public async Task Rejections_are_final_at_receipt_and_never_queued()
    {
        using var server = new TestServer();

        var result = await server.PostSample("07_malformed_truncated.hl7", waitForProcessing: false);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("AE", result.AckCode);
        var row = server.Message(result.MessageId)!;
        Assert.Equal("rejected", (string)row.status);          // no waiting needed
        Assert.NotNull(row.processed_at);
        Assert.Equal(TestServer.Sample("07_malformed_truncated.hl7"), (byte[])row.raw_message);
        Assert.Equal(0, server.Services.GetRequiredService<MessageRepository>().CountPending());
    }

    [Fact]
    public async Task Valid_messages_are_ACKed_AA_while_queued_then_become_accepted()
    {
        using var server = new TestServer();
        var posted = await server.PostSample("02_oru_valid_01.hl7", waitForProcessing: false);

        Assert.Equal("AA", posted.AckCode);
        // At this instant the worker may or may not have run; either way the row exists with the raw bytes and an AA.
        var row = server.Message(posted.MessageId)!;
        Assert.Contains((string)row.status, new[] { "queued", "accepted" });
        Assert.Equal("AA", (string)row.ack_code);

        Assert.Equal("accepted", await server.WaitProcessed(posted.MessageId!));
        using var response = await server.Client.GetAsync($"/messages/{posted.MessageId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("accepted", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("AA", json.RootElement.GetProperty("ackCode").GetString());
        Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("processedAt").ValueKind);
        Assert.Equal(1, json.RootElement.GetProperty("reports").GetArrayLength());
    }

    [Fact]
    public async Task Duplicate_of_a_queued_message_is_detected_at_receipt()
    {
        // The retry arrives before the worker has written the original's reports: still a duplicate, still one report.
        using var server = new TestServer();
        var original = await server.PostSample("02_oru_valid_01.hl7", waitForProcessing: false);
        var retry = await server.PostSample("04_oru_duplicate_retry.hl7", waitForProcessing: false);

        Assert.Equal("AA", retry.AckCode);
        var row = server.Message(retry.MessageId)!;
        Assert.Equal("duplicate", (string)row.status);
        Assert.Equal(long.Parse(original.MessageId!), (long)row.duplicate_of);

        await server.WaitProcessed(original.MessageId!);
        Assert.Equal(1, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
    }

    [Fact]
    public async Task Healthz_reports_queue_depth()
    {
        using var server = new TestServer();
        await server.PostSample("02_oru_valid_01.hl7");

        using var response = await server.Client.GetAsync("/healthz");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("pending").GetInt32());
    }

    [Fact]
    public async Task Messages_queued_before_a_restart_are_completed_on_startup()
    {
        string dbPath;
        string id;
        using (var first = new TestServer { DeleteDatabaseOnDispose = false })
        {
            dbPath = first.DbPath;
            var posted = await first.PostSample("03_oru_valid_02.hl7");
            id = posted.MessageId!;
        }

        // Rewind the row to 'queued' — as if the process died after the ACK but before the worker wrote the reports.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM observations; DELETE FROM reports; UPDATE messages SET status = 'queued', processed_at = NULL WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        // "Restart" against the same database: the startup sweep must pick it up without any HTTP traffic.
        using var second = new TestServer(dbPath: dbPath);   // deletes the DB when disposed
        Assert.Equal("accepted", await second.WaitProcessed(id));
        Assert.Equal(1, second.Scalar<long>("SELECT COUNT(*) FROM reports"));
    }

    [Fact]
    public async Task A_burst_is_fully_drained_in_receipt_order()
    {
        using var server = new TestServer();
        const int n = 40;

        var posts = Enumerable.Range(0, n).Select(i => server.PostText(
            $"MSH|^~\\&|RIS|BURST|PH|PH|20260101120000||ORU^R01|B{i:D3}|P|2.5\r" +
            $"PID|1||P{i}^^^BURST^MR||DOE^JANE\r" +
            $"OBR|1|O{i}^RIS|A{i}^RIS|71020^CHEST^CPT|||20260101115500\r" +
            $"OBX|1|TX|71020^CHEST^CPT||Report {i}||||||F\r", waitForProcessing: false));
        var results = await Task.WhenAll(posts);

        Assert.All(results, r => Assert.Equal(200, r.StatusCode));
        Assert.All(results, r => Assert.Equal("AA", r.AckCode));
        foreach (var r in results)
        {
            Assert.Equal("accepted", await server.WaitProcessed(r.MessageId!));
        }

        Assert.Equal(n, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
        Assert.Equal(0, server.Scalar<long>("SELECT COUNT(*) FROM messages WHERE status = 'queued'"));
        // FIFO: processed_at never decreases with id
        var order = server.Query("SELECT id, processed_at FROM messages ORDER BY id").Select(r => (string)r.processed_at).ToList();
        Assert.Equal(order.OrderBy(x => x, StringComparer.Ordinal), order);
    }

    [Fact]
    public void Worker_drain_can_be_driven_directly()
    {
        using var server = new TestServer();
        var receiver = server.Services.GetRequiredService<MessageReceiver>();
        var worker = server.Services.GetRequiredService<ProcessingWorker>();
        var repository = server.Services.GetRequiredService<MessageRepository>();

        var receipt = receiver.Receive(TestServer.Sample("01_oru_valid_minimal.hl7"));
        Assert.Equal(Hl7Receiver.Hl7.AckCode.AA, receipt.AckCode);
        worker.Drain();   // whichever of us gets there first, the outcome is the same

        Assert.Equal(0, repository.CountPending());
        Assert.Equal("accepted", (string)server.Message(receipt.MessageId.ToString())!.status);
    }
}
