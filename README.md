# HolidayPhotoGallery

A personal photo manager for Windows, in the spirit of Windows Live Photo Gallery: browse your
photo folders, search and filter, and edit tags, ratings, captions and dates, with everything
saved **in the photo files themselves**.

## Principles

- **Your files are the source of truth.** Tags, ratings, captions and dates are written into each
  file's metadata (XMP, EXIF and IPTC, plus the Microsoft fields Windows Explorer and Windows Live
  Photo Gallery read), so other tools see the same information. Photos stay where they are.
- **The database is only a cache.** A small search index lives in
  `%LocalAppData%\HolidayPhotoGallery\index.db`. Delete it at any time; it is rebuilt from the files.
- **Lossless where possible.** Rotating changes the EXIF orientation tag; the image is never re-encoded.

## Formats

| Format | View | Tags, rating, caption, date | Rotate |
|---|---|---|---|
| JPEG | ✓ | ✓ | ✓ |
| HEIC / HEIF | ✓ ¹ | ✓ | not yet ² |
| TIFF | ✓ | ✓ | ✓ |
| PNG, WebP | ✓ | ✓ | not yet |
| GIF | ✓ | ✓ (XMP only) | not yet |

¹ Windows needs the free [HEIF Image Extensions](https://apps.microsoft.com/detail/9pmmsr1cgpwg) from the Microsoft Store; most Windows 11 PCs already have it.
² HEIC stores rotation in its own `irot` box, which ExifTool cannot write yet.

## What the first version does

- Add one or more folders; browse them in a folder tree or all at once.
- Thumbnail grid using Windows' own thumbnails, and a full-size viewer (double-click or Enter; arrows to move, Esc to close).
- Search by file name, caption or tag; filter by tags, minimum rating and date range.
- Edit rating, caption and tags for one photo or many at once (tags you add or remove apply to all
  selected photos; other tags are left alone). Edit the date taken, with its time zone offset, for single photos.
- Rotate JPEG and TIFF photos left or right (Ctrl+, and Ctrl+.).
- **Compare** 1 to 4 selected photos side by side (Enter or the Compare button), with zoom and pan
  shared between them. Keys 1–5 rate the highlighted photo, 0 clears it, Tab moves on. With a single
  photo, Left and Right step through the gallery.
- Filter by **camera**, and **shift dates**: move the date taken of the selected photos by an amount,
  or set the earliest one to a known time and move the rest to match, optionally setting the time zone.
  The camera's "digitized" date moves along so all dates in the file agree.

## Roadmap

1. ~~Compare view with rating; camera filter and date shifting~~
2. Map panel (OpenStreetMap) to place photos, and looking up place names from GPS
3. Crop and automatic contrast/brightness, keeping the original in a backup folder with undo
4. Automatic tagging with a model that runs on the PC

## Building

You need Windows 10 1809 or later, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and
Visual Studio 2022 17.14+ or 2026 with the **WinUI application development** workload.

```powershell
git clone https://github.com/GHLasse/HolidayPhotoGallery
cd HolidayPhotoGallery
.\scripts\Get-ExifTool.ps1      # downloads ExifTool into tools\exiftool (once)
```

Then open `HolidayPhotoGallery.slnx`, choose **x64** (or ARM64), set `PhotoGallery.App` as the
startup project and press F5. From the command line:

```powershell
dotnet build src\PhotoGallery.App -c Release -p:Platform=x64
.\src\PhotoGallery.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\HolidayPhotoGallery.exe
```

The app is a plain `.exe` that carries its own Windows App SDK runtime, so nothing needs installing.

## Project layout

- `src/PhotoGallery.Core` – metadata reading and writing (via ExifTool), the search index and folder scanning. Plain .NET, no UI.
- `src/PhotoGallery.App` – the WinUI 3 app.
- `tests/PhotoGallery.Core.Tests` – tests that write and read real JPEG, HEIC, PNG, WebP, TIFF and GIF files. They need ExifTool on the PATH and are skipped otherwise.

## Credits

Metadata handling uses [ExifTool](https://exiftool.org) by Phil Harvey.
