<#
.SYNOPSIS
    Downloads the current Windows build of ExifTool into tools\exiftool, where the app build picks it up.

.DESCRIPTION
    ExifTool (https://exiftool.org, by Phil Harvey) reads and writes the metadata in the photo files.
    It is not committed to the repository; run this once after cloning, then rebuild the app.
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\tools\exiftool')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Read ver.txt as text: Invoke-RestMethod would turn "13.10" into the number 13.1.
$raw = (Invoke-WebRequest -Uri 'https://exiftool.org/ver.txt' -UseBasicParsing).Content
if ($raw -is [byte[]]) { $raw = [Text.Encoding]::ASCII.GetString($raw) }
$version = ([string]$raw).Trim()
$zipName = "exiftool-${version}_64.zip"
Write-Host "Downloading ExifTool $version..."

$temp = Join-Path ([IO.Path]::GetTempPath()) ("exiftool-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = Join-Path $temp $zipName
    Invoke-WebRequest -Uri "https://exiftool.org/$zipName" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $temp

    # The archive holds "exiftool(-k).exe" and its "exiftool_files" folder; the exe must be renamed
    # to exiftool.exe, or it waits for a key press after every run.
    $exe = Get-ChildItem -Path $temp -Recurse -Filter 'exiftool(-k).exe' | Select-Object -First 1
    if (-not $exe) { throw "exiftool(-k).exe not found in $zipName" }

    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $exe.DirectoryName '*') -Destination $Destination -Recurse
    Rename-Item -Path (Join-Path $Destination $exe.Name) -NewName 'exiftool.exe'

    & (Join-Path $Destination 'exiftool.exe') -ver | ForEach-Object { Write-Host "Installed ExifTool $_ in $((Resolve-Path $Destination).Path)" }
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
