#!/bin/sh
# Before the .rpm or .deb package is removed or updated (%preun / prerm). Removing it gets 0
# from rpm, "remove" (or "deconfigure") from dpkg: the service stops for good. An update (1,
# "upgrade") keeps it running - the new package restarts it on its files.
case "${1:-}" in
    0|remove|deconfigure) systemctl disable --now file-search-manager.service >/dev/null 2>&1 || true ;;
esac
exit 0
