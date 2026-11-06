#!/usr/bin/env bash
# Works out the version a build carries (see CONTRIBUTING.md, "Version numbers"): MAJOR.MINOR.BUILD.
# - A push to main (a release) raises MINOR by one: v0.1.0, v0.2.0, ... When VERSION holds a higher MAJOR
#   than the newest release (raised only when Lasse and Claude agree the app is ready), it becomes MAJOR.0.0.
# - Every other build keeps the newest release's MAJOR.MINOR and counts BUILD up with the run number:
#   after v0.1.0, the experimental builds are 0.1.140, 0.1.141, ... and v0.2.0 is newer than all of them.
# - A version typed in by hand (Actions > Build > Run workflow) wins.
# Usage: version.sh <event> <branch> <run number> [version typed in]
set -euo pipefail
event="$1" branch="$2" run="$3" typed="${4:-}"

if [ -n "$typed" ]; then
  version="${typed#v}"
else
  major="$(tr -d '[:space:]' < VERSION)"
  [[ "$major" =~ ^[0-9]+$ ]] || { echo "VERSION must hold the major version only, such as 0" >&2; exit 1; }
  latest="$(git ls-remote --tags --refs origin 'v*' | sed 's#.*refs/tags/v##' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -n 1 || true)"
  IFS=. read -r last_major last_minor _ <<< "${latest:-0.0.0}"
  if [ "$event" = push ] && [ "$branch" = main ]; then
    if [ "$major" -gt "$last_major" ]; then
      version="$major.0.0"
    else
      version="$last_major.$((last_minor + 1)).0"
    fi
  else
    version="$last_major.$last_minor.$run"
  fi
fi

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version '$version' must look like 0.2.0" >&2; exit 1; }
echo "$version"
