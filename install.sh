#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${GARF_INSTALL_DIR:-$HOME/.garf}"
BIN_DIR="$INSTALL_DIR/bin"
VERSION="${GARF_VERSION:-latest}"
REPO="${GARF_REPO:-namkhanh307/Garf}"

echo "====================================================="
echo "            Installing Garf Code Indexer"
echo "====================================================="

OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
    Linux)
        case "$ARCH" in
            x86_64) ASSET_NAME="garf-linux-x64.tar.gz" ;;
            aarch64|arm64) ASSET_NAME="garf-linux-arm64.tar.gz" ;;
            *) echo "Unsupported Linux architecture: $ARCH" >&2; exit 1 ;;
        esac
        ;;
    Darwin)
        case "$ARCH" in
            x86_64) ASSET_NAME="garf-macos-x64.tar.gz" ;;
            arm64) ASSET_NAME="garf-macos-arm64.tar.gz" ;;
            *) echo "Unsupported macOS architecture: $ARCH" >&2; exit 1 ;;
        esac
        ;;
    *)
        echo "Unsupported OS: $OS. For Windows, please run install.ps1." >&2
        exit 1
        ;;
esac

TEMP_DIR="$(mktemp -d)"
cleanup() {
    rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

ARCHIVE_PATH="$TEMP_DIR/$ASSET_NAME"

if [ "$VERSION" = "latest" ]; then
    RELEASE_URL="https://api.github.com/repos/$REPO/releases/latest"
else
    RELEASE_URL="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
fi

echo "==> Fetching release info from $RELEASE_URL ..."
DOWNLOAD_URL=""
if command -v curl >/dev/null 2>&1; then
    DOWNLOAD_URL=$(curl -sSL "$RELEASE_URL" | grep -o "\"browser_download_url\": *\"[^\"]*$ASSET_NAME\"" | head -n 1 | cut -d '"' -f 4 || true)
fi

if [ -n "$DOWNLOAD_URL" ]; then
    echo "==> Downloading $ASSET_NAME from GitHub Releases ..."
    curl -fsSL -o "$ARCHIVE_PATH" "$DOWNLOAD_URL"
elif [ -f "dist/$ASSET_NAME" ]; then
    echo "==> Found local distribution archive: dist/$ASSET_NAME"
    cp "dist/$ASSET_NAME" "$ARCHIVE_PATH"
else
    echo "Could not download $ASSET_NAME and no local archive exists." >&2
    exit 1
fi

echo "==> Extracting into $BIN_DIR ..."
mkdir -p "$BIN_DIR"
tar -xzf "$ARCHIVE_PATH" -C "$BIN_DIR"
chmod +x "$BIN_DIR/garf" || true

# Add to PATH in shell configuration if needed
SHELL_CONFIG=""
if [ -n "${ZSH_VERSION:-}" ] || [ "$(basename "${SHELL:-}")" = "zsh" ]; then
    SHELL_CONFIG="$HOME/.zshrc"
elif [ -n "${BASH_VERSION:-}" ] || [ "$(basename "${SHELL:-}")" = "bash" ]; then
    SHELL_CONFIG="$HOME/.bashrc"
fi

PATH_LINE="export PATH=\"$BIN_DIR:\$PATH\""
if [ -n "$SHELL_CONFIG" ] && [ -f "$SHELL_CONFIG" ]; then
    if ! grep -q "$BIN_DIR" "$SHELL_CONFIG"; then
        echo "==> Adding $BIN_DIR to $SHELL_CONFIG ..."
        echo "" >> "$SHELL_CONFIG"
        echo "# Garf code indexer" >> "$SHELL_CONFIG"
        echo "$PATH_LINE" >> "$SHELL_CONFIG"
    fi
fi

echo ""
echo "====================================================="
echo "            Installed Successfully!"
echo "====================================================="
echo "Executable location: $BIN_DIR/garf"
echo ""
echo "To use immediately in this shell:"
echo "  export PATH=\"$BIN_DIR:\$PATH\""
echo ""
echo "Quick start:"
echo "  garf index <path-to-repo>"
echo "  garf query <symbol-name>"
echo "  garf mcp"
