#!/usr/bin/env python3
"""Collects every text the app shows (Tr("...") in C#, {local:Loc Text='...'} in XAML) into
src/PhotoGallery.App/Languages/template.json, and brings each language file in that folder up to date:
new texts are added with an empty translation (shown in English until translated), texts the app no
longer uses are dropped. Run it after adding or changing texts, then fill in the empty translations."""
import json, pathlib, re, sys

root = pathlib.Path(__file__).resolve().parent.parent
languages = root / "src" / "PhotoGallery.App" / "Languages"
CS = re.compile(r'\bTr\(\s*"((?:[^"\\\n]|\\.)*)"')
XAML = re.compile(r"\{local:Loc Text='((?:[^'\\]|\\.)*)'\}")

def unescape_cs(s):
    s = re.sub(r'\\u([0-9a-fA-F]{4})', lambda m: '\\x00u' + m.group(1), s)
    s = re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', '"': '"', '\\': '\\', "'": "'", '0': '\0'}.get(m.group(1), m.group(0)), s)
    return re.sub(r'\\x00u([0-9a-fA-F]{4})', lambda m: chr(int(m.group(1), 16)), s)

def unescape_xaml(s):
    return s.replace("\\'", "'").replace('&amp;', '&').replace('&lt;', '<').replace('&gt;', '>').replace('&quot;', '"').replace('&#x0a;', '\n').replace('&#10;', '\n')

texts = set()
for f in (root / "src").rglob("*.cs"):
    if "/obj/" in f.as_posix() or "/bin/" in f.as_posix():
        continue
    texts.update(unescape_cs(m) for m in CS.findall(f.read_text(encoding="utf-8")))
for f in (root / "src").rglob("*.xaml"):
    if "/obj/" in f.as_posix() or "/bin/" in f.as_posix():
        continue
    texts.update(unescape_xaml(m) for m in XAML.findall(f.read_text(encoding="utf-8")))

keys = sorted(texts, key=str.casefold)
template = {"code": "", "name": "", "rightToLeft": False, "texts": {k: "" for k in keys}}
(languages / "template.json").write_text(json.dumps(template, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

for f in sorted(languages.glob("*.json")):
    if f.name == "template.json":
        continue
    data = json.loads(f.read_text(encoding="utf-8"))
    old = data.get("texts", {})
    data["texts"] = {k: old.get(k, "") for k in keys}
    missing = sum(1 for k in keys if not data["texts"][k])
    f.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{f.name}: {len(keys) - missing} of {len(keys)} translated")
print(f"{len(keys)} texts")
