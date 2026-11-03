#!/usr/bin/env bash
set -euo pipefail
set +x
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 1 && $1 =~ ^(daily-[0-9]{8}T[0-9]{6}Z|weekly-[0-9]{4}-W[0-9]{2})\.tar\.gz$ ]] || die 'Dùng: 07-export-backup.sh tên-backup.tar.gz'
operation_lock
backup=/var/backups/mosword/$1
[[ -f $backup && ! -L $backup && $(stat -c '%u:%a' "$backup") == 0:600 ]] || die 'Backup không an toàn.'
destination=/home/mosword/mosword-backups
[[ -d $destination && ! -L $destination && $(realpath -e "$destination") == "$destination" && $(stat -c %u /home/mosword) == "$(id -u mosword)" ]] || die 'Thiếu thư mục /home/mosword/mosword-backups an toàn.'
# Create the encrypted file in a root-only directory before handing it to the SCP user.
temporary=$(mktemp /var/backups/mosword/.export-XXXXXXXX)
trap 'unset passphrase confirmation; rm -f -- "$temporary"' EXIT
IFS= read -r -s -p 'Mật khẩu mã hóa backup (lưu riêng ở PC): ' passphrase </dev/tty
printf '\n' >/dev/tty
IFS= read -r -s -p 'Nhập lại mật khẩu: ' confirmation </dev/tty
printf '\n' >/dev/tty
[[ ${#passphrase} -ge 20 && $passphrase == "$confirmation" ]] || die 'Cần mật khẩu trùng nhau, ít nhất 20 ký tự.'
unset confirmation
# FD input avoids a password in argv, environment, history, or logs.
openssl enc -aes-256-cbc -salt -pbkdf2 -iter 600000 -md sha256 -in "$backup" -out "$temporary" -pass fd:3 3<<<"$passphrase"
unset passphrase
target=$destination/$1.enc
# An open directory FD and O_EXCL prevent following user-placed destination links.
# Copy also supports /home and /var on different filesystems.
python3 - "$temporary" "$destination" "$1.enc" <<'PY'
import os, pwd, shutil, sys
account = pwd.getpwnam('mosword')
directory = os.open(sys.argv[2], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
try:
    fd = os.open(sys.argv[3], os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
    with os.fdopen(fd, 'wb') as output, open(sys.argv[1], 'rb') as source:
        shutil.copyfileobj(source, output)
        output.flush()
        os.fsync(output.fileno())
        os.fchown(output.fileno(), account.pw_uid, account.pw_gid)
finally:
    os.close(directory)
PY
printf 'Đã xuất mã hóa; tải bằng SCP: %s\n' "$target"
printf 'SHA256=%s\n' "$(sha256sum "$temporary" | cut -d ' ' -f1)"
