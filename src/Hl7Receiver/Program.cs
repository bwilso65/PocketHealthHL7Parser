using Hl7Receiver.Storage;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from environment variables (see docker-compose.yml), with local-dev defaults.
var port = builder.Configuration["PORT"] ?? "8080";
var dbPath = builder.Configuration["DB_PATH"] ?? "data/messages.db";

// Honour PORT (from docker-compose) via Kestrel's HTTP_PORTS setting rather than UseUrls, so we don't
// fight the ASPNETCORE_HTTP_PORTS default baked into the aspnet base image.
builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, port);
builder.Services.AddSingleton(new Database(dbPath));

var app = builder.Build();

// Create the schema on startup so `docker compose up` on a fresh checkout just works.
app.Services.GetRequiredService<Database>().Initialize();
app.Logger.LogInformation("SQLite database at {DbPath}", dbPath);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposes the implicit Program class to the test project (WebApplicationFactory<Program>).
public partial class Program { }
