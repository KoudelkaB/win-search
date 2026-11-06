#!/bin/sh
# After the .rpm or .deb package is removed or updated (%postun / postrm): systemd forgets
# the unit that went away, or reads the new one
systemctl daemon-reload >/dev/null 2>&1 || true
exit 0
