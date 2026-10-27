'''Writes src/PhotoGallery.Core/Geocoding/countries.json: every country's name in the app languages, its local
name and other spellings, from the Unicode CLDR data in Babel (pip install babel). Run it again to update the list.'''
import json, sys
from babel import Locale
from babel.languages import get_official_languages
main = {'en':'en','de':'de','es':'es','ar':'ar','zh-Hans':'zh_Hans'}
extra = ['fr','it','nl','pt','pl','cs','sk','sv','da','nb','fi','hu','ro','hr','sl','el','ru','uk','tr','ja','ko','th','vi','id','he','bg','sr','lt','lv','et','is','ga','ca','ms','hi','fa','zh_Hant']
en = Locale.parse('en').territories
# Three-letter codes ("DEU"), which some apps write as the country code instead of two letters.
from babel.core import get_global
alpha3 = {two[0]: three for three, two in get_global('territory_aliases').items()
          if len(three) == 3 and three.isalpha() and len(two) == 1 and len(two[0]) == 2}
# Names other apps and older files use that the CLDR names don't cover.
ALIASES = {
    'US': ['USA', 'United States of America', 'Vereinigte Staaten von Amerika'],
    'GB': ['UK', 'Great Britain', 'Großbritannien', 'England', 'Scotland', 'Schottland', 'Wales'],
    'CN': ["People's Republic of China", 'Volksrepublik China'],
    'CZ': ['Czech Republic', 'Tschechische Republik'],
    'NL': ['Holland'],
    'KR': ['Korea', 'South Korea', 'Südkorea'],
    'RU': ['Russia', 'Russian Federation'],
}
out = {}
for code in sorted(en):
    if len(code) != 2 or not code.isalpha() or code in ('EU','EZ','UN','ZZ','QO','XA','XB','XK'):
        if code != 'XK':
            continue
    entry = {k: Locale.parse(v).territories.get(code) for k, v in main.items()}
    local = None
    for lang in get_official_languages(code, de_facto=True):
        try:
            local = Locale.parse(lang).territories.get(code)
        except Exception:
            local = None
        if local:
            break
    entry['local'] = local or entry['en']
    seen = {n.casefold() for n in entry.values() if n}
    also = []
    for l in extra:
        n = Locale.parse(l).territories.get(code)
        if n and n.casefold() not in seen:
            seen.add(n.casefold()); also.append(n)
    if code in alpha3:
        entry['a3'] = alpha3[code]
    for n in ALIASES.get(code, []):
        if n.casefold() not in seen:
            seen.add(n.casefold()); also.append(n)
    if also:
        entry['also'] = also
    out[code] = entry
json.dump(out, open(sys.argv[1] if len(sys.argv) > 1 else 'src/PhotoGallery.Core/Geocoding/countries.json', 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
print(len(out))
