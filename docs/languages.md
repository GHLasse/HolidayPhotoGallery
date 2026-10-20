# Languages

The app's texts are written in English in the code and looked up in a language file when the app starts.
Settings › Language picks the language; it is used from the next start.

Built in: English, Deutsch, Español, العربية (shown right to left) and 简体中文
(`src/PhotoGallery.App/Languages/*.json`).

## Adding a language without changing the app

1. In the app, open Settings › Language › Open the languages folder
   (`%LocalAppData%\HolidayPhotoGallery\Languages`). The app puts `template.json` there, the list of every text.
2. Copy it and name the copy after the language, for example `fr.json`.
3. Fill in `"code"` (for example `"fr"`), `"name"` (the language's own name, `"Français"`) and, for a language
   written from right to left, `"rightToLeft": true`.
4. Translate each text. Keep placeholders such as `{0}` or `{1:N0}`; their order may change. A text left empty stays English.
5. Restart the app and pick the language in Settings.

A file in this folder also replaces a built-in language with the same code, for example to correct a translation.

## For developers

Wrap every text a user reads: `Tr("Saved {0} photo(s)", count)` in C# (with
`using static PhotoGallery.Core.Localization.Strings;`) and `{local:Loc Text='Settings'}` in XAML.
Then run `python3 scripts/update-languages.py`: it rebuilds `template.json`, adds new texts to each language file
with an empty translation and drops texts no longer used. `LanguageFilesTests` fails until every shipped language
translates every text with the same placeholders.
