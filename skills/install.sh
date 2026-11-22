#!/usr/bin/env bash
# Installs this repo's Claude skills.
#  - Links each skill directory (one that has a SKILL.md) into the Claude Code skills folder,
#    so edits in the repo take effect at once. Re-running replaces this repo's own links and
#    refuses to touch anything else that is already there.
#  - Builds one <skill>.skill file per skill in skills/dist/ for upload to claude.ai (desktop
#    app and cloud sessions): a zip archive under the extension claude.ai's upload accepts.
#    The user does the upload.
#
# Usage: skills/install.sh [--dest <dir>] [--no-zip] [--no-link]
#   --dest <dir>  where to link the skills (default: ~/.claude/skills)
#   --no-zip      do not build the .skill files
set -euo pipefail

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; }

SKILLS_DIR="$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
DEST="${HOME}/.claude/skills"
DO_LINK=1
DO_ZIP=1

while [ $# -gt 0 ]; do
  case "$1" in
    --dest) [ $# -ge 2 ] || { echo "--dest needs a directory" >&2; exit 2; }; DEST="$2"; shift 2 ;;
    --no-zip) DO_ZIP=0; shift ;;
    --no-link) DO_LINK=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# A skill is a directory with a SKILL.md; evals/ and dist/ are not.
skills=()
for d in "$SKILLS_DIR"/*/; do
  [ -f "$d/SKILL.md" ] && skills+=("$(basename "$d")")
done
[ ${#skills[@]} -gt 0 ] || { echo "No skills found in $SKILLS_DIR" >&2; exit 1; }

# True if the link at $1 points into this repo's skills folder (even if its target is gone,
# for example a renamed skill), so it is ours to replace.
is_our_link() {
  local raw resolved
  raw="$(readlink "$1")"
  case "$raw" in "$SKILLS_DIR"/*) return 0 ;; esac
  if [ -d "$1" ]; then
    resolved="$(cd -P "$1" && pwd -P)"
    case "$resolved" in "$SKILLS_DIR"/*) return 0 ;; esac
  fi
  return 1
}

failed=0

if [ "$DO_LINK" -eq 1 ]; then
  mkdir -p "$DEST"
  for name in "${skills[@]}"; do
    src="$SKILLS_DIR/$name"
    target="$DEST/$name"
    if [ -L "$target" ]; then
      if ! is_our_link "$target"; then
        echo "REFUSED  $target is a link to $(readlink "$target"), not to this repo; left alone" >&2
        failed=1; continue
      fi
      if [ "$(readlink "$target")" = "$src" ]; then
        echo "ok       $target -> $src"; continue
      fi
      ln -sfn "$src" "$target"   # -n: replace the link itself, not write inside its target
      echo "relinked $target -> $src"
    elif [ -e "$target" ]; then
      echo "REFUSED  $target exists and is not a link; left alone (move it away to install)" >&2
      failed=1
    else
      ln -s "$src" "$target"
      echo "linked   $target -> $src"
    fi
  done
fi

if [ "$DO_ZIP" -eq 1 ]; then
  command -v zip >/dev/null || { echo "zip not found; cannot build the .skill files" >&2; exit 1; }
  dist="$SKILLS_DIR/dist"   # git-ignored
  mkdir -p "$dist"
  for name in "${skills[@]}"; do
    # A .skill file is a zip holding the skill folder at its top level, the layout claude.ai
    # expects. Built in a fresh temporary folder and moved into place, so a failed build never
    # leaves a half-written file and a re-run never appends to an old one.
    tmpd="$(mktemp -d)"
    (cd "$SKILLS_DIR" && zip -q -r -X "$tmpd/$name.zip" "$name" -x '*.DS_Store')
    mv -f "$tmpd/$name.zip" "$dist/$name.skill"
    rmdir "$tmpd"
    rm -f "$dist/$name.zip"   # left by 0.6.2 and earlier; claude.ai's upload does not take it
    echo "built    $dist/$name.skill"
  done
fi

exit "$failed"
