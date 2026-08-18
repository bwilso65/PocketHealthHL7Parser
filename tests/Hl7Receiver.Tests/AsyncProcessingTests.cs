using System.Net;
using System.Text.Json;
using Hl7Receiver.Ingestion;
using Hl7Receiver.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Hl7Receiver.Tests;

/// <summary>Receipt is synchronous; the verdict is not. These pin down the seam between the two.</summary>
public class AsyncProcessingTests
{
    [Fact]
    public async Task Post_returns_a_receipt_before_the_verdict_exists()
    {
        using var server = new TestServer();

        var result = await server.PostSample("07_malformed_truncated.hl7", waitForProcessing: false);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("AA", result.AckCode);
        // At this instant the worker may or may not have run; either way the row exists and has the raw bytes.
        var row = server.Message(result.MessageId)!;
        Assert.Contains((string)row.status, new[] { "received", "rejected" });
        Assert.Equal(TestServer.Sample("07_malformed_truncated.hl7"), (byte[])row.raw_message);

        Assert.Equal("rejected", await server.WaitProcessed(result.MessageId!));
        Assert.NotNull(server.Message(result.MessageId)!.processed_at);
    }

    [Fact]
    public async Task Get_shows_received_then_the_verdict()
    {
        using var server = new TestServer();
        var posted = await server.PostSample("02_oru_valid_01.hl7", waitForProcessing: false);

        await server.WaitProcessed(posted.MessageId!);
        using var response = await server.Client.GetAsync($"/messages/{posted.MessageId}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("accepted", json.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("processedAt").ValueKind);
        Assert.Equal(1, json.RootElement.GetProperty("reports").GetArrayLength());
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
    public async Task Messages_received_before_a_restart_are_processed_on_startup()
    {
        // Simulate: bytes were stored, then the process died before the worker got to them.
        string dbPath;
        string id;
        using (var first = new TestServer { DeleteDatabaseOnDispose = false })
        {
            dbPath = first.DbPath;
            var posted = await first.PostSample("03_oru_valid_02.hl7");
            id = posted.MessageId!;
        }

        // Rewind the row to 'received' — as if the process died between INSERT and verdict.

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM observations; DELETE FROM reports; UPDATE messages SET status = 'received', processed_at = NULL WHERE id = @id";
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
        foreach (var r in results)
        {
            Assert.Equal("accepted", await server.WaitProcessed(r.MessageId!));
        }

        Assert.Equal(n, server.Scalar<long>("SELECT COUNT(*) FROM reports"));
        Assert.Equal(0, server.Scalar<long>("SELECT COUNT(*) FROM messages WHERE status = 'received'"));
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

        var id = receiver.Receive(TestServer.Sample("01_oru_valid_minimal.hl7")).MessageId;
        worker.Drain();   // whichever of us gets there first, the outcome is the same

        Assert.Equal(0, repository.CountPending());
        Assert.Equal("accepted", (string)server.Message(id.ToString())!.status);
    }
}
