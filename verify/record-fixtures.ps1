<#
.SYNOPSIS
Records golden fixtures (D12) by driving real traffic through a running Vessel and
exporting the captured wire bytes from vessel.db — so fixture bytes are wire-true by
construction (Vessel records its own fixtures).

For each case it writes request.json (the wire request body), response.raw (the exact wire
response — SSE/NDJSON chunk stream or non-streamed JSON), and meta.json. It then prints the
enriched fields (format/model/tokens/tok_per_sec/stop_reason) so you can hand-write
expected.json. Malformed/truncated cases are derived by hand from these (cut mid-event, cut
mid-UTF-8-codepoint, inject a garbage line) — never recorded.

Requires a running Vessel (dotnet run --project src/Vessel) with an Ollama backend, and the
project's build output (for the SQLite/zstd assemblies used to read vessel.db).

.EXAMPLE
./record-fixtures.ps1 -Model qwen2.5:1.5b
#>
[CmdletBinding()]
param(
    [string]$VesselUrl = "http://127.0.0.1:4550",
    [string]$Model = "qwen2.5:1.5b",
    [string]$DbPath = "",
    [string]$OutRoot = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib-db.ps1")

if ([string]::IsNullOrEmpty($OutRoot)) { $OutRoot = Join-Path $repoRoot "tests/Vessel.Tests/Fixtures" }
if ([string]::IsNullOrEmpty($DbPath)) {
    foreach ($candidate in @(
        (Join-Path $repoRoot "src/Vessel/bin/Debug/net10.0/vessel.db"),
        (Join-Path $repoRoot "src/Vessel/bin/Release/net10.0/vessel.db"))) {
        if (Test-Path $candidate) { $DbPath = $candidate; break }
    }
}
if ([string]::IsNullOrEmpty($DbPath) -or -not (Test-Path $DbPath)) {
    throw "vessel.db not found. Start Vessel and pass -DbPath if it isn't under the dev-run layout."
}
if (-not (Import-VesselSqlite -RepoRoot $repoRoot)) { throw "could not load SQLite assemblies from the build output." }

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(10)

function Send-Through-Vessel {
    param([string]$Path, [string]$BodyJson, [string]$Tag)
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$VesselUrl$Path")
    $req.Content = New-Object System.Net.Http.StringContent($BodyJson, [Text.Encoding]::UTF8, "application/json")
    [void]$req.Headers.TryAddWithoutValidation("X-Vessel-Tags", $Tag)
    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    [void]$resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $status = [int]$resp.StatusCode
    $contentType = if ($resp.Content.Headers.ContentType) { $resp.Content.Headers.ContentType.ToString() } else { "" }
    $resp.Dispose(); $req.Dispose()
    return @{ Status = $status; ContentType = $contentType }
}

function Record-Case {
    param([string]$Format, [string]$Case, [string]$Path, [string]$BodyJson)

    $tag = "rec" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
    Write-Host "== $Format/$Case" -ForegroundColor Cyan
    $sent = Send-Through-Vessel -Path $Path -BodyJson $BodyJson -Tag $tag

    $row = $null
    for ($i = 0; $i -lt 40; $i++) {
        $rows = Get-VesselRows -DbPath $DbPath -Sql @"
SELECT format, model, tokens_in, tokens_out, tok_per_sec, stop_reason, streamed,
       request_body, response_body, response_raw
FROM requests WHERE tags LIKE '%$tag%' ORDER BY id DESC LIMIT 1
"@
        if ($rows.Count -gt 0) { $row = $rows[0]; break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $row) { Write-Host "   FAIL: no captured row" -ForegroundColor Red; return }

    $dir = Join-Path $OutRoot (Join-Path $Format $Case)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    [IO.File]::WriteAllText((Join-Path $dir "request.json"), $BodyJson)

    $streamed = [long]$row.streamed -ne 0
    $rawColumn = if ($streamed) { $row.response_raw } else { $row.response_body }
    $responseBytes = Expand-VesselBody ([byte[]]$rawColumn)
    [IO.File]::WriteAllBytes((Join-Path $dir "response.raw"), $responseBytes)

    $meta = [ordered]@{
        path                = $Path
        status              = $sent.Status
        responseContentType = ($sent.ContentType -split ';')[0].Trim()
        backend             = "ollama"
        backendType         = "ollama"
    }
    ($meta | ConvertTo-Json -Compress) | Set-Content -Path (Join-Path $dir "meta.json") -Encoding utf8 -NoNewline

    $tps = if ($null -ne $row.tok_per_sec) { [Math]::Round([double]$row.tok_per_sec, 2) } else { "null" }
    Write-Host "   wrote request.json + response.raw + meta.json" -ForegroundColor Green
    Write-Host "   enriched: format=$($row.format) model=$($row.model) in=$($row.tokens_in) out=$($row.tokens_out) tok/s=$tps stop=$($row.stop_reason)"
    Write-Host "   -> hand-write expected.json from these values (and the synthesized response_body for streamed cases)."
}

$msg = '{"role":"user","content":"Reply with exactly the word: Hello"}'
$opts = '"options":{"seed":42,"temperature":0}'

Record-Case -Format "ollama-chat" -Case "recorded-nonstreamed" -Path "/api/chat" `
    -BodyJson "{`"model`":`"$Model`",`"messages`":[$msg],`"stream`":false,$opts}"
Record-Case -Format "ollama-chat" -Case "recorded-streamed" -Path "/api/chat" `
    -BodyJson "{`"model`":`"$Model`",`"messages`":[$msg],`"stream`":true,$opts}"
Record-Case -Format "ollama-generate" -Case "recorded-nonstreamed" -Path "/api/generate" `
    -BodyJson "{`"model`":`"$Model`",`"prompt`":`"Reply with exactly the word: Hello`",`"stream`":false,$opts}"
Record-Case -Format "ollama-generate" -Case "recorded-streamed" -Path "/api/generate" `
    -BodyJson "{`"model`":`"$Model`",`"prompt`":`"Reply with exactly the word: Hello`",`"stream`":true,$opts}"

Write-Host ""
Write-Host "Recorded 4 cases under $OutRoot. Fill in each expected.json, then run: dotnet test" -ForegroundColor Green
