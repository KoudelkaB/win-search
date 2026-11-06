#!/usr/bin/env python3
"""
SELinux (Fedora, RHEL) picks the domain the package's service runs in by the label of its
executable: bin_t (/usr/libexec) and usr_t (/opt) lead to unconfined_service_t, while lib_t
(/usr/lib) leaves it in init_t - which may not mark whole file systems for fanotify. The
service then runs but watches nothing ("fanotify_mark failed: Permission denied").

Checks, against the targeted policy on disk (no running SELinux needed - a container
will do), that the executable's default label leads to a domain that may watch file systems.

Usage: check-selinux.py /usr/libexec/file-search-manager/FileSearchManager
"""
import glob
import stat
import sys

import selinux
import setools

path = sys.argv[1]
handle = selinux.selabel_open(selinux.SELABEL_CTX_FILE, None, 0)
_, context = selinux.selabel_lookup(handle, path, stat.S_IFREG)
label = context.split(":")[2]

policy = setools.SELinuxPolicy(sorted(glob.glob("/etc/selinux/targeted/policy/policy.*"))[-1])
domains = {str(rule.default) for rule in setools.TERuleQuery(
    policy, ruletype=["type_transition"], source="init_t", target=label, tclass=["process"]).results()}
domain = domains.pop() if len(domains) == 1 else "init_t"  # No transition: systemd's own domain
watch = any(True for _ in setools.TERuleQuery(
    policy, ruletype=["allow"], source=domain, tclass=["filesystem"], perms=["watch"]).results())

print(f"{path}: {label} -> service domain {domain}, watching file systems {'allowed' if watch else 'DENIED'}")
sys.exit(0 if watch else 1)
