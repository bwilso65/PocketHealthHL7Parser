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
builder.Services.AddSingleton<MessageQueries>();

// HL7 pipeline pieces (stateless)
builder.Services.AddSingleton<Hl7Parser>();
builder.Services.AddSingleton<OruExtractor>();
builder.Services.AddSingleton<OruValidator>();
builder.Services.AddSingleton<IProviderProfileRegistry>(new ProviderProfileRegistry());
builder.Services.AddSingleton(sp => new AckBuilder(receivingApplication, receivingFacility, sp.GetRequiredService<TimeProvider>()));

// Receive (sync: validate + store + ACK) → queue → process (async: write reports)
builder.Services.AddSingleton<MessageEvaluator>();
builder.Services.AddSingleton<ProcessingQueue>();
builder.Services.AddSingleton<MessageReceiver>();
builder.Services.AddSingleton<MessageProcessor>();
builder.Services.AddSingleton<ProcessingWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingWorker>());

var app = builder.Build();

// Create the schema on startup so `docker compose up` on a fresh checkout just works.
app.Services.GetRequiredService<Database>().Initialize();
app.Logger.LogInformation("SQLite database at {DbPath}", dbPath);

app.MapGet("/", () => Results.Text(
    """
    HL7 ORU^R01 ingestion server

      POST /messages                 raw HL7 v2 message in the body (Content-Type: text/plain).
                                     200 + HL7 ACK once the bytes are stored: MSA-1 = AA (valid, queued) / AE / AR.
                                     Reports are written asynchronously (status queued -> accepted).
                                     JSON instead of the ACK with 'Accept: application/json'.
      GET  /messages/{id}            outcome of a message + extracted report(s)   (id from X-Message-Id / Location)
      GET  /messages/{id}/raw        the exact bytes received
      GET  /messages?controlId=&facility=&status=&limit=   search (newest first)
      GET  /healthz                  liveness + queue depth

    Or query SQLite directly: docker compose exec hl7-server sqlite3 /app/data/messages.db
    """, "text/plain"));
app.MapGet("/healthz", (MessageRepository repository) => Results.Ok(new { status = "ok", pending = repository.CountPending() }));
app.MapMessagesEndpoint();

app.Run();

// Exposes the implicit Program class to the test project (WebApplicationFactory<Program>).
public partial class Program { }
