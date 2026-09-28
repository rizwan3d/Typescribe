param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('win-x64')]
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$Version = '0.15.1'
$Asset = 'typst-x86_64-pc-windows-msvc.zip'
$ExpectedSha256 = '19ce3551153c2fe7ee9fa2f95208310c8f4d3209fedb699e0333faf8913f6736'
$Root = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root 'src/Typescribe.Infrastructure/tools'
$Temp = Join-Path ([IO.Path]::GetTempPath()) ("typescribe-typst-" + [Guid]::NewGuid().ToString('N'))
$Archive = Join-Path $Temp $Asset
$Url = "https://github.com/typst/typst/releases/download/v$Version/$Asset"

try {
    New-Item -ItemType Directory -Force -Path $Temp, $Tools | Out-Null
    Write-Host "Downloading Typst $Version for $Rid..."
    Invoke-WebRequest -Uri $Url -OutFile $Archive -UseBasicParsing

    $Actual = (Get-FileHash -Path $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Actual -ne $ExpectedSha256) {
        throw "Typst archive checksum mismatch. Expected $ExpectedSha256, got $Actual"
    }

    $Extracted = Join-Path $Temp 'extracted'
    Expand-Archive -Path $Archive -DestinationPath $Extracted -Force
    $Binary = Get-ChildItem -Path $Extracted -Filter 'typst.exe' -File -Recurse | Select-Object -First 1
    if (-not $Binary) { throw 'Typst executable not found in release archive.' }

    Copy-Item -Path $Binary.FullName -Destination (Join-Path $Tools 'typst.exe') -Force
    Remove-Item -Path (Join-Path $Tools 'typst') -Force -ErrorAction SilentlyContinue
    Write-Host "Pinned Typst $Version copied to $Tools/typst.exe"
}
finally {
    Remove-Item -Path $Temp -Recurse -Force -ErrorAction SilentlyContinue
}
