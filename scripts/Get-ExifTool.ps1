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

# The download page links the current Windows package; its name has changed over the years
# (exiftool-13.xx_64.zip today), so take the link from the page rather than guessing it.
$page = (Invoke-WebRequest -Uri 'https://exiftool.org/' -UseBasicParsing).Content
$link = [regex]::Match($page, 'href="([^"]*exiftool-[0-9.]+_64\.zip)"', 'IgnoreCase')
$candidates = @()
if ($link.Success) {
    $candidates += ([Uri]::new([Uri]'https://exiftool.org/', $link.Groups[1].Value)).AbsoluteUri
}
# Fallback: the current version's package on exiftool.org, then its SourceForge mirror.
$raw = (Invoke-WebRequest -Uri 'https://exiftool.org/ver.txt' -UseBasicParsing).Content
if ($raw -is [byte[]]) { $raw = [Text.Encoding]::ASCII.GetString($raw) }
$version = ([string]$raw).Trim()
$candidates += "https://exiftool.org/exiftool-${version}_64.zip"
$candidates += "https://sourceforge.net/projects/exiftool/files/exiftool-${version}_64.zip/download"

$temp = Join-Path ([IO.Path]::GetTempPath()) ("exiftool-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $zip = Join-Path $temp 'exiftool.zip'
    $downloaded = $false
    foreach ($url in ($candidates | Select-Object -Unique)) {
        try {
            Write-Host "Downloading $url"
            # SourceForge only redirects to the file for non-browser user agents.
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -UserAgent 'Wget'
            $downloaded = $true
            break
        }
        catch {
            Write-Warning "Download failed: $($_.Exception.Message)"
        }
    }
    if (-not $downloaded) { throw "Could not download ExifTool. Get the Windows 64-bit package from https://exiftool.org and unpack it into $Destination, renaming 'exiftool(-k).exe' to 'exiftool.exe'." }

    Expand-Archive -Path $zip -DestinationPath $temp

    # The archive holds "exiftool(-k).exe" and its "exiftool_files" folder; the exe must be renamed
    # to exiftool.exe, or it waits for a key press after every run.
    $exe = Get-ChildItem -Path $temp -Recurse -File |
        Where-Object { $_.Name -in 'exiftool(-k).exe', 'exiftool.exe' } | Select-Object -First 1
    if (-not $exe) { throw "No exiftool executable found in the downloaded package" }

    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $exe.DirectoryName '*') -Destination $Destination -Recurse
    if ($exe.Name -ne 'exiftool.exe') {
        Rename-Item -Path (Join-Path $Destination $exe.Name) -NewName 'exiftool.exe'
    }

    & (Join-Path $Destination 'exiftool.exe') -ver | ForEach-Object { Write-Host "Installed ExifTool $_ in $((Resolve-Path $Destination).Path)" }
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
