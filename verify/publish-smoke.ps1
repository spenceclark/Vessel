<#
.SYNOPSIS
Single-file publish smoke test (CI-runnable): publishes a self-contained single-file
win-x64 build, runs the produced exe from an empty directory, and asserts first-run
config creation, /vessel/api/status, proxying, and the unknown-backend error path.

.EXAMPLE
./publish-smoke.ps1            # untrimmed (the shipping configuration)
./publish-smoke.ps1 -Trimmed   # data-gathering for the phase-6 trimming decision
#>
[CmdletBinding()]
param(
    [switch]$Trimmed,
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/Vessel"

function Get-FreePort {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Wait-ForStatus {
    param([string]$BaseUrl, [int]$TimeoutSec = 15)
    $client = New-Object System.Net.Http.HttpClient
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = $client.GetAsync("$BaseUrl/vessel/api/status").GetAwaiter().GetResult()
            if ([int]$resp.StatusCode -eq 200) {
                $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $client.Dispose()
                return $body
            }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    $client.Dispose()
    throw "Vessel did not become ready at $BaseUrl within $TimeoutSec s"
}

# --- Publish --------------------------------------------------------------------------

$publishArgs = @("publish", $project, "-c", "Release", "-r", $Rid, "--self-contained", "-p:PublishSingleFile=true")
if ($Trimmed) { $publishArgs += "-p:PublishTrimmed=true" }

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $project "bin/Release/net10.0/$Rid/publish/Vessel.exe"
if (-not (Test-Path $exe)) { throw "expected single-file exe not found: $exe" }
$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Published: $exe ($sizeMb MB)" -ForegroundColor Green

# --- Run from an empty directory ------------------------------------------------------

$work = Join-Path ([IO.Path]::GetTempPath()) ("vessel-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item $exe (Join-Path $work "Vessel.exe")

$vesselProc = $null
$stubListener = $null
$failures = 0

try {
    # First-run: config is created next to the exe with the Ollama default. Use --config
    # so the smoke test doesn't depend on port 4550 (or a local Ollama) being free.
    $port = Get-FreePort
    $stubPort = Get-FreePort
    $configPath = Join-Path $work "vessel.json"

    $vesselProc = Start-Process -FilePath (Join-Path $work "Vessel.exe") -WorkingDirectory $work -PassThru `
        -RedirectStandardOutput (Join-Path $work "stdout.log") -RedirectStandardError (Join-Path $work "stderr.log") `
        -ArgumentList @("--config", "`"$configPath`"") -WindowStyle Hidden

    Start-Sleep -Seconds 2
    if (-not (Test-Path $configPath)) {
        Write-Host "FAIL: first run did not create vessel.json" -ForegroundColor Red
        $failures++
    }
    else {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($cfg.defaultBackend -ne "ollama" -or $cfg.backends.ollama.baseUrl -ne "http://localhost:11434") {
            Write-Host "FAIL: default config content unexpected: $(Get-Content $configPath -Raw)" -ForegroundColor Red
            $failures++
        }
        else {
            Write-Host "PASS: first run created default config (ollama backend)" -ForegroundColor Green
        }
    }

    if (-not $vesselProc.HasExited) { Stop-Process -Id $vesselProc.Id -Force }
    $vesselProc = $null

    # Real run: default backend pointed at a local HttpListener stub, on a free port.
    Set-Content -Path $configPath -Encoding utf8 -Value @"
{
  "listen": "127.0.0.1:$port",
  "defaultBackend": "stub",
  "backends": { "stub": { "baseUrl": "http://127.0.0.1:$stubPort" } }
}
"@

    $stubListener = New-Object System.Net.HttpListener
    $stubListener.Prefixes.Add("http://127.0.0.1:$stubPort/")
    $stubListener.Start()

    $vesselProc = Start-Process -FilePath (Join-Path $work "Vessel.exe") -WorkingDirectory $work -PassThru `
        -RedirectStandardOutput (Join-Path $work "stdout2.log") -RedirectStandardError (Join-Path $work "stderr2.log") `
        -ArgumentList @("--config", "`"$configPath`"") -WindowStyle Hidden

    $baseUrl = "http://127.0.0.1:$port"
    $statusBody = Wait-ForStatus -BaseUrl $baseUrl
    if ($statusBody -notmatch '"stub"') {
        Write-Host "FAIL: /vessel/api/status does not list the stub backend: $statusBody" -ForegroundColor Red
        $failures++
    }
    else {
        Write-Host "PASS: /vessel/api/status responds and lists backends" -ForegroundColor Green
    }

    # Proxying: request via Vessel must reach the stub and the stub's response must
    # come back intact.
    $client = New-Object System.Net.Http.HttpClient
    $pending = $client.GetAsync("$baseUrl/smoke/echo?x=1")
    $ctx = $stubListener.GetContext()
    $receivedPath = $ctx.Request.Url.PathAndQuery
    $responseBytes = [Text.Encoding]::UTF8.GetBytes("smoke-ok")
    $ctx.Response.StatusCode = 200
    $ctx.Response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
    $ctx.Response.Close()

    $resp = $pending.GetAwaiter().GetResult()
    $respBody = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($receivedPath -ne "/smoke/echo?x=1" -or $respBody -ne "smoke-ok" -or [int]$resp.StatusCode -ne 200) {
        Write-Host "FAIL: proxying broken - path='$receivedPath' status=$([int]$resp.StatusCode) body='$respBody'" -ForegroundColor Red
        $failures++
    }
    else {
        Write-Host "PASS: proxying works (path + body intact through the exe)" -ForegroundColor Green
    }

    # Unknown backend error path.
    $resp404 = $client.GetAsync("$baseUrl/b/nope/x").GetAwaiter().GetResult()
    $body404 = $resp404.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ([int]$resp404.StatusCode -ne 404 -or $body404 -notmatch '"unknown_backend"') {
        Write-Host "FAIL: unknown-backend path - status=$([int]$resp404.StatusCode) body=$body404" -ForegroundColor Red
        $failures++
    }
    else {
        Write-Host "PASS: unknown backend -> 404 unknown_backend" -ForegroundColor Green
    }

    # Phase 3 D1: the frontend was built and embedded as part of this publish (Node is on
    # PATH in this environment) - /vessel/ must serve the real SPA shell, not the
    # no-frontend-embedded placeholder, and its bundled JS asset must be reachable too.
    $uiResp = $client.GetAsync("$baseUrl/vessel/").GetAwaiter().GetResult()
    $uiBody = $uiResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ([int]$uiResp.StatusCode -ne 200 -or $uiBody -notmatch '<title>Vessel</title>' -or $uiBody -match 'not built into this binary') {
        Write-Host "FAIL: /vessel/ did not serve the embedded UI - status=$([int]$uiResp.StatusCode)" -ForegroundColor Red
        $failures++
    }
    else {
        Write-Host "PASS: /vessel/ serves the embedded SPA shell" -ForegroundColor Green

        if ($uiBody -match 'src="(/vessel/assets/[^"]+\.js)"') {
            $assetUrl = "$baseUrl$($Matches[1])"
            $assetResp = $client.GetAsync($assetUrl).GetAwaiter().GetResult()
            if ([int]$assetResp.StatusCode -ne 200) {
                Write-Host "FAIL: embedded UI asset $assetUrl -> $([int]$assetResp.StatusCode)" -ForegroundColor Red
                $failures++
            }
            else {
                Write-Host "PASS: embedded UI asset $assetUrl loads" -ForegroundColor Green
            }
        }
        else {
            Write-Host "FAIL: could not find a bundled <script src> in /vessel/ to verify" -ForegroundColor Red
            $failures++
        }
    }

    $client.Dispose()
}
finally {
    if ($null -ne $vesselProc -and -not $vesselProc.HasExited) { Stop-Process -Id $vesselProc.Id -Force }
    if ($null -ne $stubListener) { $stubListener.Stop(); $stubListener.Close() }
    Start-Sleep -Milliseconds 300
    try { Remove-Item -Recurse -Force $work } catch { Write-Warning "could not clean $work" }
}

if ($failures -gt 0) {
    Write-Host "$failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "Publish smoke test passed ($(if ($Trimmed) { 'trimmed' } else { 'untrimmed' }), $Rid, $sizeMb MB)" -ForegroundColor Green
exit 0
