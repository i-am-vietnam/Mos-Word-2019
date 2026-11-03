#!/usr/bin/env bash
# Shared helpers installed under /usr/local/lib/mosword-deploy (root-owned).
set -euo pipefail
set +x
PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
umask 077

die() { printf 'Lỗi: %s\n' "$*" >&2; exit 1; }
require_root() { [[ $EUID == 0 ]] || die 'Chạy bằng sudo.'; }
operation_lock() {
    # /run is root-writable; /run/lock may be world-writable on Ubuntu.
    [[ ! -L /run/mosword-operation.lock ]] || die 'Lock không được là symlink.'
    exec 9>/run/mosword-operation.lock
    flock -n 9 || die 'Có thao tác triển khai/nhập/sao lưu đang chạy.'
}
trusted_file() {
    local path=$1 parent=$1
    [[ -f $path && ! -L $path ]] || die "Không có tệp an toàn: $path"
    [[ $(stat -c '%u:%g:%a' "$path") == 0:0:600 ]] || die "Cần root:root 600: $path"
    while [[ $parent != / ]]; do
        parent=$(dirname -- "$parent")
        [[ ! -L $parent && $(stat -c %u "$parent") == 0 ]] || die 'Thư mục cấu hình không đáng tin.'
        (( (8#$(stat -c %a "$parent") & 0022) == 0 )) || die 'Thư mục cấu hình cho phép người khác ghi.'
    done
}
load_env() {
    trusted_file /etc/mosword/mosword.env
    set -a
    # Root-owned, shell-quoted configuration, never supplied by an upload.
    # shellcheck disable=SC1091
    source /etc/mosword/mosword.env
    set +a
    [[ ${ASPNETCORE_ENVIRONMENT:-} == Production && ${DataProtection__KeyPath:-} == /var/lib/mosword/dataprotection &&
       ${Packages__StorageRoot:-} == /var/lib/mosword/packages && ${Packages__SigningKeyPath:-} == /var/lib/mosword/keys/manifest-signing.key ]] || die 'Cấu hình đường dẫn/Production không đúng.'
}
cli() {
    local release=$1
    shift
    # Parent environment carries secrets; they never appear in process arguments.
    (cd -- "$release"; runuser -m -u moswordsvc -- /usr/bin/dotnet "$release/MosWord2019.Server.dll" "$@")
}
current_release() {
    local path
    [[ -L /opt/mosword/current ]] || die 'current phải là symlink.'
    path=$(readlink -f /opt/mosword/current) || return 1
    [[ $path =~ ^/opt/mosword/releases/[0-9]{8}T[0-9]{6}Z-[a-zA-Z0-9]+$ && -d $path && ! -L $path ]] || die 'Liên kết current không hợp lệ.'
    printf '%s\n' "$path"
}
ready() {
    local _
    for _ in {1..30}; do
        if systemctl is-active --quiet mosword.service &&
           [[ $(curl --silent --show-error --max-time 3 --output /dev/null --write-out '%{http_code}' -H 'X-Forwarded-Proto: https' http://127.0.0.1:5000/health/ready) == 200 ]]; then return 0; fi
        sleep 2
    done
    return 1
}
postgres_sql() { runuser -u postgres -- psql -X -v ON_ERROR_STOP=1 -d mosword "$@"; }
validate_listeners() {
    local protocol=$1 address port
    while read -r _ _ _ address _; do
        [[ -n $address ]] || continue
        port=${address##*:}
        if [[ $protocol == tcp ]]; then
            case "$port" in
                5000) [[ $address == 127.0.0.1:5000 ]] || die 'Cổng 5000 không chỉ loopback.' ;;
                5432) [[ $address == 127.0.0.1:5432 || $address == '[::1]:5432' ]] || die 'PostgreSQL không chỉ loopback.' ;;
            esac
        fi
        case "$address" in 127.*:*|'[::1]:'*|'[::1]%lo:'*) continue ;; esac
        if [[ $protocol == tcp ]]; then
            [[ $port == 22 || $port == 443 ]] || die "Cổng TCP ngoài loopback không được phép: $address"
        elif [[ $port == 68 ]]; then
            printf 'Lưu ý: DHCP UDP68 đang listen: %s; UFW phải chặn inbound không được yêu cầu.\n' "$address" >&2
        else
            die "Cổng UDP ngoài loopback không được phép: $address"
        fi
    done
}
