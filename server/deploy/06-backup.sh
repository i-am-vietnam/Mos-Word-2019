#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 0 || ( $# == 1 && $1 == --locked ) ]] || die 'Dùng: 06-backup.sh'
if [[ $# == 0 ]]; then operation_lock; fi
load_env
[[ -d /var/backups/mosword && ! -L /var/backups/mosword && $(realpath /var/backups/mosword) == /var/backups/mosword ]] || die 'Thư mục backup không an toàn.'
[[ -d /etc/mosword/tls ]] || die 'Thiếu chứng thư TLS.'
for file in /etc/mosword/mosword.env /etc/mosword/migrate.env; do trusted_file "$file"; done
was_active=0
if systemctl is-active --quiet mosword.service; then was_active=1; fi
temporary=$(mktemp -d /var/backups/mosword/.building-XXXXXXXX)
finish() {
    local code=$?
    trap - EXIT
    set +e
    rm -rf --one-file-system -- "$temporary" || code=1
    if (( was_active == 1 )); then
        systemctl start mosword.service || code=1
        ready || { printf 'Lỗi: dịch vụ chưa ready sau sao lưu.\n' >&2; code=1; }
    fi
    exit "$code"
}
trap finish EXIT
systemctl stop mosword.service
runuser -u postgres -- pg_dump --format=custom --dbname=mosword >"$temporary/database.dump"
[[ -s $temporary/database.dump ]] || die 'Database dump trống.'
current_release >"$temporary/release.txt"
# Stopped application makes the database and package/key/DP files coherent.
tar --create --gzip --file "$temporary/state.tar.gz" --directory / \
    var/lib/mosword/packages var/lib/mosword/keys var/lib/mosword/dataprotection \
    etc/mosword/mosword.env etc/mosword/migrate.env etc/mosword/tls
stamp=$(date -u +%Y%m%d)T000000Z
destination=/var/backups/mosword/daily-$stamp.tar.gz
tar --create --gzip --file "$temporary/backup.tar.gz" --directory "$temporary" database.dump state.tar.gz release.txt
chmod 600 "$temporary/backup.tar.gz"
# Persist the completed archive before atomically replacing this UTC day's snapshot.
sync -f "$temporary/backup.tar.gz"
mv -T "$temporary/backup.tar.gz" "$destination"
sync -f /var/backups/mosword
if [[ $(date -u +%u) == 7 ]]; then
    weekly=/var/backups/mosword/weekly-$(date -u +%G-W%V).tar.gz
    if [[ ! -e $weekly ]]; then
        cp -- "$destination" "$temporary/weekly.tar.gz"
        mv -T "$temporary/weekly.tar.gz" "$weekly"
    fi
fi
# Keep one snapshot per UTC day, including legacy timestamped snapshots.
mapfile -t daily_files < <(find /var/backups/mosword -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | grep -E '^daily-[0-9]{8}T[0-9]{6}Z\.tar\.gz$' | sort -r || true)
last_day=''
selected=''
days=0
for file in "${daily_files[@]}"; do
    day=${file:6:8}
    if [[ $day != "$last_day" ]]; then
        days=$((days + 1))
        last_day=$day
        selected=$file
        stable=daily-${day}T000000Z.tar.gz
        if [[ -f /var/backups/mosword/$stable && ! -L /var/backups/mosword/$stable ]]; then selected=$stable; fi
    fi
    if (( days > 14 )) || [[ $file != "$selected" ]]; then rm -- "/var/backups/mosword/$file"; fi
done
# Only managed basename patterns are eligible for weekly retention cleanup.
mapfile -t files < <(find /var/backups/mosword -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | grep -E '^weekly-[0-9]{4}-W[0-9]{2}\.tar\.gz$' | sort -r || true)
for ((index=8; index<${#files[@]}; index++)); do rm -- "/var/backups/mosword/${files[index]}"; done
printf 'Đã sao lưu: %s\n' "$destination"
