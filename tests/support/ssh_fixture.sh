#!/bin/bash
# The caller enters private mount/PID namespaces before running this script.
set -euo pipefail
root=$1
port=$2
python=${3:-0}
mount --make-rprivate /
size=128m
[ "$python" = 0 ] || size=256m
mount -t tmpfs -o mode=0755,size=$size msync-ssh-fixture "$root/linux"
mkdir -p "$root/linux/run" "$root/linux/tmp"
mount --bind "$root/linux/run" /run
mount --bind "$root/linux/tmp" /tmp
mkdir -m 0755 /run/sshd

# The existing root account is usable only inside this namespace; no account or
# SSH configuration file in the distribution is edited.
printf 'root:x:20000:0:99999:7:::\n' > /run/shadow
chmod 0600 /run/shadow
mount --bind /run/shadow /etc/shadow

jail=/run/jail
mkdir -p "$jail"/{bin,usr/bin,etc,root,dev,tmp}
chmod 0700 "$jail/root"
chmod 1777 "$jail/tmp"
mknod -m 0666 "$jail/dev/null" c 1 3
printf 'root:x:0:0:SSH test:/root:/bin/bash\n' > "$jail/etc/passwd"
printf 'root:x:0:\n' > "$jail/etc/group"
printf 'passwd: files\ngroup: files\n' > "$jail/etc/nsswitch.conf"
install_binary() {
    local path=$1
    install -D -m 0755 "$path" "$jail$path"
    while IFS= read -r library; do
        install -D -m 0755 "$library" "$jail$library"
    done < <(ldd "$path" | awk '$2 == "=>" && $3 ~ /^\// {print $3} $1 ~ /^\// {print $1}')
}
for path in /bin/bash /bin/sh /bin/cat /bin/cp /bin/chmod /bin/mkdir /bin/rm \
    /usr/bin/find /usr/bin/realpath /usr/bin/dirname /usr/bin/basename /usr/bin/mktemp; do
    install_binary "$path"
done
if [ "$python" = 1 ]; then
    mkdir -p "$jail/proc"
    mount -t proc -o nosuid,nodev,noexec proc "$jail/proc"
    install_binary /usr/bin/mkfifo
    install_binary /usr/bin/python3
    stdlib=$(/usr/bin/python3 -c 'import sysconfig; print(sysconfig.get_path("stdlib"))')
    mkdir -p "$jail$stdlib"
    tar -C "$stdlib" --exclude=__pycache__ --exclude=dist-packages --exclude=site-packages -cf - . \
        | tar -C "$jail$stdlib" -xf -
    while IFS= read -r -d '' library; do
        while IFS= read -r dependency; do
            install -D -m 0755 "$dependency" "$jail$dependency"
        done < <(ldd "$library" | awk '$2 == "=>" && $3 ~ /^\// {print $3} $1 ~ /^\// {print $1}')
    done < <(find "$stdlib/lib-dynload" -name '*.so' -print0)
fi
/usr/bin/ssh-keygen -q -t ed25519 -N '' -f /run/host_key
cp "$root/client_key.pub" /run/authorized_keys
chmod 0600 /run/authorized_keys
cp /run/host_key.pub "$root/host_key.pub"
cat > "$root/sshd_config" <<EOF
ListenAddress 127.0.0.1
Port $port
HostKey /run/host_key
PidFile /run/sshd.pid
AuthorizedKeysFile /run/authorized_keys
StrictModes yes
AllowUsers root
PermitRootLogin prohibit-password
AuthenticationMethods publickey
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
PermitUserRC no
PermitUserEnvironment no
DisableForwarding yes
PermitTTY no
PrintMotd no
PrintLastLog no
UseDNS no
ChrootDirectory /run/jail
Subsystem sftp internal-sftp
SetEnv LANG=C LC_ALL=C PYTHONDONTWRITEBYTECODE=1 PYTHONNOUSERSITE=1
LogLevel VERBOSE
EOF
/usr/sbin/sshd -t -f "$root/sshd_config"
/usr/sbin/sshd -D -e -f "$root/sshd_config" >> "$root/sshd.log" 2>&1 &
daemon=$!
cleanup() {
    kill "$daemon" 2>/dev/null || true
    wait "$daemon" 2>/dev/null || true
}
trap cleanup EXIT
sleep 0.1
kill -0 "$daemon"
printf 'ready\n' > "$root/ready"
# EOF from the test process shuts down the namespace. A bounded watchdog also
# tears it down if the parent crashes without closing its pipe normally.
read -r -t 240 _ || true
