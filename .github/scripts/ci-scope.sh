#!/usr/bin/env bash
# Decides whether this run has anything to build or test, and writes code=true|false to $GITHUB_OUTPUT.
#
# A change that only touches engineering/ (specs, handoffs, plans, the validation batch list) has nothing to build
# or test, whether it arrives as a pull request or as a direct push to main. Tests read config/, docs/reference/
# data files, engineering/design, clients/ and root files, so everything else still runs the full set.
# Nightly and manual runs always run everything.
#
# Arguments: <event name> <pull request base branch> <push "before" commit>
set -euo pipefail

event="${1:-}"
base_ref="${2:-}"
before="${3:-}"

decide() {
  local other
  other=$(grep -v -e '^engineering/' -e '^$' || true)
  if [ -z "$other" ]; then echo "code=false"; else echo "code=true"; fi
}

case "$event" in
  pull_request)
    result=$(git diff --name-only "origin/${base_ref}...HEAD" | decide)
    ;;
  push)
    # A first push, a force push or a missing commit cannot be compared: run everything.
    if [ -z "$before" ] || ! git cat-file -e "${before}^{commit}" 2>/dev/null; then
      result="code=true"
    else
      changed=$(git diff --name-only "$before" HEAD)
      if [ -z "$changed" ]; then result="code=true"; else result=$(printf '%s\n' "$changed" | decide); fi
    fi
    ;;
  *)
    result="code=true"
    ;;
esac

echo "$result"
echo "$result" >> "${GITHUB_OUTPUT:-/dev/null}"
