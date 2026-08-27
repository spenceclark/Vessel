<#
.SYNOPSIS
Byte-identical verification harness: sends each request twice - direct to the backend
and via Vessel - and compares status, body bytes, and headers, plus a first-byte
latency delta (rough vessel_overhead preview).

Streamed responses are compared as the concatenated byte sequence (chunk boundaries are
legitimately not preserved by re-chunking). Real LLM backends stamp per-call volatile
fields (ids, timestamps, durations) into responses, so when the raw bytes differ the
comparison falls back to a normalized compare that masks exactly those fields - the
generated content itself must still be identical (requests pin seed / temperature 0).

.EXAMPLE
./verify.ps1                          # against local Ollama via local Vessel
./verify.ps1 -Model llama3.2
./verify.ps1 -OpenAI -Anthropic      # adds live-API cases (needs OPENAI_API_KEY / ANTHROPIC_API_KEY)
#>
[CmdletBinding()]
param(
    [string]$VesselUrl = "http://127.0.0.1:4550",
    [string]$BackendUrl = "http://localhost:11434",
    [string]$Model = "",
    [switch]$OpenAI,
    [switch]$Anthropic,
    [string]$OpenAIModel = "gpt-4o-mini",
    [string]$AnthropicModel = "claude-haiku-4-5-20251001"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(10)

$script:Failures = 0
$script:Overheads = @()

function Send-Raw {
    param([string]$Url, [string]$Method = "POST", $BodyJson = $null, $Headers = @{})

    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($null -ne $BodyJson) {
        $req.Content = New-Object System.Net.Http.StringContent($BodyJson, [Text.Encoding]::UTF8, "application/json")
    }
    foreach ($k in $Headers.Keys) { [void]$req.Headers.TryAddWithoutValidation($k, $Headers[$k]) }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()

    $ms = New-Object System.IO.MemoryStream
    $buf = New-Object byte[] 65536
    $firstByteMs = $null
    while (($n = $stream.Read($buf, 0, $buf.Length)) -gt 0) {
        if ($null -eq $firstByteMs) { $firstByteMs = $sw.Elapsed.TotalMilliseconds }
        $ms.Write($buf, 0, $n)
    }

    $headersOut = @{}
    foreach ($h in $resp.Headers) { $headersOut[$h.Key] = ($h.Value -join ",") }
    foreach ($h in $resp.Content.Headers) { $headersOut[$h.Key] = ($h.Value -join ",") }

    $result = [pscustomobject]@{
        Status      = [int]$resp.StatusCode
        Headers     = $headersOut
        Bytes       = $ms.ToArray()
        FirstByteMs = if ($null -ne $firstByteMs) { $firstByteMs } else { $sw.Elapsed.TotalMilliseconds }
        TotalMs     = $sw.Elapsed.TotalMilliseconds
    }
    $resp.Dispose()
    $req.Dispose()
    return $result
}

# Masks per-call volatile fields so deterministic content can be compared across two
# real generations. Everything else - including the generated text and token counts -
# must match exactly.
function Get-NormalizedText {
    param([byte[]]$Bytes)
    $text = [Text.Encoding]::UTF8.GetString($Bytes)
    $text = $text -replace '"(id|system_fingerprint|created_at|request_id)"\s*:\s*"[^"]*"', '"$1":"X"'
    $text = $text -replace '"(created|total_duration|load_duration|prompt_eval_duration|eval_duration)"\s*:\s*\d+', '"$1":0'
    return $text
}

$IgnoredHeaders = @(
    "Date", "Server", "Transfer-Encoding", "Connection", "Keep-Alive", "Alt-Svc",
    # Live APIs: per-call rate-limit accounting and tracing headers
    "X-Request-Id", "Request-Id", "CF-RAY", "cf-cache-status", "Set-Cookie",
    "openai-processing-ms", "openai-organization", "openai-version", "openai-project",
    "anthropic-organization-id"
)

function Test-HeadersMatch {
    param($Direct, $Proxied, [bool]$BytesIdentical)
    $problems = @()
    $keys = @($Direct.Headers.Keys) + @($Proxied.Headers.Keys) | Sort-Object -Unique
    foreach ($k in $keys) {
        if ($IgnoredHeaders -contains $k) { continue }
        if ($k -match '^(x-ratelimit|anthropic-ratelimit)') { continue }
        if ($k -eq "Content-Length" -and -not $BytesIdentical) {
            # Two generations with different volatile-field digit counts legitimately
            # differ in length; assert each response is self-consistent instead.
            foreach ($side in @(@("direct", $Direct), @("vessel", $Proxied))) {
                $declared = $side[1].Headers[$k]
                if ($null -ne $declared -and [long]$declared -ne $side[1].Bytes.Length) {
                    $problems += "header 'Content-Length' ($($side[0])): declared $declared, actual $($side[1].Bytes.Length)"
                }
            }
            continue
        }
        $d = $Direct.Headers[$k]
        $p = $Proxied.Headers[$k]
        if ($d -ne $p) { $problems += "header '$k': direct='$d' vessel='$p'" }
    }
    return $problems
}

function Compare-Case {
    param([string]$Name, [string]$DirectBase, [string]$VesselBase, [string]$Path, [string]$BodyJson, $Headers = @{})

    Write-Host "== $Name" -ForegroundColor Cyan
    try {
        $direct = Send-Raw -Url "$DirectBase$Path" -BodyJson $BodyJson -Headers $Headers
        $proxied = Send-Raw -Url "$VesselBase$Path" -BodyJson $BodyJson -Headers $Headers
    }
    catch {
        Write-Host "   FAIL: request error: $_" -ForegroundColor Red
        $script:Failures++
        return
    }

    $ok = $true
    $bytesIdentical = $false

    if ($direct.Status -ne $proxied.Status) {
        Write-Host "   FAIL: status direct=$($direct.Status) vessel=$($proxied.Status)" -ForegroundColor Red
        $ok = $false
    }

    if ($ok) {
        if ([System.Linq.Enumerable]::SequenceEqual($direct.Bytes, $proxied.Bytes)) {
            $bytesIdentical = $true
            Write-Host "   body: byte-identical ($($direct.Bytes.Length) bytes)" -ForegroundColor Green
        }
        else {
            $dn = Get-NormalizedText $direct.Bytes
            $pn = Get-NormalizedText $proxied.Bytes
            if ($dn -eq $pn) {
                Write-Host "   body: identical after masking per-call volatile fields (ids/timestamps/durations)" -ForegroundColor Green
            }
            else {
                # Two generations can legitimately differ where determinism isn't
                # achievable; show where so a human can judge.
                $dLines = $dn -split "`n"
                $pLines = $pn -split "`n"
                $firstDiff = 0
                while ($firstDiff -lt [Math]::Min($dLines.Count, $pLines.Count) -and $dLines[$firstDiff] -eq $pLines[$firstDiff]) { $firstDiff++ }
                Write-Host "   FAIL: body differs after normalization (line $($firstDiff + 1)):" -ForegroundColor Red
                Write-Host "     direct: $($dLines[$firstDiff])"
                Write-Host "     vessel: $($pLines[$firstDiff])"
                $ok = $false
            }
        }

        $headerProblems = Test-HeadersMatch -Direct $direct -Proxied $proxied -BytesIdentical $bytesIdentical
        if ($headerProblems.Count -gt 0) {
            foreach ($p in $headerProblems) { Write-Host "   FAIL: $p" -ForegroundColor Red }
            $ok = $false
        }
    }

    $delta = $proxied.FirstByteMs - $direct.FirstByteMs
    $script:Overheads += $delta
    Write-Host ("   first byte: direct {0:n1} ms, vessel {1:n1} ms (delta {2:n1} ms)" -f $direct.FirstByteMs, $proxied.FirstByteMs, $delta)

    if (-not $ok) { $script:Failures++ }
}

# --- Preflight ------------------------------------------------------------------------

try {
    $status = Send-Raw -Url "$VesselUrl/vessel/api/status" -Method "GET"
    if ($status.Status -ne 200) { throw "status $($status.Status)" }
    Write-Host "Vessel is up at $VesselUrl" -ForegroundColor Green
}
catch {
    Write-Error "Vessel is not reachable at $VesselUrl - start it first (dotnet run --project src/Vessel). $_"
    exit 1
}

if ([string]::IsNullOrEmpty($Model)) {
    try {
        $tags = Send-Raw -Url "$BackendUrl/api/tags" -Method "GET"
        $Model = ((([Text.Encoding]::UTF8.GetString($tags.Bytes)) | ConvertFrom-Json).models | Select-Object -First 1).name
        if ([string]::IsNullOrEmpty($Model)) { throw "no models installed" }
        Write-Host "Using model: $Model (first from /api/tags)"
    }
    catch {
        Write-Error "Could not auto-pick a model from $BackendUrl/api/tags - pass -Model. $_"
        exit 1
    }
}

# --- Ollama cases (deterministic: seed + temperature 0) -------------------------------

$msg = '{"role":"user","content":"Reply with exactly the word: hello"}'
$ollamaOpts = '"options":{"seed":42,"temperature":0}'

Compare-Case -Name "ollama native /api/chat (non-streamed)" `
    -DirectBase $BackendUrl -VesselBase $VesselUrl -Path "/api/chat" `
    -BodyJson "{`"model`":`"$Model`",`"messages`":[$msg],`"stream`":false,$ollamaOpts}"

Compare-Case -Name "ollama native /api/chat (NDJSON streamed)" `
    -DirectBase $BackendUrl -VesselBase $VesselUrl -Path "/api/chat" `
    -BodyJson "{`"model`":`"$Model`",`"messages`":[$msg],`"stream`":true,$ollamaOpts}"

Compare-Case -Name "ollama /v1/chat/completions (non-streamed)" `
    -DirectBase $BackendUrl -VesselBase $VesselUrl -Path "/v1/chat/completions" `
    -BodyJson "{`"model`":`"$Model`",`"messages`":[$msg],`"stream`":false,`"seed`":42,`"temperature`":0}"

Compare-Case -Name "ollama /v1/chat/completions (SSE streamed)" `
    -DirectBase $BackendUrl -VesselBase $VesselUrl -Path "/v1/chat/completions" `
    -BodyJson "{`"model`":`"$Model`",`"messages`":[$msg],`"stream`":true,`"seed`":42,`"temperature`":0}"

# --- Live cases (opt-in; compared with volatile-field masking) ------------------------

if ($OpenAI) {
    if (-not $env:OPENAI_API_KEY) { Write-Error "-OpenAI requires OPENAI_API_KEY"; exit 1 }
    Compare-Case -Name "OpenAI live /v1/chat/completions via /b/openai" `
        -DirectBase "https://api.openai.com" -VesselBase "$VesselUrl/b/openai" -Path "/v1/chat/completions" `
        -BodyJson "{`"model`":`"$OpenAIModel`",`"messages`":[$msg],`"stream`":false,`"seed`":42,`"temperature`":0}" `
        -Headers @{ Authorization = "Bearer $($env:OPENAI_API_KEY)" }
}

if ($Anthropic) {
    if (-not $env:ANTHROPIC_API_KEY) { Write-Error "-Anthropic requires ANTHROPIC_API_KEY"; exit 1 }
    Compare-Case -Name "Anthropic live /v1/messages via /b/anthropic" `
        -DirectBase "https://api.anthropic.com" -VesselBase "$VesselUrl/b/anthropic" -Path "/v1/messages" `
        -BodyJson "{`"model`":`"$AnthropicModel`",`"max_tokens`":16,`"temperature`":0,`"messages`":[$msg]}" `
        -Headers @{ "x-api-key" = $env:ANTHROPIC_API_KEY; "anthropic-version" = "2023-06-01" }
}

# --- Summary --------------------------------------------------------------------------

Write-Host ""
if ($script:Overheads.Count -gt 0) {
    $avg = ($script:Overheads | Measure-Object -Average).Average
    Write-Host ("vessel_overhead preview (first-byte delta): avg {0:n1} ms over {1} cases - expect low single digits for local backends" -f $avg, $script:Overheads.Count)
}

if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) case(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "All cases passed" -ForegroundColor Green
exit 0
