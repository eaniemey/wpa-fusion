<#
.SYNOPSIS
    Publishes paired self-contained Windows x64 capture and attribution packages.
#>
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$release = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$name = "WPA-Fusion-Capture-win-x64-$release"
$attributeName = "WPA-Fusion-Attribute-win-x64-$release"
$packages = Join-Path $root "artifacts\packages"
$package = Join-Path $packages $name
$archive = "$package.zip"
$attributePackage = Join-Path $packages $attributeName
$attributeArchive = "$attributePackage.zip"
if ((Test-Path $package) -or (Test-Path $archive) -or
    (Test-Path $attributePackage) -or (Test-Path $attributeArchive)) {
    throw "Package already exists for release $release"
}

New-Item -ItemType Directory -Path $package -Force | Out-Null
$project = Join-Path $root "src\WpaFusion.Capture\WpaFusion.Capture.csproj"
# One compressed self-contained exe; symbols are embedded so crash stacks keep line numbers.
& dotnet publish $project -c Release -f net10.0 -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded -p:PublishTrimmed=false -o $package -nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "Collector publish failed; incomplete files remain at $package"
}

Copy-Item (Join-Path $root "src\WpaFusion.Capture\PACKAGE-README.txt") (Join-Path $package "README.txt")

New-Item -ItemType Directory -Path $attributePackage -Force | Out-Null
$attributeProject = Join-Path $root "src\WpaFusion.Attribution\WpaFusion.Attribution.csproj"
& dotnet publish $attributeProject -c Release -f net6.0 -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:DebugType=embedded -p:PublishTrimmed=false -o $attributePackage -nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "Attribute publish failed; incomplete files remain at $attributePackage"
}

$files = @(Get-ChildItem -LiteralPath $attributePackage -Recurse -File)
if (@($files | Where-Object Extension -eq ".exe").Count -ne 1 -or
    @($files | Where-Object { $_.Name -ne "wpafusion-attribute.exe" }).Count -ne 0) {
    throw "Attribute publish did not produce exactly one executable: $attributePackage"
}

Copy-Item (Join-Path $root "src\WpaFusion.Attribution\PACKAGE-README.txt") (Join-Path $attributePackage "README.txt")
Copy-Item (Join-Path $attributePackage "wpafusion-attribute.exe") (Join-Path $package "wpafusion-attribute.exe")
Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
Compress-Archive -LiteralPath $attributePackage -DestinationPath $attributeArchive -CompressionLevel Optimal

foreach ($path in @($archive, $attributeArchive)) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    Write-Output "Package: $path"
    Write-Output "SHA256: $($hash.Hash)"
}
