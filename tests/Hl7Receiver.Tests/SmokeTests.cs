using System.Net;

namespace Hl7Receiver.Tests;

public class SmokeTests
{
    [Fact]
    public async Task Healthz_returns_ok_and_creates_database()
    {
        using var server = new TestServer();

        var response = await server.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(server.DbPath), "database file should be created on startup");
    }

    [Fact]
    public void Sample_fixtures_are_available_to_tests()
    {
        var samples = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.hl7");

        Assert.Equal(8, samples.Length);
    }
}
