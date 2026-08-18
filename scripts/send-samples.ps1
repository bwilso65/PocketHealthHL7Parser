# Posts every sample message to the running server (docker compose up) and prints the HTTP status and ACK.
# Usage: .\scripts\send-samples.ps1 [-BaseUrl http://localhost:8080] [-Json]
param(
    [string] $BaseUrl = "http://localhost:8080",
    [switch] $Json
)

$accept = if ($Json) { "application/json" } else { "text/plain" }
$samples = Join-Path $PSScriptRoot "..\samples"

Get-ChildItem -Path $samples -Filter *.hl7 | Sort-Object Name | ForEach-Object {
    Write-Host ""
    Write-Host "=== $($_.Name) ===" -ForegroundColor Cyan
    # curl.exe ships with Windows 10+; --data-binary preserves the \r segment terminators exactly.
    $out = & curl.exe -sS -X POST "$BaseUrl/messages" -H "Content-Type: text/plain" -H "Accept: $accept" --data-binary "@$($_.FullName)" -w "`n[HTTP %{http_code}]`n"
    ($out -join "`n") -replace "`r", "`n"
}
