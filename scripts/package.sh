#!/usr/bin/env bash
set -euo pipefail

RUNTIME="${1:-linux-x64}"
CONFIG="${2:-Release}"
OUTPUT_DIR="${3:-dist}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
FULL_OUTPUT_DIR="$REPO_ROOT/$OUTPUT_DIR"
STAGE_DIR="$REPO_ROOT/temp-stage-$RUNTIME"

echo "==> Packaging Garf for $RUNTIME ($CONFIG) ..."

rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR"
mkdir -p "$FULL_OUTPUT_DIR"

echo "==> Publishing Garf.Indexer ($RUNTIME) ..."
dotnet publish "$REPO_ROOT/src/Garf.Indexer/Garf.Indexer.csproj" \
    -c "$CONFIG" \
    -r "$RUNTIME" \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$STAGE_DIR"

rm -f "$STAGE_DIR"/*.pdb

echo "==> Staging ts-indexer ..."
TS_SOURCE="$REPO_ROOT/ts-indexer"
TS_STAGE="$STAGE_DIR/ts-indexer"
mkdir -p "$TS_STAGE"

if command -v npm >/dev/null 2>&1; then
    if [ ! -d "$TS_SOURCE/node_modules" ]; then
        echo "==> Running npm install in $TS_SOURCE ..."
        (cd "$TS_SOURCE" && npm install --omit=dev)
    fi
fi

cp "$TS_SOURCE/index.mjs" "$TS_STAGE/"
cp "$TS_SOURCE/package.json" "$TS_STAGE/"
[ -f "$TS_SOURCE/package-lock.json" ] && cp "$TS_SOURCE/package-lock.json" "$TS_STAGE/"
[ -d "$TS_SOURCE/node_modules" ] && cp -r "$TS_SOURCE/node_modules" "$TS_STAGE/"

case "$RUNTIME" in
    win-x64) ARCHIVE_NAME="garf-windows-x64.zip" ;;
    win-arm64) ARCHIVE_NAME="garf-windows-arm64.zip" ;;
    linux-x64) ARCHIVE_NAME="garf-linux-x64.tar.gz" ;;
    linux-arm64) ARCHIVE_NAME="garf-linux-arm64.tar.gz" ;;
    osx-x64) ARCHIVE_NAME="garf-macos-x64.tar.gz" ;;
    osx-arm64) ARCHIVE_NAME="garf-macos-arm64.tar.gz" ;;
    *) ARCHIVE_NAME="garf-$RUNTIME.tar.gz" ;;
esac

ARCHIVE_PATH="$FULL_OUTPUT_DIR/$ARCHIVE_NAME"
rm -f "$ARCHIVE_PATH"

echo "==> Creating archive: $ARCHIVE_PATH ..."
if [[ "$ARCHIVE_NAME" == *.zip ]]; then
    (cd "$STAGE_DIR" && zip -r "$ARCHIVE_PATH" .)
else
    tar -czf "$ARCHIVE_PATH" -C "$STAGE_DIR" .
fi

if command -v sha256sum >/dev/null 2>&1; then
    HASH=$(sha256sum "$ARCHIVE_PATH" | awk '{print $1}')
elif command -v shasum >/dev/null 2>&1; then
    HASH=$(shasum -a 256 "$ARCHIVE_PATH" | awk '{print $1}')
else
    HASH="n/a"
fi

echo "$HASH  $ARCHIVE_NAME" >> "$FULL_OUTPUT_DIR/checksums.txt"

echo "==> Packaged successfully!"
echo "    Archive: $ARCHIVE_PATH"
echo "    SHA256:  $HASH"

rm -rf "$STAGE_DIR"
