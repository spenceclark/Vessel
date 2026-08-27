<#
.SYNOPSIS
Shared helper for the verify scripts to read a live vessel.db from PowerShell.

Loads Microsoft.Data.Sqlite (and ZstdSharp for body decompression) out of the Vessel
build output, pre-loading the native e_sqlite3 library so SQLitePCLRaw can find it when
running outside the app's own base directory. All functions degrade to a clear warning
rather than throwing when the assemblies can't be located — the byte-comparison portion
of verify.ps1 must still run on a machine that never built the project.
#>

$script:VesselSqliteReady = $false

function Import-VesselSqlite {
    param([string]$RepoRoot)

    if ($script:VesselSqliteReady) { return $true }

    $binRoot = Join-Path $RepoRoot "src/Vessel/bin"
    if (-not (Test-Path $binRoot)) {
        Write-Warning "DB checks skipped: no build output under $binRoot (build the project first)."
        return $false
    }

    # Prefer a Release build, fall back to whatever exists; take the newest match.
    function Find-Newest([string]$name) {
        Get-ChildItem -Path $binRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }

    $sqlite = Find-Newest "Microsoft.Data.Sqlite.dll"
    $zstd = Find-Newest "ZstdSharp.dll"
    $native = Find-Newest "e_sqlite3.dll"
    if ($null -eq $sqlite -or $null -eq $native) {
        Write-Warning "DB checks skipped: could not find Microsoft.Data.Sqlite.dll / e_sqlite3.dll under $binRoot."
        return $false
    }

    try {
        # Pre-load the native lib by full path so the later by-name load resolves in-proc.
        [System.Runtime.InteropServices.NativeLibrary]::Load($native.FullName) | Out-Null

        $dir = Split-Path $sqlite.FullName -Parent
        foreach ($dep in @("SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "SQLitePCLRaw.batteries_v2.dll")) {
            $path = Join-Path $dir $dep
            if (Test-Path $path) { Add-Type -Path $path -ErrorAction SilentlyContinue }
        }
        Add-Type -Path $sqlite.FullName
        if ($null -ne $zstd) { Add-Type -Path $zstd.FullName }
    }
    catch {
        Write-Warning "DB checks skipped: failed to load SQLite assemblies: $_"
        return $false
    }

    $script:VesselSqliteReady = $true
    return $true
}

function Get-VesselRows {
    param([string]$DbPath, [string]$Sql)

    $csb = New-Object Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    $csb.DataSource = $DbPath
    $csb.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
    $csb.Pooling = $false

    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection($csb.ToString())
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        $reader = $cmd.ExecuteReader()
        $rows = @()
        while ($reader.Read()) {
            $row = [ordered]@{}
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $name = $reader.GetName($i)
                $row[$name] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
            }
            $rows += [pscustomobject]$row
        }
        return , $rows
    }
    finally {
        $conn.Close()
    }
}

function Expand-VesselBody {
    param([byte[]]$Compressed)
    if ($null -eq $Compressed) { return $null }
    $decompressor = New-Object ZstdSharp.Decompressor
    try {
        return $decompressor.Unwrap($Compressed).ToArray()
    }
    finally {
        $decompressor.Dispose()
    }
}
