#!/usr/bin/env bash
# Decides what this run has to do, and writes two answers to $GITHUB_OUTPUT:
#
#   code=true|false    whether there is anything to build or test at all.
#   native=true|false  whether the phone, TV and car apps (the Native clients workflow) are affected.
#
# A change that only touches engineering/ (specs, handoffs, plans, the validation batch list) has nothing to build
# or test, whether it arrives as a pull request or as a direct push to main. Tests read config/, docs/reference/
# data files, engineering/design, clients/ and root files, so everything else still runs the full set.
#
# The apps are affected when the change touches clients/, the Engine routes or wire types they use, their fixtures
# and gates, or the files that decide and run these checks.
#
# Nightly and manual runs always run everything.
#
# Arguments: <event name> <pull request base branch> <push "before" commit>
set -euo pipefail

event="${1:-}"
base_ref="${2:-}"
before="${3:-}"

native_paths='^(clients/|src/MediaEngine\.Api/Endpoints/|src/MediaEngine\.Contracts/|tests/fixtures/native-client-v1/|tests/native/|\.github/workflows/(native-clients|ci)\.yml$|\.github/scripts/ci-scope\.sh$)'

# Reads the changed file names on standard input and prints both answers.
decide() {
  local changed other
  changed=$(cat)
  other=$(printf '%s\n' "$changed" | grep -v -e '^engineering/' -e '^$' || true)
  if [ -z "$other" ]; then echo "code=false"; else echo "code=true"; fi
  if printf '%s\n' "$changed" | grep -q -E "$native_paths"; then echo "native=true"; else echo "native=false"; fi
}

everything() {
  echo "code=true"
  echo "native=true"
}

case "$event" in
  pull_request)
    result=$(git diff --name-only "origin/${base_ref}...HEAD" | decide)
    ;;
  push)
    # A first push, a force push or a missing commit cannot be compared: run everything.
    if [ -z "$before" ] || ! git cat-file -e "${before}^{commit}" 2>/dev/null; then
      result=$(everything)
    else
      changed=$(git diff --name-only "$before" HEAD)
      if [ -z "$changed" ]; then result=$(everything); else result=$(printf '%s\n' "$changed" | decide); fi
    fi
    ;;
  *)
    result=$(everything)
    ;;
esac

echo "$result"
echo "$result" >> "${GITHUB_OUTPUT:-/dev/null}"
