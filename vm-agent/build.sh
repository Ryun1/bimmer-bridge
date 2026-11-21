#!/usr/bin/env bash
# Builds the VM agent release artefact (docs/deploy.md):
#   release/EdiabasAgent.zip         EdiabasAgent.exe and version.txt at the zip root
#   release/EdiabasAgent.zip.sha256  the hex digest, one line
# Publishes Release, win-x86, self-contained, single file. Fails loudly on any error.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
RELEASE="$REPO/release"
PROJECT="$HERE/EdiabasAgent/EdiabasAgent.csproj"

die() { echo "build.sh: ERROR: $*" >&2; exit 1; }

# Find the .NET 10 SDK: PATH first, then the Homebrew keg.
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x /opt/homebrew/opt/dotnet@10/bin/dotnet ]; then
    export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet@10/libexec}"
    export PATH="/opt/homebrew/opt/dotnet@10/bin:$PATH"
  else
    die "dotnet not found (need the .NET 10 SDK)"
  fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

for tool in zip unzip shasum; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool not found"
done

STAGE="$(mktemp -d "${TMPDIR:-/tmp}/ediabas-build.XXXXXX")"
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

VERSION="$(dotnet msbuild "$PROJECT" -nologo -getProperty:Version)"
VERSION="$(printf '%s' "$VERSION" | tr -d '[:space:]')"
[ -n "$VERSION" ] || die "could not read Version from the project"

echo "==> Publishing EdiabasAgent $VERSION (Release, win-x86, self-contained, single file)"
dotnet publish "$PROJECT" \
  -c Release \
  -r win-x86 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -p:TreatWarningsAsErrors=true \
  -o "$STAGE/publish"

EXE="$STAGE/publish/EdiabasAgent.exe"
[ -f "$EXE" ] || die "publish did not produce EdiabasAgent.exe"

# Single file means single file: anything else beside the exe would be missing in the VM.
EXTRA="$(cd "$STAGE/publish" && find . -type f ! -name 'EdiabasAgent.exe' | sort)"
[ -z "$EXTRA" ] || die "publish produced files besides EdiabasAgent.exe: $EXTRA"

mkdir -p "$STAGE/zip" "$RELEASE"
cp "$EXE" "$STAGE/zip/EdiabasAgent.exe"
printf '%s\r\n' "$VERSION" > "$STAGE/zip/version.txt"

ZIP_TMP="$STAGE/EdiabasAgent.zip"
(cd "$STAGE/zip" && zip -X -q "$ZIP_TMP" EdiabasAgent.exe version.txt)

# The zip must contain exactly these two entries at its root.
ENTRIES="$(unzip -Z1 "$ZIP_TMP" | sort | tr '\n' ' ')"
[ "$ENTRIES" = "EdiabasAgent.exe version.txt " ] || die "unexpected zip contents: $ENTRIES"

SHA="$(shasum -a 256 "$ZIP_TMP" | awk '{print $1}')"
[ "${#SHA}" -eq 64 ] || die "could not compute SHA-256"

mv -f "$ZIP_TMP" "$RELEASE/EdiabasAgent.zip"
printf '%s\n' "$SHA" > "$RELEASE/EdiabasAgent.zip.sha256"

SIZE="$(wc -c < "$RELEASE/EdiabasAgent.zip" | tr -d ' ')"
echo "==> $RELEASE/EdiabasAgent.zip ($SIZE bytes)"
echo "==> version $VERSION"
echo "==> SHA-256 $SHA"
