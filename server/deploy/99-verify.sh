#!/usr/bin/env bash
set -euo pipefail
# shellcheck source=common.sh
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
require_root
[[ $# == 3 && $1 =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ && $2 =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ && $3 =~ ^sha256//[A-Za-z0-9+/]{43}=$ ]] || die 'Dùng: 99-verify.sh tên-host IPv4 sha256//BASE64-SPKI'
load_env
current_release
for unit in mosword.service nginx.service postgresql.service; do
    systemctl is-active --quiet "$unit" || die "Dịch vụ chưa hoạt động: $unit"
done
ready || die 'Readiness nội bộ thất bại.'
cert=/etc/mosword/tls/server.crt
[[ -f $cert && ! -L $cert && $(stat -c '%u:%g:%a' "$cert") == 0:0:644 ]] || die 'Chứng thư CA cục bộ không an toàn.'
# Trust the deployed self-signed certificate while independently checking the owner's pin.
code=$(curl --silent --show-error --fail --max-time 15 --noproxy '*' --cacert "$cert" --pinnedpubkey "$3" --resolve "$1:443:$2" --output /dev/null --write-out '%{http_code}' "https://$1/health/ready")
[[ $code == 200 ]] || die 'TLS/pin/readiness từ nginx thất bại.'
systemctl is-enabled --quiet mosword.service || die 'Dịch vụ mosword chưa enabled.'
systemctl is-enabled --quiet mosword-backup.timer || die 'Timer backup chưa enabled.'
systemctl is-active --quiet mosword-backup.timer || die 'Timer backup chưa hoạt động.'
listeners=$(ss -H -ltn)
printf '%s\n' "$listeners"
# Verify firewall before allowing the DHCP client socket exception below.
firewall=$(LC_ALL=C ufw status verbose)
grep -Fxq 'Status: active' <<<"$firewall" || die 'UFW chưa active.'
grep -Eq '^Default: deny \(incoming\),' <<<"$firewall" || die 'UFW chưa deny incoming mặc định.'
firewall_rules=$(LC_ALL=C ufw show added)
while IFS= read -r rule; do
    case "$rule" in
        ''|'ufw allow 22/tcp'|'ufw allow 443/tcp') ;;
        *) die 'UFW có rule ngoài 22/tcp và 443/tcp.' ;;
    esac
done < <(sed -n '/^ufw /p' <<<"$firewall_rules")
validate_listeners tcp <<<"$listeners"
validate_listeners udp <<<"$(ss -H -lun)"
for directory in / /var/lib/mosword /var/backups/mosword; do
    used=$(df --output=pcent "$directory" | tail -n 1 | tr -dc '0-9')
    (( used < 90 )) || die "Ổ đĩa gần đầy: $directory"
done
find /var/backups/mosword -maxdepth 1 -type f -name 'daily-*.tar.gz' -mmin -1560 -print -quit | grep -q . || die 'Không có backup mới trong 26 giờ; chạy 06-backup.sh.'
systemctl list-timers --no-pager mosword-backup.timer
df -h / /var/lib/mosword /var/backups/mosword
printf 'Kiểm tra dịch vụ, TLS pin, cổng, backup và dung lượng đạt.\n'
