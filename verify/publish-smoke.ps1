<#
.SYNOPSIS
Single-file publish smoke test (CI-runnable): publishes a self-contained single-file
win-x64 build **from a clean source copy carrying no build outputs**, runs the produced
exe from an empty directory, and asserts first-run config creation, /vessel/api/status,
proxying, the unknown-backend error path, and the embedded SPA + its hashed asset.

R01: publishing from the working tree let a stale frontend/dist mask a broken build
order — the gate passed while a genuinely clean publish embedded nothing. The copy is
built from `git ls-files --cached --others --exclude-standard`, i.e. tracked files plus
untracked-but-not-ignored ones: everything a fresh clone would compile, minus the
ignored build outputs (frontend/dist, node_modules, bin, obj). Untracked-not-ignored
files are included deliberately so the check works on a working tree with uncommitted
work, which the house rules require (AGENTS.md: never commit).

.EXAMPLE
./publish-smoke.ps1            # untrimmed (the shipping configuration)
./publish-smoke.ps1 -Trimmed   # data-gathering for the phase-6 trimming decision
./publish-smoke.ps1 -InPlace   # publish from the working tree (faster; skips the R01 gate)
#>
[CmdletBinding()]
param(
    [switch]$Trimmed,
    [switch]$InPlace,
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$repoRoot = Split-Path -Parent $PSScriptRoot

function New-CleanSourceCopy {
    param([string]$RepoRoot)

    $dest = Join-Path ([IO.Path]::GetTempPath()) ("vessel-clean-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $dest | Out-Null

    Push-Location $RepoRoot
    try {
        $files = & git ls-files --cached --others --exclude-standard
        if ($LASTEXITCODE -ne 0) { throw "git ls-files failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }

    $copied = 0
    foreach ($rel in $files) {
        if ([string]::IsNullOrWhiteSpace($rel)) { continue }
        $src = Join-Path $RepoRoot $rel
        # Tracked-but-deleted paths are listed and simply have nothing to copy.
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { continue }
        $dst = Join-Path $dest $rel
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
        Copy-Item -LiteralPath $src -Destination $dst
        $copied++
    }

    # Fail loudly rather than "successfully" publishing an empty tree.
    foreach ($mustExist in @("src/Vessel/Vessel.csproj", "frontend/package.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $dest $mustExist))) {
            throw "clean source copy is missing $mustExist - refusing to publish from it"
        }
    }
    foreach ($mustNotExist in @("frontend/dist", "frontend/node_modules", "src/Vessel/bin", "src/Vessel/obj")) {
        if (Test-Path -LiteralPath (Join-Path $dest $mustNotExist)) {
            throw "clean source copy unexpectedly contains build output $mustNotExist"
        }
    }

    Write-Host "Clean source copy: $dest ($copied files, no dist/bin/obj/node_modules)" -ForegroundColor Cyan
    return $dest
}

$cleanRoot = $null
if ($InPlace) {
    Write-Host "-InPlace: publishing from the working tree (R01 clean-tree gate SKIPPED)" -ForegroundColor Yellow
    $sourceRoot = $repoRoot
}
else {
    $cleanRoot = New-CleanSourceCopy -RepoRoot $repoRoot
    $sourceRoot = $cleanRoot
}
$project = Join-Path $sourceRoot "src/Vessel"

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

# R01: assert the embedded UI is actually *in the artifact*, before any HTTP check. The
# HTTP checks below can only fail after a successful launch; this one localizes a broken
# build order to the build, and catches it even if the process fails to start. The
# intermediate Vessel.dll is the assembly whose manifest resources were compiled in (the
# single-file exe bundles it).
$builtDll = Join-Path $project "bin/Release/net10.0/$Rid/Vessel.dll"
if (-not (Test-Path $builtDll)) { throw "expected built assembly not found: $builtDll" }
$dllBytes = [IO.File]::ReadAllBytes($builtDll)
$dllText = [Text.Encoding]::UTF8.GetString($dllBytes)
foreach ($resource in @("vessel-ui/index.html", "vessel-ui/assets")) {
    if ($dllText -notlike "*$resource*") {
        throw "published assembly embeds no '$resource' - the frontend was not built before resource collection (R01)"
    }
}
Write-Host "PASS: published assembly embeds the frontend (vessel-ui/index.html + assets)" -ForegroundColor Green

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
    if ($null -ne $cleanRoot) {
        try { Remove-Item -Recurse -Force $cleanRoot } catch { Write-Warning "could not clean $cleanRoot" }
    }
}

if ($failures -gt 0) {
    Write-Host "$failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "Publish smoke test passed ($(if ($Trimmed) { 'trimmed' } else { 'untrimmed' }), $Rid, $sizeMb MB)" -ForegroundColor Green
exit 0
