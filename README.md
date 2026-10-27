# HolidayPhotoGallery

A free photo manager for Windows in the spirit of Windows Live Photo Gallery. Everything you add is
saved in the photo itself.

HolidayPhotoGallery is for people who keep their pictures in folders on their own PC. It looks and feels
like a Windows 11 app and is built for the work after every trip: sorting hundreds of photos, choosing
the best ones, and getting the dates, places and names right. No account, no subscription, no cloud
needed.

**[Download the latest version](https://github.com/GHLasse/HolidayPhotoGallery/releases/latest)**
(installer `HolidayPhotoGallery-Setup-x64.exe`, or the portable zip)

## Principles

- **Your files are the source of truth.** Tags, ratings, flags, captions, people, places and dates are
  written into each file's own metadata (XMP, EXIF and IPTC, plus the fields Windows Explorer, Lightroom,
  digiKam and Windows Live Photo Gallery read). Copy a folder to another PC or open it in another program,
  and everything comes along. Photos stay where they are.
- **The database is only a cache.** The search index in `%LocalAppData%\HolidayPhotoGallery` can be
  deleted at any time; it is rebuilt from the files.
- **Private by design.** Automatic tagging, face recognition and AI upscaling run on your own PC. No
  photo is sent anywhere unless you upload it.
- **Every edit can be undone.** The original is kept in a hidden `.originals` folder next to the photo.

## Features

**Library and browsing**
- Add any folders; browse by folder, by date (year and month), by place (country and city), or everything at once
- Thumbnails keep their real shape; zoom with a slider or Ctrl+mouse wheel; month headers and a year timeline when sorted by date
- Workspaces in the title bar: Library, Map, People and Albums
- Search by file name, caption, tag or person; a filter bar for people, tags, camera, rating, flags, rejects, dates, uploads and missing details
- Folder albums that travel with the folder; drag photos onto a folder to move them (Ctrl copies), with undo
- Ctrl+K finds any command; a grouped list of keyboard shortcuts (?), first-use tips and Ctrl+Z undo

**Sorting and rating**
- Compare 1 to 4 photos side by side with shared zoom and pan, and rate them with the number keys
- Flags (for example "to print") and a reject mark, stored in the file; move rejected photos to the Recycle Bin
- Lightroom keyboard shortcuts and optional auto-advance after rating

**Dates and time zones**
- Edit the date taken with its time zone offset
- Filter by camera and shift a batch of dates by an amount, or set the first photo to a known time
- Windows' list of time zones, with summer time handled for each photo's own date

**Map and places**
- OpenStreetMap map beside the grid or full width, with a satellite view; click the map to place the selected photos
- Estimate a position for photos without GPS from the photos taken before and after
- Look up country, region, city and district and write them into IPTC/XMP, in the language you choose

**People**
- Faces found and recognised on your PC (YuNet and SFace); suggestions to confirm, groups to name
- Names saved as standard face regions (MWG and Microsoft), so Lightroom, digiKam and Windows Live Photo Gallery see them
- People already named in Windows Live Photo Gallery are read from the files

**Automatic tagging**
- Tags picked by an AI model on your PC (SigLIP 2) from your own tag list, in your language
- Auto tags are plain keywords other apps understand, and can be removed together

**Editing**
- Crop freely or to common ratios (1:1, 4:3, 3:2, 16:9, print sizes and more)
- Automatic brightness and contrast; rotate JPEG, HEIC, PNG, WebP, TIFF and GIF
- Undo the last edit or go back to the original; save in the original format or as JPEG

**Import, videos and tidying up**
- Import from a phone, camera or SD card
- Videos (details in the file for MP4, MOV, M4V and 3GP; an `.xmp` file beside AVI, MTS, MKV and WMV) and Live Photos
- Find duplicates, including resized copies, keep the best one and fill in its details from the others
- Rename a batch with a pattern (F2), with undo
- Repair damaged photos: fill a cut-off part from the preview stored in the file, bring back detail with AI upscaling (Real-ESRGAN, on the graphics card or processor), fix wrong file extensions
- Copy and paste details between photos (Ctrl+Shift+C / Ctrl+Shift+V)
- Private folders and photos, hidden until private mode is on (nothing is written into the photos)
- Updates inside the app, Stable or Experimental

**Sharing**
- Upload to Google Photos albums, with uploaded photos marked and filterable
- OneDrive albums
- Windows share dialog, "Open with" and two buttons for your favourite editors

**Look and language**
- Windows 11 Fluent design with a custom title bar and resizable panes
- Light, dark or follow Windows, plus colour palettes; the photo area stays neutral
- English, German, Spanish, Arabic (right to left) and Chinese; add your own language file ([how](docs/languages.md))

## Formats

| Format | View | Details (tags, rating, people, places, dates) | Rotate and edit |
|---|---|---|---|
| JPEG | ✓ | ✓ | ✓ |
| HEIC / HEIF | ✓ ¹ | ✓ | ✓ |
| PNG, WebP, TIFF | ✓ | ✓ | ✓ |
| GIF | ✓ | ✓ (XMP only) | ✓ |
| Videos | ✓ ² | ✓ | – |

¹ Windows needs the free [HEIF Image Extensions](https://apps.microsoft.com/detail/9pmmsr1cgpwg) from the
Microsoft Store; most Windows 11 PCs already have them. Saving HEIC also needs the HEVC Video Extensions.
² Played with Windows' own video support.

## Good to know

- Windows 10 version 1809 or later, or Windows 11, 64-bit (x64).
- The installer is per user and needs no admin rights. The zip runs without installing.
- The tagging model (about 750 MB), the face models (about 40 MB) and the upscaling models download the first time they are needed.
- The internet is only used for map tiles, satellite pictures and place names (OpenStreetMap), those one-time model downloads, update checks on GitHub, and Google Photos or OneDrive once you link them.
- The app is not code-signed yet, so Windows SmartScreen may warn the first time ("More info", then "Run anyway").

## Downloads and branches

- **Releases** ([latest](https://github.com/GHLasse/HolidayPhotoGallery/releases/latest)) are built from `main`, which
  only ever holds released versions.
- **Experimental** ([pre-release](https://github.com/GHLasse/HolidayPhotoGallery/releases/tag/experimental)) is the
  newest test version, rebuilt from `prep` whenever a change lands there. It may have bugs.

Versions are numbered `0.MINOR.BUILD`: a release ends in `.0`, test versions carry a build number. An
installed app looks for a new version once a day and offers it in a banner (Settings › App › Updates
picks Stable or Experimental). Settings › Info shows the version you have.

All work goes into `prep` through pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md) for the flow.

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

The app is a plain `.exe` that carries its own Windows App SDK runtime. The installer script is in
`installer/` (Inno Setup 6.7 or later).

## Project layout

- `src/PhotoGallery.Core` – everything without a window: metadata (via ExifTool), the search index, folder scanning,
  faces, tagging, duplicates, editing and repair, place lookup, albums, Google Photos and OneDrive, updates. Plain .NET.
- `src/PhotoGallery.App` – the WinUI 3 app. Its texts live in `Languages/*.json`.
- `tests/PhotoGallery.Core.Tests` – tests that write and read real JPEG, HEIC, PNG, WebP, TIFF and GIF files.
  They need ExifTool on the PATH and are skipped otherwise.
- `installer/` – the Windows installer. `scripts/` – ExifTool download, version numbers, language files.

## Credits

Code created by [Claude Code](https://claude.com/claude-code) from Anthropic.

It builds on the work of others (Settings › Info lists the same):

| Component | Used for | Licence |
|---|---|---|
| [ExifTool](https://exiftool.org) by Phil Harvey | Reading and writing metadata | Perl Artistic License / GPL |
| [.NET](https://dotnet.microsoft.com), [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (WinUI 3) | The app itself | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Linking the windows to the data | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), [SQLite](https://www.sqlite.org) | The search index | MIT, public domain |
| [ImageSharp](https://github.com/SixLabors/ImageSharp) by Six Labors | Rotating, cropping and saving | Six Labors Split License (Apache 2.0) |
| [ONNX Runtime](https://onnxruntime.ai) with DirectML | Running the AI models | MIT |
| [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning) | Text input for tagging | MIT |
| [SigLIP 2](https://huggingface.co/google/siglip2-base-patch16-224) by Google ([ONNX version](https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX)) | Automatic tagging | Apache 2.0 |
| [YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) and [SFace](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface) (OpenCV Zoo) | Finding and recognising faces | MIT, Apache 2.0 |
| [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) by Xintao Wang et al. (versions from [Qualcomm AI Hub](https://huggingface.co/qualcomm)) | AI upscaling in the repair wizard | BSD 3-Clause |
| [Leaflet](https://leafletjs.com), [MapLibre GL JS](https://maplibre.org) | The map | BSD 2-Clause, BSD 3-Clause |
| [OpenStreetMap](https://www.openstreetmap.org/copyright) | Map data, © OpenStreetMap contributors | ODbL |
| [OpenFreeMap](https://openfreemap.org) | Map tiles | MIT, ODbL |
| [Sentinel-2 cloudless](https://s2maps.eu) by EOX IT Services GmbH | Satellite pictures (contains modified Copernicus Sentinel data) | CC BY-NC-SA 4.0 |
| [Esri World Imagery](https://www.esri.com/en-us/legal/terms/full-master-agreement) | Sharp satellite pictures, when the app has an Esri key | Esri terms of use |
| [Nominatim](https://nominatim.org) | Place names and positions | GPL 2.0 (service), ODbL (data) |
| [Unicode CLDR](https://cldr.unicode.org) | Country names in many languages | Unicode License v3 |
| [Inno Setup](https://jrsoftware.org/isinfo.php) by Jordan Russell | The installer | Inno Setup License |

The AI models are downloaded from their makers on first use and are not part of this repository.

## Licence

HolidayPhotoGallery is free and open source under the [Apache 2.0 licence](LICENSE).
