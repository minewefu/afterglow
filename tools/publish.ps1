param(
    [string]$Version = "v0.0.0-dev"
)

# Publishes the self-contained portable build into artifacts/.
$ErrorActionPreference = "Stop"
$clean = $Version.TrimStart("v")
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\portable"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root "src\Afterglow.App") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$clean -o $out
if ($LASTEXITCODE -ne 0) { exit 1 }

dotnet publish (Join-Path $root "src\Afterglow.Cli") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$clean -o $out
if ($LASTEXITCODE -ne 0) { exit 1 }

# Trim publish noise.
Get-ChildItem $out -Filter "*.pdb" | Remove-Item

# Belt-and-suspenders: single-file publish has been seen to skip bundled native exes.
Copy-Item (Join-Path $root "ThirdParty\PresentMon\*") (Join-Path $out "ThirdParty\PresentMon\") -Force
if (-not (Test-Path (Join-Path $out "ThirdParty\PresentMon\PresentMon-2.5.1-x64.exe"))) {
    Write-Error "PresentMon missing from publish output"
    exit 1
}

Copy-Item (Join-Path $root "LICENSE") $out
Copy-Item (Join-Path $root "THIRD_PARTY.md") $out
Copy-Item (Join-Path $root "README.md") $out -ErrorAction SilentlyContinue

$zip = Join-Path $root "artifacts\Afterglow-$clean-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip

Write-Host "Portable build: $zip"
Get-ChildItem (Join-Path $root "artifacts")
