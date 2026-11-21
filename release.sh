#!/usr/bin/env bash
# One command for a release (PLAN.md Phase 8): checks that the versions agree, runs every test
# suite, builds the agent zip and the Mac server, then prints the zip's SHA-256 and the Update
# line. Stops at the first failure. It does not push, tag or deploy anything.
#
#   ./release.sh                 everything
#   ./release.sh --no-build      versions and tests only; no zip, no mac-mcp/dist
#   ./release.sh --skip-dotnet   leave out the .NET tests and the agent build (not a release)
#   ./release.sh --dry-run       check the versions, print every step, run nothing else
#
# Versions that must agree: VERSION, vm-agent/Directory.Build.props <Version>,
# mac-mcp/package.json and mac-mcp/package-lock.json.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO"

NO_BUILD=0
SKIP_DOTNET=0
DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --no-build) NO_BUILD=1 ;;
    --skip-dotnet) SKIP_DOTNET=1 ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "release.sh: unknown argument $arg (see --help)" >&2; exit 2 ;;
  esac
done

die() { echo "release.sh: FAILED: $*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }
# Run a command, or only show it with --dry-run. With set -e, a failing command ends the script.
run() {
  echo "+ $*"
  if [ "$DRY_RUN" -eq 1 ]; then return 0; fi
  "$@"
}

for tool in node npm python3 shasum unzip; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool not found"
done

# ------------------------------------------------------------------------------ versions
step "Checking versions"
[ -f VERSION ] || die "no VERSION file at the repo root"
VERSION="$(tr -d '[:space:]' < VERSION)"
printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || die "VERSION is not x.y.z: '$VERSION'"

AGENT_VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' vm-agent/Directory.Build.props | head -n 1 | tr -d '[:space:]')"
PKG_VERSION="$(node -p 'require("./mac-mcp/package.json").version')"
LOCK_VERSION="$(node -p 'require("./mac-mcp/package-lock.json").version')"
LOCK_ROOT_VERSION="$(node -p 'require("./mac-mcp/package-lock.json").packages[""].version')"

VERSIONS_OK=1
check_version() {
  if [ "$2" = "$VERSION" ]; then
    printf '  %-48s %s\n' "$1" "$2"
  else
    printf '  %-48s %s   <-- differs from VERSION (%s)\n' "$1" "${2:-(none)}" "$VERSION"
    VERSIONS_OK=0
  fi
}
check_version "VERSION" "$VERSION"
check_version "vm-agent/Directory.Build.props <Version>" "$AGENT_VERSION"
check_version "mac-mcp/package.json" "$PKG_VERSION"
check_version "mac-mcp/package-lock.json" "$LOCK_VERSION"
check_version "mac-mcp/package-lock.json packages[\"\"]" "$LOCK_ROOT_VERSION"
if [ "$VERSIONS_OK" -ne 1 ]; then
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "  (dry run: a real run stops here)"
  else
    die "versions do not agree"
  fi
fi

# ------------------------------------------------------------------------------ .NET
if [ "$SKIP_DOTNET" -eq 0 ]; then
  # Find the .NET 10 SDK the way vm-agent/build.sh does: PATH first, then the Homebrew keg.
  if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x /opt/homebrew/opt/dotnet@10/bin/dotnet ]; then
      export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet@10/libexec}"
      export PATH="/opt/homebrew/opt/dotnet@10/bin:$PATH"
    elif [ "$DRY_RUN" -eq 0 ]; then
      die "dotnet not found (need the .NET 10 SDK; or --skip-dotnet, which is not a release)"
    fi
  fi
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  step "Agent tests (dotnet)"
  run dotnet test vm-agent/EdiabasAgent.Tests
fi

# ------------------------------------------------------------------------------ Node
[ -d mac-mcp/node_modules ] || die "mac-mcp/node_modules is missing: run (cd mac-mcp && npm ci) first"

step "MCP server: typecheck and tests"
run npm --prefix mac-mcp run typecheck
run npm --prefix mac-mcp test

step "Dev tools: deploy server (python), replay agent and scrubber (TypeScript)"
run python3 -m unittest dev/test_serve.py
run npm --prefix mac-mcp run typecheck:dev
run npm --prefix mac-mcp run test:dev

# ------------------------------------------------------------------------------ build
ZIP="release/EdiabasAgent.zip"
if [ "$NO_BUILD" -eq 0 ]; then
  if [ "$SKIP_DOTNET" -eq 0 ]; then
    step "Building the agent zip"
    run vm-agent/build.sh
  fi
  step "Building the MCP server (mac-mcp/dist)"
  run npm --prefix mac-mcp run build
fi

# ------------------------------------------------------------------------------ summary
step "Release $VERSION"
if [ "$DRY_RUN" -eq 1 ]; then
  echo "Dry run: nothing was run after the version check."
  [ "$VERSIONS_OK" -eq 1 ] || exit 1
  exit 0
fi

BUILT=$(( NO_BUILD == 0 && SKIP_DOTNET == 0 ))
if [ -f "$ZIP" ]; then
  ZIP_VERSION="$(unzip -p "$ZIP" version.txt | tr -d '[:space:]')"
  SHA="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
  if [ "$BUILT" -eq 1 ]; then
    [ "$ZIP_VERSION" = "$VERSION" ] || die "$ZIP holds version.txt '$ZIP_VERSION', expected $VERSION"
    if [ -f "$ZIP.sha256" ]; then
      [ "$(tr -d '[:space:]' < "$ZIP.sha256")" = "$SHA" ] || die "$ZIP.sha256 does not match the zip"
    fi
    echo "Agent zip:  $ZIP (version $ZIP_VERSION)"
  else
    echo "Agent zip:  $ZIP from an earlier build, NOT rebuilt this run (its version.txt: ${ZIP_VERSION:-none})"
  fi
  echo "SHA-256:    $SHA"
  echo
  echo "Update line (paste into Windows PowerShell as administrator in the VM while"
  echo "'python3 dev/serve.py' runs; serve.py prints it with the Mac's address filled in):"
  echo "  \$base='http://<Mac bridge address>:8000'; \$sha='$SHA'; irm \$base/update.ps1 | iex"
else
  echo "Agent zip:  not built (no $ZIP)"
fi

if [ "$BUILT" -eq 1 ]; then
  echo
  echo "Release $VERSION built and tested. Nothing was pushed, tagged or deployed."
else
  echo
  echo "Checks passed, but this is NOT a release:$([ "$NO_BUILD" -eq 1 ] && echo ' --no-build')$([ "$SKIP_DOTNET" -eq 1 ] && echo ' --skip-dotnet')."
fi
