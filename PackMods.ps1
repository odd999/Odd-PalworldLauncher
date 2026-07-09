# PackMods.ps1
# This script automatically zips your uncompressed mod folder and saves it to your Google Drive local mirror folder.

$sourcePath = "G:\My Drive\PalWorldMods\Client"
$outputPath = "G:\My Drive\PalWorldMods\ClientMods.zip"

Write-Host "Packing mods from: $sourcePath" -ForegroundColor Cyan
Write-Host "Output ZIP: $outputPath" -ForegroundColor Cyan

if (-not (Test-Path $sourcePath)) {
    Write-Error "Source path not found: $sourcePath"
    Exit
}

# Remove old zip if exists
if (Test-Path $outputPath) {
    Remove-Item $outputPath -Force
    Write-Host "Removed old ZIP archive." -ForegroundColor Yellow
}

# Remove desktop.ini files to prevent zip extraction errors
Write-Host "Cleaning up hidden desktop.ini files..." -ForegroundColor Cyan
Get-ChildItem -Path $sourcePath -Filter "desktop.ini" -Recurse -Force | Remove-Item -Force

# Compress the folder contents
Write-Host "Compressing folder..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($sourcePath, $outputPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "SUCCESS! Modpack successfully zipped." -ForegroundColor Green
Write-Host "Google Drive Desktop Client will now sync ClientMods.zip to the cloud automatically." -ForegroundColor Green
Start-Sleep -Seconds 3
