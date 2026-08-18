using Microsoft.AspNetCore.Mvc.Testing;

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

    public TestServer()
    {
        DbPath = Path.Combine(Path.GetTempPath(), "hl7receiver-tests", $"{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DB_PATH", DbPath);
        });

        Client = _factory.CreateClient();
    }

    /// <summary>Path to a sample file copied into the test output by the csproj.</summary>
    public static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "samples", fileName);

    public static byte[] Sample(string fileName) => File.ReadAllBytes(SamplePath(fileName));

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
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
