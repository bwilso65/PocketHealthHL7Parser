# Posts every sample message to the running server (docker compose up), prints the HTTP status + ACK for each,
# then (after the background worker has run) fetches each message's verdict from GET /messages/{id}.
# Usage: .\scripts\send-samples.ps1 [-BaseUrl http://localhost:8080] [-Json]
param(
    [string] $BaseUrl = "http://localhost:8080",
    [switch] $Json
)

$accept = if ($Json) { "application/json" } else { "text/plain" }
$samples = Join-Path $PSScriptRoot "..\samples"
$ids = @()

Get-ChildItem -Path $samples -Filter *.hl7 | Sort-Object Name | ForEach-Object {
    Write-Host ""
    Write-Host "=== $($_.Name) ===" -ForegroundColor Cyan
    # curl.exe ships with Windows 10+; --data-binary preserves the \r segment terminators exactly; -D - dumps headers.
    $out = (& curl.exe -sS -D - -X POST "$BaseUrl/messages" -H "Content-Type: text/plain" -H "Accept: $accept" --data-binary "@$($_.FullName)" -w "`n[HTTP %{http_code}]") -join "`n"
    $out = $out -replace "`r", "`n"
    $id = ($out -split "`n" | Where-Object { $_ -match '^(?i)x-message-id:\s*(\d+)' } | ForEach-Object { $Matches[1] } | Select-Object -First 1)
    # body = everything after the blank line that ends the headers
    $parts = $out -split "`n`n", 2
    if ($parts.Count -gt 1) { $parts[1] } else { $out }
    $script:ids += $id
}

Start-Sleep -Seconds 1

Write-Host ""
Write-Host "=== Verdicts (GET $BaseUrl/messages/{id}) ===" -ForegroundColor Cyan
foreach ($id in $ids) {
    if (-not $id) { continue }
    $m = Invoke-RestMethod -Uri "$BaseUrl/messages/$id"
    $line = "id=$id  status=$($m.status)"
    if ($m.rejection) { $line += "  code=$($m.rejection.code)" }
    if ($m.duplicateOf) { $line += "  duplicateOf=$($m.duplicateOf)" }
    if ($m.reports.Count -gt 0) { $line += "  accession=$($m.reports[0].accessionNumber)" }
    Write-Host $line
}
Write-Host ""
Write-Host "Details: curl $BaseUrl/messages/<id>    quarantine: curl '$BaseUrl/messages?status=rejected'    DB: scripts/show-db.sh"
