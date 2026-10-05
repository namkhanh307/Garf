#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${GARF_INSTALL_DIR:-$HOME/.garf}"
BIN_DIR="$INSTALL_DIR/bin"

echo "==> Uninstalling Garf from $INSTALL_DIR ..."

if [ -d "$INSTALL_DIR" ]; then
    rm -rf "$INSTALL_DIR"
    echo "==> Removed $INSTALL_DIR"
fi

for RC in "$HOME/.bashrc" "$HOME/.zshrc" "$HOME/.profile"; do
    if [ -f "$RC" ] && grep -q "$BIN_DIR" "$RC"; then
        sed -i "/\.garf\/bin/d" "$RC" || true
        echo "==> Removed Garf path entry from $RC"
    fi
done

echo "==> Garf uninstalled successfully."
