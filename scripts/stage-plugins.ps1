<#
.SYNOPSIS
    Builds and stages the WPA-Fusion SEP plugin.
.DESCRIPTION
    Output: artifacts\plugins\WPA-Fusion\ (WpaFusion.Plugin.Sep.dll and shared libraries; TMA metrics come
    from the local SEP install's config\edp). The Typeperf processing source is excluded for now; capture still writes its CSV. Load it with:
        wpa.exe -addsearchdir <repo>\artifacts\plugins\WPA-Fusion
    Staging keeps WPA from locking the build output.
.PARAMETER Configuration
    Debug or Release (default Release).
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "artifacts\plugins\WPA-Fusion"

foreach ($project in "WpaFusion.Plugin.Sep") {
    $csproj = Join-Path $root "src\$project\$project.csproj"
    $out = & dotnet build $csproj -c $Configuration -v q -nologo -clp:NoSummary 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        Write-Output "BUILD FAILED: $project"
        $out | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
        exit 1
    }
}

if (Test-Path $stage) {
    try {
        Remove-Item $stage -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Output "Cannot replace $stage (is WPA running with it loaded?): $($_.Exception.Message)"
        exit 1
    }
}
New-Item -ItemType Directory -Path $stage | Out-Null

foreach ($project in "WpaFusion.Plugin.Sep") {
    $bin = Join-Path $root "src\$project\bin\$Configuration\net6.0"
    Copy-Item (Join-Path $bin "*") $stage -Recurse -Force
}

# The SDK must come from WPA; fail loudly if a copy slipped into the stage.
if (Test-Path (Join-Path $stage "Microsoft.Performance.SDK.dll")) {
    Write-Output "ERROR: Microsoft.Performance.SDK.dll was staged; plugins must not ship the SDK."
    exit 1
}

$files = Get-ChildItem $stage -Recurse -File
Write-Output "Staged $($files.Count) files to $stage"
exit 0
