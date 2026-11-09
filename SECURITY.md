# Security and privacy

## Reporting a problem

Please report a security problem privately through GitHub: **Security › Report a vulnerability** on this
repository. Don't open a public issue for it.

## What stays on your PC

Holiday Photo Gallery keeps everything it knows in the photo files themselves and in a rebuildable index in
`%LocalAppData%\HolidayPhotoGallery` (the index database, face crops for the People view, settings, logs and the
downloaded models). Nothing in that folder is sent anywhere.

- **Private photos** are hidden in the app until private mode is turned on. They are *not* encrypted: File
  Explorer, other apps and the index still have them.
- **Logs** (`activity.log`, `crash.log`) note what the app did, with file paths. Your user folder is written as
  `%USERPROFILE%`, so the logs don't contain your Windows user name. Look at a log before you send it to anyone.
- **Sign-ins** to Google Photos and OneDrive are kept in Windows Credential Manager, encrypted for your Windows
  account. Sign out in Settings › Linked apps to remove them.

## What leaves your PC, and when

| What | Where to | When |
| --- | --- | --- |
| Photos you upload | Google Photos or OneDrive (your own account) | Only when you upload. With Settings › Linked apps › *Leave out the GPS position*, a copy without the position is sent. |
| Photos you share | The app you pick in the Windows share dialog | Only when you share; the same GPS setting applies. |
| A photo's GPS position | OpenStreetMap Nominatim | When you look up place names, and in the background for photos without place names when Settings › Dates and places › “Add place names … by themselves” is on (off by default). |
| The part of the world the map shows | The map's tile servers (OpenStreetMap, OpenFreeMap, Esri, EOX) | While the map is open. |
| What you type in the map's Find box | OpenStreetMap Nominatim | When you search for a place. |
| A photo's GPS position | Open-Meteo (elevation service) | With the place names (by hand or in the background), only for photos without a height of their own, to add the ground's height. |
| Requests for the pages and pictures of a website | Only the site whose address you enter in Library › *Import from a website* (and its own subdomains for pictures), one request at a time, as the app “HolidayPhotoGallery”; links and redirects to other sites are not followed, and its robots.txt is honoured. When the site asks for a sign-in or a human check, you can open its page in the app (the same built-in browser as the map, whose data stays in the app’s data folder); the site’s cookies from that page then go with the app’s requests to that site only. A photo page that builds its photo with a script is opened in that browser out of sight, like a browser tab would, so the page’s own scripts and the files it loads run as they would in your browser | Only while you import from that site. Nothing about your photos or library is sent. |
| The app's version | GitHub | When the app looks for updates (can be turned off in Settings). |
| Downloads of the recognition models | Hugging Face, GitHub, Qualcomm AI Hub | Once, when you agree to the download. Each file is checked against its SHA-256. |

Auto-tagging, face recognition, duplicate finding, quality hints, upscaling and the magic eraser run on your PC; no photo is sent
anywhere for them.

## How the app protects itself

- Updates come from this repository's GitHub releases and are checked against the release's SHA-256 before they
  are installed.
- Downloaded models are pinned by SHA-256; the map's scripts are pinned by subresource integrity hashes.
- Links stored in album files are only opened when they are https addresses.
- ExifTool gets each value as its own line, so file names and tags can't turn into options.
