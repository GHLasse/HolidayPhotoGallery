# Contributing

## Branches

| Branch | Holds | Built as |
| --- | --- | --- |
| `main` | Released versions only. Protected: changes come only through the release pull request. | GitHub Release `v0.MINOR.0` |
| `prep` | Everything finished and tested for the next release. | Pre-release `experimental` |
| feature and fix branches | One change each, e.g. `fix/rescan` or `feature/updates`. | Test build on the pull request |

## Making a change

1. Branch off `prep`.
2. Open a pull request into `prep`. The Build workflow builds and tests it; the installer and the zip are
   attached to the run as artifacts for trying out.
3. When it is merged, the build on `prep` replaces the `experimental` pre-release with the new version
   (e.g. 0.1.140).

## Version numbers

Versions are `MAJOR.MINOR.BUILD`, worked out by the build (`scripts/version.sh`) from the newest release:

- Every build from `prep` (and every pull request) keeps the newest release's `MAJOR.MINOR` and takes the
  build's run number as `BUILD`: after v0.1.0 the test versions are 0.1.140, 0.1.141, and so on.
- Only a release (a push to `main`) raises `MINOR`: v0.1.0, then v0.2.0. It happens when everything planned
  for that version is in `prep` and tested.
- `VERSION` holds only the major version, `0`. It becomes `1` only when Lasse and Claude agree that the app
  has changed enough for 1.0; the next release is then v1.0.0.

The app's updater relies on this order: a release (BUILD 0) is newer than every test version before it.

## Making a release

1. Open a pull request from `prep` into `main`, titled e.g. "Release 0.2.0".
2. Merging it builds the app once more and publishes GitHub Release `v0.2.0` with
   `HolidayPhotoGallery-Setup-x64.exe`, its `.sha256` checksum and `HolidayPhotoGallery-win-x64.zip`.
   The app offers it to everyone on the Stable channel (Settings › App › Updates).

## Before pushing

- Run the core tests: `dotnet test tests/PhotoGallery.Core.Tests` (they need ExifTool on the PATH).
- After changing any text shown in the app, run `python scripts/update-languages.py` and translate the new
  entries in every file in `src/PhotoGallery.App/Languages` (see [docs/languages.md](docs/languages.md)).
- Never put API keys or client secrets in the code: the built-in Google and Microsoft keys come from the
  repository's secrets at build time.
