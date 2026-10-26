# Contributing

## Branches

| Branch | Holds | Built as |
| --- | --- | --- |
| `main` | Released versions only. Protected: changes come only through the release pull request. | GitHub Release `v<VERSION>` |
| `prep` | Everything finished and tested for the next release. | Pre-release `experimental` |
| feature and fix branches | One change each, e.g. `fix/rescan` or `feature/updates`. | Test build on the pull request |

## Making a change

1. Branch off `prep`.
2. Open a pull request into `prep`. The Build workflow builds and tests it; the installer and the zip are
   attached to the run as artifacts for trying out.
3. When it is merged, the build on `prep` replaces the `experimental` pre-release with the new version
   (`VERSION` plus the build number, e.g. 0.1.0.131).

## Making a release

1. In `prep`, set `VERSION` to the new version (e.g. `0.2.0`), through a pull request like any other change.
2. Open a pull request from `prep` into `main`, titled e.g. "Release 0.2.0".
3. Merging it builds the app once more and publishes GitHub Release `v0.2.0` with
   `HolidayPhotoGallery-Setup-x64.exe` and `HolidayPhotoGallery-win-x64.zip`. A version that was already
   released is refused, so a forgotten `VERSION` change can't overwrite a release.

## Before pushing

- Run the core tests: `dotnet test tests/PhotoGallery.Core.Tests` (they need ExifTool on the PATH).
- After changing any text shown in the app, run `python scripts/update-languages.py` and translate the new
  entries in every file in `src/PhotoGallery.App/Languages` (see [docs/languages.md](docs/languages.md)).
- Never put API keys or client secrets in the code: the built-in Google and Microsoft keys come from the
  repository's secrets at build time.
