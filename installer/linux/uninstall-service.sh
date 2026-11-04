#!/bin/sh
# Removes the File Search Manager file system service installed by install-service.sh.
# The app keeps working without it: changes are then refreshed when you return to the
# window, and only the files you can access are shown.
#
# Usage: sudo installer/linux/uninstall-service.sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run it with sudo." >&2
    exit 1
fi

systemctl disable --now file-search-manager.service 2>/dev/null || true
rm -f /etc/systemd/system/file-search-manager.service
systemctl daemon-reload
rm -rf /opt/file-search-manager
echo "File Search Manager service removed."
