<#
.SYNOPSIS
    Build and test WPA-Fusion with truncated output, for AI sessions and humans alike.
.DESCRIPTION
    Runs a quiet build, then tests with minimal verbosity. Prints at most -MaxLines
    lines (errors and failures first) and exits non-zero on any failure.
.EXAMPLE
    pwsh scripts/check.ps1
    pwsh scripts/check.ps1 -Filter "FullyQualifiedName~ClockMap"
#>
param(
    [string]$Filter = "",
    [int]$MaxLines = 60
)

# Continue, not Stop: Windows PowerShell turns native stderr into error records under 2>&1,
# which would abort the script before failures are printed. Exit codes decide success.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "WpaFusion.sln"

function Show-Truncated([string[]]$lines, [int]$max) {
    # Surface errors and failures first, then everything else, up to $max lines.
    # Drop PowerShell's own wrapper noise around native stderr lines.
    $lines = $lines | Where-Object { $_ -notmatch "^\s*(\+ |At .*check\.ps1|CategoryInfo|FullyQualifiedErrorId)" }
    $lines = $lines | ForEach-Object { $_ -replace "^dotnet(\.exe)? : ", "" }
    $important = $lines | Where-Object { $_ -match "error|fail|Failed|\[FAIL\]|Exception|Assert" }
    $rest = $lines | Where-Object { $_ -notmatch "error|fail|Failed|\[FAIL\]|Exception|Assert" }
    $all = @($important) + @($rest) | Where-Object { $_.Trim() -ne "" } | Select-Object -Unique
    $all | Select-Object -First $max | ForEach-Object { Write-Output $_ }
    if ($all.Count -gt $max) { Write-Output "... ($($all.Count - $max) more lines truncated)" }
}

$build = & dotnet build $sln -v q -nologo -clp:NoSummary 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    Write-Output "BUILD FAILED"
    Show-Truncated $build $MaxLines
    exit 1
}

$testArgs = @("test", $sln, "--no-build", "-nologo", "--logger", "console;verbosity=minimal")
if ($Filter) { $testArgs += @("--filter", $Filter) }
$test = & dotnet @testArgs 2>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE

if ($code -ne 0) { Write-Output "TESTS FAILED" } else { Write-Output "BUILD OK, TESTS OK" }
Show-Truncated $test $MaxLines
exit $code
