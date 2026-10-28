<#
.SYNOPSIS
    Opens a WPA-Fusion run in WPA: runs attribution if needed, then launches WPA on the processed folder.
.PARAMETER Run
    A capture folder (raw) or a processed folder.
.PARAMETER SymbolPath
    Optional local symbol folder for function names. Never a symbol server.
.PARAMETER Profile
    Optional .wpaProfile exported from WPA. Without one, WPA opens with each table's default preset.
.PARAMETER NoAttribute
    Do not run attribution; open the folder as given.
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

$runPath = (Resolve-Path -LiteralPath $Run).Path
if (Test-Path (Join-Path $runPath "CAPTURE-FAILED.txt")) {
    throw "This capture did not complete; see CAPTURE-FAILED.txt in $runPath"
}

$session = Read-Session $runPath
$tb7Name = $session.files.tb7
if (-not $tb7Name) { throw "session.json has no tb7 file; this capture did not bind SEP data." }
$processed = Test-Path (Join-Path $runPath ($tb7Name + ".attribution.json"))

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
$tb7 = Join-Path $runPath $session.files.tb7
$files = @()
if ($session.files.etl) { $files += (Join-Path $runPath $session.files.etl) }
$files += $tb7

if (-not (Test-Path $stage)) {
    & (Join-Path $root "scripts\stage-plugins.ps1")
}

$wpaArgs = @("-addsearchdir", $stage)
foreach ($f in $files) { if (Test-Path $f) { $wpaArgs += @("-i", $f) } }
if ($Profile) { $wpaArgs += @("-profile", $Profile, "-mode", "Single") }

Write-Output ("WPA command: `"{0}`" {1}" -f $wpa, ($wpaArgs -join " "))
Start-Process -FilePath $wpa -ArgumentList $wpaArgs
Write-Output "Opened $runPath"
