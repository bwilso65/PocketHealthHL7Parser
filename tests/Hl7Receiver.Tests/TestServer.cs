using System.Net.Http.Headers;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Hl7Receiver.Tests;

/// <summary>
/// Boots the real application in-process against a throwaway SQLite file.
/// Each instance gets its own database so tests can run in parallel and inspect state independently.
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public string DbPath { get; }
    public HttpClient Client { get; }

    public TestServer(Action<IServiceCollection>? configureServices = null)
    {
        DbPath = Path.Combine(Path.GetTempPath(), "hl7receiver-tests", $"{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DB_PATH", DbPath);
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });

        Client = _factory.CreateClient();
    }

    // ---- sample fixtures -------------------------------------------------------------------------

    /// <summary>Path to a sample file copied into the test output by the csproj.</summary>
    public static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "samples", fileName);

    public static byte[] Sample(string fileName) => File.ReadAllBytes(SamplePath(fileName));

    // ---- HTTP helpers ----------------------------------------------------------------------------

    public sealed record PostResult(int StatusCode, string Body, string? MessageId)
    {
        /// <summary>MSA-1 from an HL7 ACK body ("AA", "AE", "AR"), or null if the body isn't an ACK.</summary>
        public string? AckCode
        {
            get
            {
                var msa = Body.Split('\r').FirstOrDefault(s => s.StartsWith("MSA|", StringComparison.Ordinal));
                return msa?.Split('|').ElementAtOrDefault(1);
            }
        }
    }

    public Task<PostResult> PostSample(string fileName, string? accept = null) => Post(Sample(fileName), accept);

    public Task<PostResult> PostText(string hl7, string? accept = null) => Post(Encoding.UTF8.GetBytes(hl7), accept);

    public async Task<PostResult> Post(byte[] body, string? accept = null)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/messages") { Content = content };
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.Headers.TryGetValues("X-Message-Id", out var ids);
        return new PostResult((int)response.StatusCode, text, ids?.FirstOrDefault());
    }

    // ---- DB helpers ------------------------------------------------------------------------------

    public IEnumerable<dynamic> Query(string sql, object? param = null)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();
        return connection.Query(sql, param).ToList();
    }

    public T Scalar<T>(string sql, object? param = null)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();
        return connection.ExecuteScalar<T>(sql, param)!;
    }

    public dynamic? Message(string? messageId) =>
        Query("SELECT * FROM messages WHERE id = @Id", new { Id = messageId }).SingleOrDefault();

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(DbPath)!, Path.GetFileName(DbPath) + "*"))
            {
                File.Delete(f);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup of temp DB files
        }
    }
}
