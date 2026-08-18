# HL7 ORU ingestion server — C# / .NET 10 (LTS)
#
# Stages:
#   build    - restore + compile everything (src + tests)
#   test     - runs `dotnet test` (used by the `tests` compose profile; not part of the runtime image)
#   publish  - framework-dependent publish of the server
#   runtime  - small ASP.NET runtime image + sqlite CLI (so the DB can be inspected inside the container)

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first so NuGet downloads are cached as their own layer.
COPY Hl7Receiver.slnx ./
COPY src/Hl7Receiver/Hl7Receiver.csproj src/Hl7Receiver/
COPY tests/Hl7Receiver.Tests/Hl7Receiver.Tests.csproj tests/Hl7Receiver.Tests/
RUN dotnet restore Hl7Receiver.slnx

COPY . .
RUN dotnet build Hl7Receiver.slnx -c Release --no-restore

# ---------- test ----------
FROM build AS test
CMD ["dotnet", "test", "Hl7Receiver.slnx", "-c", "Release", "--no-build", "--logger", "console;verbosity=normal"]

# ---------- publish ----------
FROM build AS publish
RUN dotnet publish src/Hl7Receiver/Hl7Receiver.csproj -c Release --no-build -o /app/publish

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
# sqlite: lets reviewers run `docker compose exec hl7-server sqlite3 /app/data/messages.db`
# curl:   used by the container HEALTHCHECK
RUN apk add --no-cache sqlite curl
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/data
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -fsS http://localhost:${PORT:-8080}/healthz || exit 1
ENTRYPOINT ["dotnet", "Hl7Receiver.dll"]
