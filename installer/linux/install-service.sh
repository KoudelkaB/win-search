#!/bin/sh
# Installs the File Search Manager file system service - live changes and, for
# administrators, every file on Linux: a root-owned copy of the app in
# /opt/file-search-manager and a systemd unit running it as "FileSearchManager --service".
# Run it again after updating the app; uninstall-service.sh removes both.
#
# Usage: sudo installer/linux/install-service.sh [app folder]
#   app folder: the built app (default: search/bin/Release/net10.0 of this repository -
#               dotnet build search -c Release)
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run it with sudo." >&2
    exit 1
fi

here=$(cd "$(dirname "$0")" && pwd)
source=${1:-$here/../../search/bin/Release/net10.0}
target=/opt/file-search-manager
unit=/etc/systemd/system/file-search-manager.service

if [ ! -x "$source/FileSearchManager" ]; then
    echo "No built app in $source (dotnet build search -c Release)." >&2
    exit 1
fi

# A root service must never run code its users can change: copy the app, owned by root.
# -L copies what a link points to, never the link - a link into a user's folder would let
# that user change what root runs.
systemctl stop file-search-manager.service 2>/dev/null || true
rm -rf "$target.new"
mkdir -p "$target.new"
cp -RL --preserve=mode,timestamps "$source/." "$target.new/"
chown -R root:root "$target.new"
chmod -R go-w "$target.new"
rm -rf "$target"
mv "$target.new" "$target"

sed "s|@APPDIR@|$target|g" "$here/file-search-manager.service" > "$unit"
chmod 644 "$unit"
systemctl daemon-reload
systemctl enable --now file-search-manager.service
systemctl --no-pager --lines=5 status file-search-manager.service || true
