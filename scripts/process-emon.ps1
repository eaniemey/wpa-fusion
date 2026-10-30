param(
    [Parameter(Mandatory = $true)] [string]$Run,
    [string]$MppExe = $env:WPA_FUSION_MPP_EXE,
    [string]$TimeZoneId = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $Run).Path
$session = Get-Content -LiteralPath (Join-Path $source "session.json") -Raw | ConvertFrom-Json
if (Test-Path (Join-Path $source "CAPTURE-FAILED.txt")) { throw "Cannot process a failed capture." }
if (-not $session.files.emonDat -or -not $session.syncStart -or -not $session.syncEnd -or $session.qpcFrequency -le 0) { throw "EMON processing requires emonDat and capture clock anchors in session.json; ETL is optional." }
if ($TimeZoneId) { $session | Add-Member -NotePropertyName captureTimeZoneId -NotePropertyValue $TimeZoneId -Force }
if (-not $session.captureTimeZoneId) { throw "Older captures require -TimeZoneId with the collection machine's time zone, not the analysis machine's." }
[void][TimeZoneInfo]::FindSystemTimeZoneById($session.captureTimeZoneId)

if (-not $MppExe) {
    $command = Get-Command mpp -ErrorAction SilentlyContinue
    if ($command) { $MppExe = $command.Source }
    else {
        $local = Join-Path $root "artifacts\mpp-env\Scripts\mpp.exe"
        if (Test-Path $local) { $MppExe = $local }
    }
}
if (-not $MppExe -or -not (Test-Path -LiteralPath $MppExe)) { throw "Install Intel MPP from the SEP pyedp distribution and set WPA_FUSION_MPP_EXE or pass -MppExe." }
$sep = $env:WPA_FUSION_SEP_ROOT
if (-not $sep) { $sep = Join-Path ${env:ProgramFiles(x86)} "IntelSWTools\sep" }
$metrics = Join-Path $sep "config\edp"
if (-not (Test-Path $metrics)) { throw "Set WPA_FUSION_SEP_ROOT to the SEP installation containing config\edp." }

$output = Join-Path (Join-Path $root "output") (Get-Date -Format "yyyyMMdd-HHmmss-fff")
if (Test-Path $output) { throw "Output run already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
try {
    foreach ($property in $session.files.PSObject.Properties) {
        if (-not $property.Value) { continue }
        $name = [string]$property.Value
        if ([IO.Path]::GetFileName($name) -ne $name -or $name.Contains('/') -or $name.Contains('\')) { throw "Manifest inputs must be filenames inside the source run." }
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $output $name)
    }
    foreach ($name in @("capture.log", "emon.log")) {
        if (Test-Path (Join-Path $source $name)) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $output }
    }
    $prefix = Join-Path $output "emon"
    $raw = Join-Path $output $session.files.emonDat
    $definitionName = "edp-metrics.xml"
    $definitionPath = Join-Path $output $definitionName
    if (-not (Test-Path $definitionPath)) {
        $header = Get-Content -LiteralPath $raw -TotalCount 4096 |
            Select-String -Pattern '^EDP metric file:\s*(.+)$' | Select-Object -First 1
        if ($header) {
            $name = $header.Matches[0].Groups[1].Value.Trim()
            if ([IO.Path]::GetFileName($name) -ne $name -or $name.Contains('/') -or $name.Contains('\')) { throw "Invalid captured EDP metric filename." }
            $selected = Join-Path $metrics $name
            if (Test-Path $selected) { Copy-Item -LiteralPath $selected -Destination $definitionPath }
        }
    }
    if (Test-Path $definitionPath) { $metricInput = $definitionPath }
    else { $metricInput = $metrics; $definitionName = $null }
    & $MppExe -m $metricInput -i $raw -o $prefix
    if ($LASTEXITCODE -ne 0) { throw "Intel MPP failed with exit code $LASTEXITCODE." }
    $details = "emon_system_view_details.csv"
    $summary = "emon_system_view_summary.csv"
    foreach ($name in @($details, $summary)) {
        if (-not (Test-Path (Join-Path $output $name))) { throw "MPP did not produce expected output: $name" }
    }
    @{ schemaVersion = 1; details = $details; summary = $summary; metricDefinitions = $definitionName } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output "metrics.emon.json") -Encoding UTF8
    $session.files | Add-Member -NotePropertyName emonMetrics -NotePropertyValue "metrics.emon.json" -Force
    if ($definitionName) { $session.files | Add-Member -NotePropertyName emonDefinition -NotePropertyValue $definitionName -Force }
    $session | Add-Member -NotePropertyName processedFrom -NotePropertyValue $source -Force
    $session.tools | Add-Member -NotePropertyName mpp -NotePropertyValue "$MppExe -m $metricInput -i emon.dat -o emon" -Force
    $session | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output "session.json") -Encoding UTF8
    Write-Output "Output: $output"
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $output "PROCESSING-FAILED.txt")
    throw
}