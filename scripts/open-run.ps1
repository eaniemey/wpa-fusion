<#
.SYNOPSIS
    Opens native, processed, or fused WPA-Fusion data, processing raw SEP/EMON or opt-in batches as needed.
.PARAMETER Run
    A raw/processed run, opt-in capture batch, or fused folder. Use the processing command's Output path.
.PARAMETER SymbolPath
    Optional local symbol folder for function names. Never a symbol server.
.PARAMETER Profile
    Optional .wpaProfile exported from WPA. Without one, WPA opens with each table's default preset.
.PARAMETER NoAttribute
    Skip processing. Raw SEP may open without attribution; raw EMON and batches must be processed first.
#>
param(
    [Parameter(Mandatory = $true)] [string]$Run,
    [string]$SymbolPath = "",
    [string]$Profile = "",
    [switch]$NoAttribute
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$wpa = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe"
$stage = Join-Path $root "artifacts\plugins\WPA-Fusion"

function Read-Session([string]$folder) {
    $file = Join-Path $folder "session.json"
    if (-not (Test-Path $file)) { throw "No session.json in $folder. Is this a WPA-Fusion run folder?" }
    return Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
}

function Attribution-Executable {
    $paths = @("src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe", "src\WpaFusion.Attribution\bin\Release\net6.0\wpafusion-attribute.exe") |
        ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } |
        Get-Item | Sort-Object LastWriteTime -Descending
    if (-not $paths) { throw "Build wpafusion-attribute before processing a capture batch." }
    return ($paths | Select-Object -First 1).FullName
}

function Fusion-Source([string]$folder, [string]$relative) {
    $prefix = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
    $path = [IO.Path]::GetFullPath((Join-Path $folder $relative))
    if ([IO.Path]::IsPathRooted($relative) -or -not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fusion source paths must stay inside the fused dataset."
    }
    return $path
}

$runPath = (Resolve-Path -LiteralPath $Run).Path
if (Test-Path (Join-Path $runPath "CAPTURE-FAILED.txt")) {
    throw "This capture did not complete; see CAPTURE-FAILED.txt in $runPath"
}

if (Test-Path (Join-Path $runPath "capture-batch.json")) {
    if ($NoAttribute) { throw "Process a capture batch with wpafusion-attribute before using -NoAttribute." }
    $attribute = Attribution-Executable
    $arguments = @($runPath)
    if ($SymbolPath) { $arguments += $SymbolPath }
    $result = & $attribute @arguments 2>&1
    $result | ForEach-Object { Write-Output $_ }
    if ($LASTEXITCODE -ne 0) { throw "Batch attribution/fusion failed." }
    $line = $result | Where-Object { $_ -like "Output: *" } | Select-Object -Last 1
    if (-not $line) { throw "Batch fusion did not report an output dataset." }
    $runPath = $line.Substring("Output: ".Length).Trim()
}

$files = @()
if (Test-Path (Join-Path $runPath "fusion.json")) {
    $fusion = Get-Content -LiteralPath (Join-Path $runPath "fusion.json") -Raw | ConvertFrom-Json
    if ($fusion.schemaVersion -ne 1 -or -not $fusion.separateExecutions) { throw "Unsupported fused dataset." }
    if (Test-Path (Join-Path $runPath "PROCESSING-FAILED.txt")) { throw "Fusion did not complete successfully." }
    Write-Output "Cross-run comparison: each source is a separate execution; process/thread identities belong to their native SEP/ETL pair."
    $fusion.warnings | ForEach-Object { Write-Output "WARNING: $_" }
    $reference = Fusion-Source $runPath $fusion.referenceDirectory
    $referenceSession = Read-Session $reference
    $files += Join-Path $reference $referenceSession.files.etl
    foreach ($source in $fusion.sources) {
        $folder = Fusion-Source $runPath $source.directory
        $sourceSession = Read-Session $folder
        if ($sourceSession.files.tb7) { $files += Join-Path $folder $sourceSession.files.tb7 }
        if ($sourceSession.files.emonMetrics) { $files += Join-Path $folder $sourceSession.files.emonMetrics }
    }
}
else {
$session = Read-Session $runPath
$tb7Name = $session.files.tb7
if (-not $tb7Name -and -not $session.files.emonDat) { throw "session.json has neither SEP nor EMON data." }
if ($session.files.emonDat -and -not $session.files.emonMetrics) {
    if ($NoAttribute) { throw "Process raw EMON data with scripts\process-emon.ps1 before using -NoAttribute." }
    $processedOutput = & (Join-Path $PSScriptRoot "process-emon.ps1") -Run $runPath
    $processedOutput | ForEach-Object { Write-Output $_ }
    $line = $processedOutput | Where-Object { $_ -like "Output: *" } | Select-Object -Last 1
    if (-not $line) { throw "EMON processing did not report an output run." }
    $runPath = $line.Substring("Output: ".Length).Trim()
    $session = Read-Session $runPath
}
$processed = -not $tb7Name -or (Test-Path (Join-Path $runPath ($tb7Name + ".attribution.json")))

if (-not $processed -and -not $NoAttribute) {
    # Reuse an earlier processed folder for this capture, newest first.
    $existing = Get-ChildItem -Path (Join-Path $root "output") -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Where-Object {
            $f = Join-Path $_.FullName "session.json"
            (Test-Path $f) -and ((Get-Content -LiteralPath $f -Raw | ConvertFrom-Json).processedFrom -eq $runPath)
        } | Select-Object -First 1
    if ($existing) {
        $runPath = $existing.FullName
        Write-Output "Reusing processed folder: $runPath"
    }
    else {
        $exe = Join-Path $root "src\WpaFusion.Attribution\bin\Release\net6.0\wpafusion-attribute.exe"
        if (-not (Test-Path $exe)) { $exe = Join-Path $root "src\WpaFusion.Attribution\bin\Debug\net6.0\wpafusion-attribute.exe" }
        if (-not (Test-Path $exe)) { throw "wpafusion-attribute.exe not found. Run scripts\check.ps1 first." }
        $args = @($runPath, "--stacks", "lbr")
        if ($SymbolPath) { $args = @($runPath, $SymbolPath, "--stacks", "lbr") }
        $out = & $exe @args 2>&1
        $out | ForEach-Object { Write-Output $_ }
        if ($LASTEXITCODE -ne 0) { throw "Attribution failed (exit $LASTEXITCODE)." }
        $line = $out | Where-Object { $_ -like "Output: *" } | Select-Object -First 1
        if (-not $line) { throw "Attribution did not report its output folder." }
        $runPath = $line.Substring("Output: ".Length).Trim()
    }
}

$session = Read-Session $runPath
if ($session.files.etl) { $files += (Join-Path $runPath $session.files.etl) }
if ($session.files.tb7) { $files += (Join-Path $runPath $session.files.tb7) }
if ($session.files.emonMetrics) { $files += (Join-Path $runPath $session.files.emonMetrics) }
}

if (-not (Test-Path $stage)) {
    & (Join-Path $root "scripts\stage-plugins.ps1")
}

$wpaArgs = @("-addsearchdir", $stage)
foreach ($f in $files) { if (Test-Path $f) { $wpaArgs += @("-i", $f) } }
if ($Profile) { $wpaArgs += @("-profile", $Profile, "-mode", "Single") }

Write-Output ("WPA command: `"{0}`" {1}" -f $wpa, ($wpaArgs -join " "))
Start-Process -FilePath $wpa -ArgumentList $wpaArgs
Write-Output "Opened $runPath"
