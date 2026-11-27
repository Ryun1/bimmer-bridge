#!/usr/bin/env bash
# Fails if any path tracked in this repo (not recursing into submodules) is a BMW
# SGBD or library file: .prg, .grp or .lib, in any case. Run before every commit.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
# --cached includes staged additions; a submodule is one gitlink entry, never its contents.
found=$(git ls-files --cached | grep -iE '\.(prg|grp|lib)$' || true)
if [ -n "$found" ]; then
  echo "BMW files are tracked; remove them before committing:" >&2
  printf '%s\n' "$found" >&2
  exit 1
fi
exit 0
