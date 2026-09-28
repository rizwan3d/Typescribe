param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('win-x64')]
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$Publish = Join-Path $Artifacts "publish/$Rid"
$Project = Join-Path $Root 'src/Typescribe.Desktop/Typescribe.Desktop.csproj'

& (Join-Path $PSScriptRoot 'fetch-typst.ps1') -Rid $Rid
if (Test-Path $Publish) { Remove-Item $Publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Publish, $Artifacts | Out-Null

dotnet restore (Join-Path $Root 'Typescribe.slnx')
dotnet publish $Project `
    -c Release `
    -r $Rid `
    --self-contained true `
    -o $Publish `
    -p:PublishAot=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') $Publish -Force
Copy-Item (Join-Path $Root 'README.md') $Publish -Force

$Archive = Join-Path $Artifacts "Typescribe-$Rid.zip"
if (Test-Path $Archive) { Remove-Item $Archive -Force }
Compress-Archive -Path (Join-Path $Publish '*') -DestinationPath $Archive -CompressionLevel Optimal
Write-Host "Created: $Archive"
