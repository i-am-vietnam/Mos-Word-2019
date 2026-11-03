#!/usr/bin/env bash
set -euo pipefail
umask 077

die() { printf 'Lỗi: %s\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die 'Chạy bằng sudo trên VPS đích.'
exec 9>/run/mosword-operation.lock
flock -n 9 || die 'Có thao tác triển khai/nhập/sao lưu đang chạy.'
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
server_ip='' admin_allow_list='' rotate=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --ip) [[ $# -ge 2 ]] || die '--ip cần IPv4.'; server_ip=$2; shift 2 ;;
        --admin-allow-list) [[ $# -ge 2 ]] || die '--admin-allow-list cần danh sách IPv4/CIDR, cách nhau bằng dấu phẩy.'; admin_allow_list=$2; shift 2 ;;
        --rotate) rotate=true; shift ;;
        *) die 'Cách dùng: sudo ./02-certificate.sh --ip IPv4 [--admin-allow-list IPv4,CIDR] [--rotate]' ;;
    esac
done
[[ -n $server_ip ]] || die 'Bắt buộc --ip IPv4 của VPS.'
# ipaddress rejects shell/nginx metacharacters, invalid octets and malformed CIDRs.
python3 - "$server_ip" "$admin_allow_list" <<'PY'
import ipaddress
import sys
try:
    ipaddress.IPv4Address(sys.argv[1])
    if sys.argv[2]:
        for value in sys.argv[2].split(','):
            ipaddress.IPv4Network(value, strict=False)
except ValueError:
    sys.exit('Lỗi: IP hoặc AdminAllowList không hợp lệ; chỉ dùng IPv4/CIDR, không có khoảng trắng.')
PY
[[ -d /etc/mosword && -d /etc/nginx/sites-available ]] || die 'Chạy 01-provision.sh trước.'
install -d -o root -g root -m 700 /etc/mosword/tls
cert=/etc/mosword/tls/server.crt
key=/etc/mosword/tls/server.key
site=/etc/nginx/sites-available/mosword
enabled=/etc/nginx/sites-enabled/mosword
proxy=/etc/nginx/mosword-proxy.conf
for path in "$cert" "$key" "$site" "$proxy"; do
    [[ ! -L $path ]] || die 'Tệp cấu hình/chứng thư không được là symlink.'
done
for path in "$site" "$proxy"; do
    if [[ -f $path ]]; then
        grep -Fq '# Managed by MOS Word deployment.' "$path" || die 'Cấu hình nginx đã tồn tại nhưng không thuộc bộ deploy; kiểm tra thủ công.'
    fi
done
if [[ -e $enabled || -L $enabled ]]; then
    [[ -L $enabled && $(readlink -f "$enabled") == "$site" ]] || die 'Tên site mosword đã được dùng bởi cấu hình khác.'
fi
if [[ -e $cert || -e $key ]]; then
    [[ -f $cert && -f $key ]] || die 'Thiếu một nửa cặp chứng thư/khóa; khôi phục bản sao lưu, không tự thay khóa.'
    if [[ $rotate == false ]]; then
        openssl x509 -in "$cert" -noout -checkip "$server_ip" >/dev/null || die 'SAN không khớp IP; dùng --rotate nếu chủ động đổi pin client.'
        openssl x509 -in "$cert" -noout -checkend 0 >/dev/null || die 'Chứng thư hết hạn; dùng --rotate và cập nhật pin client.'
        openssl pkey -in "$key" -text -noout | grep -Fq 'ASN1 OID: prime256v1' || die 'Khóa không phải ECDSA P-256.'
        [[ $(openssl x509 -in "$cert" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256) == \
           "$(openssl pkey -in "$key" -pubout -outform DER | openssl dgst -sha256)" ]] || die 'Khóa không khớp chứng thư.'
    fi
fi

# Keep a local transaction backup so nginx validation failures restore the old site/pin.
scratch=$(mktemp -d /etc/mosword/tls/.configure.XXXXXX)
committed=false
had_enabled=false
[[ ! -L $enabled ]] || had_enabled=true
for path in "$cert" "$key" "$site" "$proxy"; do
    if [[ -f $path ]]; then cp -p -- "$path" "$scratch/$(basename "$path").old"; fi
done
cleanup() {
    if [[ $committed == false ]]; then
        for path in "$cert" "$key" "$site" "$proxy"; do
            backup="$scratch/$(basename "$path").old"
            if [[ -f $backup ]]; then cp -p -- "$backup" "$path"; else rm -f -- "$path"; fi
        done
        if [[ $had_enabled == false ]]; then rm -f -- "$enabled"; fi
        # Only reload a known-good restored configuration, preserving an active old pin.
        if nginx -t >/dev/null 2>&1; then systemctl reload nginx || true; fi
    fi
    rm -rf -- "$scratch"
}
trap cleanup EXIT
if [[ ! -f $cert || $rotate == true ]]; then
    if [[ $rotate == true ]]; then
        printf 'Đang chủ động thay chứng thư/khóa: pin SPKI sẽ đổi, phải cập nhật client trước khi dùng.\n'
    fi
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -sha256 -nodes \
        -keyout "$scratch/server.key" -out "$scratch/server.crt" -days 3650 \
        -subj "/CN=$server_ip" -addext "subjectAltName=IP:$server_ip" \
        -addext 'basicConstraints=critical,CA:FALSE' \
        -addext 'keyUsage=critical,digitalSignature' -addext 'extendedKeyUsage=serverAuth' 2>/dev/null
    install -o root -g root -m 600 "$scratch/server.key" "$key"
    install -o root -g root -m 644 "$scratch/server.crt" "$cert"
fi
chown root:root "$cert" "$key"
chmod 600 "$key"
cat >"$proxy" <<'PROXY'
# Managed by MOS Word deployment. Overwrite forwarded identity at the trusted edge.
proxy_http_version 1.1;
proxy_set_header Host $host;
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto https;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header Forwarded "";
proxy_set_header Connection "";
proxy_connect_timeout 5s;
proxy_read_timeout 60s;
PROXY
python3 - "$script_dir/nginx.conf.template" "$site" "$server_ip" "$admin_allow_list" <<'PY'
import ipaddress
import pathlib
import sys
template = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')
rules = ''
if sys.argv[4]:
    rules = '\n'.join('        allow ' + str(ipaddress.IPv4Network(value, strict=False)) + ';'
                      for value in sys.argv[4].split(',')) + '\n        deny all;'
pathlib.Path(sys.argv[2]).write_text(template.replace('@SERVER_IP@', sys.argv[3])
                                  .replace('@ADMIN_ALLOW_LIST@', rules), encoding='utf-8')
PY
chmod 644 "$site" "$proxy"
ln -sfn "$site" "$enabled"
nginx -t
systemctl reload nginx
committed=true
printf 'Đã cấu hình HTTPS cổng 443 (không mở cổng 80). Pin SHA256 SPKI Base64:\n'
openssl x509 -in "$cert" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | openssl base64 -A
printf '\nGiữ khóa TLS, raw seed Ed25519 và Data Protection qua các lần deploy. Pin client phải khớp giá trị trên.\n'
