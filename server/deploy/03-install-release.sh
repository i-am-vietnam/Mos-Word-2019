#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 2 && $2 =~ ^[a-fA-F0-9]{64}$ ]] || die 'Dùng: 03-install-release.sh archive.tar.gz SHA256'
archive=$(realpath -e -- "$1")
[[ -f $archive ]] || die 'Không có archive.'
stamp=$(basename -- "$archive" .tar.gz)
[[ $stamp =~ ^[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}$ ]] || die 'Tên archive phải là timestamp-commit/random.tar.gz do publish.ps1 tạo.'
operation_lock
load_env
snapshot=$(mktemp /var/tmp/mosword-release-XXXXXXXX.tar.gz)
trap 'rm -f -- "$snapshot"' EXIT
# Copy a possibly user-owned upload before checking its digest; unpack only this root-owned snapshot.
cp -- "$archive" "$snapshot"
archive=$snapshot
if [[ $(sha256sum -- "$archive" | cut -d ' ' -f 1) != "${2,,}" ]]; then rm -- "$snapshot"; die 'SHA256 không khớp.'; fi
previous=''
if [[ -e /opt/mosword/current || -L /opt/mosword/current ]]; then previous=$(current_release); fi
release="/opt/mosword/releases/$stamp"
[[ ! -e $release && ! -L $release ]] || die 'Release đã tồn tại; không ghi đè.'
mkdir -- "$release"
switched=0
stopped=0
finish() {
    local code=$?
    local rollback_healthy=0
    trap - EXIT
    set +e
    rm -f -- "$snapshot"
    if (( code != 0 && stopped == 1 )); then
        printf 'Triển khai lỗi; khôi phục bản chạy trước (không hạ schema).\n' >&2
        if [[ -n $previous ]]; then
            if rm -f -- /opt/mosword/current.rollback &&
               ln -s -- "$previous" /opt/mosword/current.rollback &&
               mv -Tf /opt/mosword/current.rollback /opt/mosword/current &&
               systemctl restart mosword.service && ready; then rollback_healthy=1;
            else printf 'Lỗi: bản trước chưa ready; cần xử lý ngay.\n' >&2; fi
        else
            if (( switched == 1 )); then rm -f -- /opt/mosword/current; fi
            systemctl stop mosword.service
        fi
    fi
    # Discard rejected uploads, but never remove a release that might still run.
    if (( code != 0 )) && { (( switched == 0 )) || (( rollback_healthy == 1 )); }; then
        if [[ $release != "$previous" && ! -L $release && $(realpath -e -- "$release") == "$release" &&
              $release =~ ^/opt/mosword/releases/[0-9]{8}T[0-9]{6}Z-[a-zA-Z0-9]+$ &&
              $(readlink -f /opt/mosword/current) != "$release" ]]; then
            rm -rf --one-file-system -- "$release"
        fi
    fi
    exit "$code"
}
trap finish EXIT
# Validate every archive member before extracting. Reject links and special files.
python3 - "$archive" "$release" <<'PY'
import pathlib, sys, tarfile
with tarfile.open(sys.argv[1], 'r:gz') as archive:
    members = archive.getmembers()
    total = 0
    names = set()
    for member in members:
        p = pathlib.PurePosixPath(member.name)
        if p.is_absolute() or '..' in p.parts or '\\' in member.name or not (member.isfile() or member.isdir()):
            raise SystemExit('Archive chứa đường dẫn/loại tệp không an toàn.')
        if str(p) in names:
            raise SystemExit('Archive chứa mục trùng.')
        names.add(str(p))
        total += member.size
        if total > 1024**3 or len(names) > 20000:
            raise SystemExit('Archive quá lớn.')
    if 'MosWord2019.Server.dll' not in names or 'MosWord2019.Server.runtimeconfig.json' not in names:
        raise SystemExit('Archive không có publish server ở thư mục gốc.')
    archive.extractall(sys.argv[2], members=members, filter='data')
PY
chown -R root:root "$release"
find "$release" -type d -exec chmod 755 {} +
find "$release" -type f -exec chmod 644 {} +
# A consistent backup must succeed before any schema change on an installed server.
if [[ -n $previous ]]; then "$(dirname -- "${BASH_SOURCE[0]}")/06-backup.sh" --locked; fi
previous_start=$(systemctl show mosword.service --property=ExecMainStartTimestampMonotonic --value)
systemctl stop mosword.service
stopped=1
(
    trusted_file /etc/mosword/migrate.env
    set -a
    # shellcheck disable=SC1091
    source /etc/mosword/migrate.env
    set +a
    cli "$release" migrate
)
postgres_sql -q <<'SQL'
GRANT USAGE ON SCHEMA public TO mosword_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO mosword_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO mosword_app;
ALTER DEFAULT PRIVILEGES FOR ROLE mosword_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mosword_app;
ALTER DEFAULT PRIVILEGES FOR ROLE mosword_migrator IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO mosword_app;
SQL
[[ ! -e /opt/mosword/current.next && ! -L /opt/mosword/current.next ]] || die 'current.next đã tồn tại.'
ln -s -- "$release" /opt/mosword/current.next
mv -Tf /opt/mosword/current.next /opt/mosword/current
switched=1
systemctl restart mosword.service
ready || die 'Server mới chưa ready.'
# current alone is not proof: an old process can answer health after a failed restart.
systemctl is-active --quiet mosword.service || die 'Dịch vụ chưa active.'
pid=$(systemctl show mosword.service --property=MainPID --value)
start=$(systemctl show mosword.service --property=ExecMainStartTimestampMonotonic --value)
started_at=$(systemctl show mosword.service --property=ExecMainStartTimestamp --value)
[[ $pid =~ ^[1-9][0-9]*$ && $start =~ ^[1-9][0-9]*$ && $start != "$previous_start" && -n $started_at ]] || die 'Không xác nhận được tiến trình dịch vụ mới.'
active=$(current_release)
[[ $active == "$release" && $(readlink -e -- "/proc/$pid/cwd") == "$release" ]] || die 'Tiến trình dịch vụ vẫn chạy release khác.'
mapfile -d '' -t command < "/proc/$pid/cmdline"
[[ ${command[0]:-} == /usr/bin/dotnet && ${command[1]:-} == /opt/mosword/current/MosWord2019.Server.dll ]] || die 'Tiến trình không chạy server dự kiến.'
systemctl enable mosword.service
stopped=0
# Keep three releases, always protecting current and the rollback target.
mapfile -t releases < <(find /opt/mosword/releases -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort -r)
kept=1
if [[ -n $previous && $previous != "$release" ]]; then kept=2; fi
for name in "${releases[@]}"; do
    [[ $name =~ ^[0-9]{8}T[0-9]{6}Z-[a-zA-Z0-9]+$ ]] || continue
    path=/opt/mosword/releases/$name
    if [[ $path == "$release" || $path == "$previous" ]]; then continue; fi
    if (( kept < 3 )); then
        kept=$((kept + 1))
    else
        [[ ! -L $path && $path != "$(current_release)" ]] || die 'Từ chối dọn release không an toàn.'
        rm -rf --one-file-system -- "$path"
    fi
done
printf 'Đã triển khai: %s\n' "$release"
printf 'MOSWORD_ACTIVE_RELEASE=%s\nMOSWORD_SERVICE_STARTED_AT=%s\n' "$active" "$started_at"
