using Hl7Receiver.Hl7;
using Hl7Receiver.Http;
using Hl7Receiver.Ingestion;
using Hl7Receiver.Storage;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from environment variables (see docker-compose.yml), with local-dev defaults.
var port = builder.Configuration["PORT"] ?? "8080";
var dbPath = builder.Configuration["DB_PATH"] ?? "data/messages.db";
var receivingApplication = builder.Configuration["RECEIVING_APPLICATION"] ?? "POCKETHEALTH"; // MSH-3 of our ACKs
var receivingFacility = builder.Configuration["RECEIVING_FACILITY"] ?? "POCKETHEALTH";       // MSH-4 of our ACKs

// Honour PORT (from docker-compose) via Kestrel's HTTP_PORTS setting rather than UseUrls, so we don't
// fight the ASPNETCORE_HTTP_PORTS default baked into the aspnet base image.
builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, port);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new Database(dbPath));
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<Hl7Parser>();
builder.Services.AddSingleton<OruExtractor>();
builder.Services.AddSingleton<OruValidator>();
builder.Services.AddSingleton<IProviderProfileRegistry>(new ProviderProfileRegistry());
builder.Services.AddSingleton(sp => new AckBuilder(receivingApplication, receivingFacility, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IngestionService>();

var app = builder.Build();

// Create the schema on startup so `docker compose up` on a fresh checkout just works.
app.Services.GetRequiredService<Database>().Initialize();
app.Logger.LogInformation("SQLite database at {DbPath}", dbPath);

app.MapGet("/", () => Results.Text(
    """
    HL7 ORU^R01 ingestion server

      POST /messages   raw HL7 v2 message in the body (Content-Type: text/plain). Returns an HL7 ACK,
                       or JSON with 'Accept: application/json'.
      GET  /healthz    liveness

    Data: sqlite3 on the file at DB_PATH (see README for queries).
    """, "text/plain"));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapMessagesEndpoint();

app.Run();

// Exposes the implicit Program class to the test project (WebApplicationFactory<Program>).
public partial class Program { }
