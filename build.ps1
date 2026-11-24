param()

$ErrorActionPreference = 'Stop'
$sourceZip = Join-Path $PSScriptRoot 'ant-libs\ANT_Libraries.zip'
$libraryDir = Join-Path $PSScriptRoot 'ant-libs\pc'
$project = Join-Path $PSScriptRoot 'BrainWalk.csproj'
$output = Join-Path $PSScriptRoot 'bin\Release\net9.0-windows\win-x86\publish-pc'

& (Join-Path $PSScriptRoot 'create-icon.ps1')

if (-not (Test-Path -LiteralPath $sourceZip)) {
    throw "ANT_Libraries.zip not found: $sourceZip"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$outer = [System.IO.Compression.ZipFile]::OpenRead($sourceZip)
try {
    $sdkEntry = $outer.GetEntry('ANT_Libraries/ANT-SDK_PC.3.5.zip')
    if ($null -eq $sdkEntry) { throw 'PC ANT SDK archive is missing.' }
    $memory = [System.IO.MemoryStream]::new()
    $sdkStream = $sdkEntry.Open()
    try { $sdkStream.CopyTo($memory) } finally { $sdkStream.Dispose() }
    $memory.Position = 0
    $sdk = [System.IO.Compression.ZipArchive]::new($memory, [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        New-Item -ItemType Directory -Path $libraryDir -Force | Out-Null
        foreach ($name in @('ANT_NET.dll', 'ANT_WrappedLib.dll', 'DSI_CP210xManufacturing_3_1.dll', 'DSI_SiUSBXp_3_1.dll')) {
            $entry = $sdk.GetEntry("ANT-SDK_PC.3.5/Bin/$name")
            if ($null -eq $entry) { throw "PC ANT SDK is missing $name" }
            $destination = Join-Path $libraryDir $name
            $source = $entry.Open()
            $target = [System.IO.File]::Create($destination)
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    } finally { $sdk.Dispose(); $memory.Dispose() }
} finally { $outer.Dispose() }

dotnet publish $project -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$runBat = Join-Path $output 'RUN.bat'
$runBatContent = @'
@echo off
cd /d "%~dp0"
"brainWALK-run.exe" -console
'@
[System.IO.File]::WriteAllText($runBat, $runBatContent, [System.Text.Encoding]::ASCII)
Write-Host "Published: $output"
Write-Host "Console launcher: $runBat"
