#!/bin/sh
# Executed only after unshare creates private user, mount and PID namespaces.
# Caller maps to root here so mount(8) can install fixture-only account files.
set -eu
root=$1
mount --make-rprivate /
mount --bind "$root/passwd" /etc/passwd
mount --bind "$root/shadow" /etc/shadow
mount --bind "$root/group" /etc/group
# Run sshd as non-root within a second user namespace. Its only mapped account
# is the caller, avoiding a root sshd's unmapped privilege-separation account.
exec unshare --user --map-user 1000 --map-group 1000 \
    /usr/sbin/sshd -D -e -f "$root/sshd_config"
