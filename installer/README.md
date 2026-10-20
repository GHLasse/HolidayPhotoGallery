# Windows installer

`HolidayPhotoGallery.iss` is an [Inno Setup 6](https://jrsoftware.org/isinfo.php) script that turns the
app build into `HolidayPhotoGallery-Setup-x64.exe`.

What the installer does:

- Installs for the current user only, so it needs no administrator rights. The app goes to
  `%LocalAppData%\Programs\HolidayPhotoGallery`.
- Adds a Start menu entry, and a desktop icon if the user ticks the box.
- Registers the app with Windows (App Paths), so Run and "Open with" find `HolidayPhotoGallery.exe`.
- Shows up in Settings > Apps > Installed apps, with icon and version, where it can be uninstalled.
- Updates an existing install in place (an open app is closed first and started again afterwards).
- On uninstall, asks whether to also remove settings, the photo index and the downloaded models in
  `%LocalAppData%\HolidayPhotoGallery` (default: keep). Photos are never touched.
- Speaks English, German and Spanish, picked from the Windows language.

Silent install for IT use: `HolidayPhotoGallery-Setup-x64.exe /VERYSILENT`; add `/ALLUSERS` (as administrator)
to install for every user in Program Files instead.

## Making a release

1. On GitHub, open Actions > Build > Run workflow, pick the `main` branch, and enter a version such as `1.0.0`.
2. The workflow builds and tests as usual, then creates the tag `v1.0.0` and the GitHub Release
   "Holiday Photo Gallery 1.0.0" with the installer and the zip attached, and release notes made from the
   merged pull requests.

The file names are the same for every release, so these links always give the newest one:

- https://github.com/GHLasse/HolidayPhotoGallery/releases/latest/download/HolidayPhotoGallery-Setup-x64.exe
- https://github.com/GHLasse/HolidayPhotoGallery/releases/latest/download/HolidayPhotoGallery-win-x64.zip

Every pull request build also has the installer as the `HolidayPhotoGallery-Setup-x64` artifact
(version `0.0.<run number>`), for testing before a release.

## Building on your own PC

Install Inno Setup 6, build the app in Release x64, then:

```
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 installer\HolidayPhotoGallery.iss
```

The installer lands in `installer\Output`.

## Not signed yet

The installer and app are not code-signed, so Windows SmartScreen shows "Windows protected your PC" on
first run; users click "More info" and "Run anyway". A code-signing certificate (or Azure Trusted
Signing) would remove that warning.
