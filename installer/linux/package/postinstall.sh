#!/bin/sh
# After the .rpm or .deb package is installed or updated (%post / postinst). A first install
# gets 1 from rpm, "configure" without an old version from dpkg; an update 2, or "configure"
# and the old version.
#
# Like the Windows installer, a first install turns the file system service on. Settings
# turns it off and on later; an update keeps that choice and restarts a running service on
# the new files. A service installed earlier from the portable app (install-service.sh: its
# own unit and a copy of the app in /opt/file-search-manager) gives way to the package's,
# and its choice carries over: turned off, it stays off.
#
# FSM_ROOT is for the tests only: a folder standing in for /.
set -u
root=${FSM_ROOT:-}
unit=file-search-manager.service

turn_on=false
case "${1:-}" in
    1) turn_on=true ;;
    configure) [ -z "${2:-}" ] && turn_on=true ;;
esac

portable=$root/etc/systemd/system/$unit
if [ -f "$portable" ] && grep -q "/opt/file-search-manager/" "$portable"; then
    if systemctl is-enabled --quiet "$unit" 2>/dev/null; then turn_on=true; else turn_on=false; fi
    systemctl disable --now "$unit" >/dev/null 2>&1 || true
    rm -f "$portable"
    rm -rf "$root/opt/file-search-manager"
fi

# Nothing here may fail the package - without systemd (a container) there is no service
systemctl daemon-reload >/dev/null 2>&1 || true
if $turn_on; then
    systemctl enable --now "$unit" >/dev/null 2>&1 || true
else
    systemctl try-restart "$unit" >/dev/null 2>&1 || true
fi
exit 0
