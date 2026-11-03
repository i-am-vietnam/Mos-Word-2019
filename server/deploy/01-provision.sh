#!/usr/bin/env bash
set -euo pipefail
set +x
umask 077

# Run only on the intended Ubuntu host, as root. Never enable shell tracing.
die() { printf 'Lỗi: %s\n' "$*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || die 'Chạy bằng sudo trên VPS đích.'
[[ $# -eq 0 ]] || die 'Cách dùng: sudo ./01-provision.sh'
exec 9>/run/mosword-operation.lock
flock -n 9 || die 'Có thao tác triển khai/nhập/sao lưu đang chạy.'
# shellcheck source=/dev/null
source /etc/os-release
[[ $ID == ubuntu && $VERSION_ID == 24.04 && $(dpkg --print-architecture) == amd64 ]] || die 'Chỉ hỗ trợ Ubuntu 24.04 x64.'
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
# Fixed deployment paths must not redirect root writes through symbolic links.
for path in /opt/mosword /opt/mosword/releases /etc/mosword /etc/mosword/tls \
    /var/lib/mosword /var/lib/mosword/packages /var/lib/mosword/dataprotection \
    /var/lib/mosword/keys /var/lib/mosword/incoming /var/backups/mosword \
    /usr/local/lib/mosword-deploy /swapfile; do
    [[ ! -L $path ]] || die "Đường dẫn triển khai không được là symlink: $path"
done
export DEBIAN_FRONTEND=noninteractive
apt-get update
# .NET 10 is supplied by Ubuntu; never install an SDK or Microsoft feed here.
apt-get install -y --no-install-recommends aspnetcore-runtime-10.0 postgresql-16 nginx ufw fail2ban python3-systemd unattended-upgrades openssl openssh-client ca-certificates python3 curl iproute2 util-linux
ssh_settings=$(/usr/sbin/sshd -T)
grep -Fxq 'port 22' <<<"$ssh_settings" || die 'SSH chưa dùng cổng 22; kiểm tra trước khi bật UFW chỉ cho 22/443.'

# Refuse unknown firewall rules instead of silently deleting an operator's rules.
while IFS= read -r rule; do
    case "$rule" in
        ''|'ufw allow 22/tcp'|'ufw allow 443/tcp') ;;
        *) die 'UFW có quy tắc khác 22/tcp và 443/tcp. Kiểm tra bằng ufw show added, xử lý thủ công rồi chạy lại; chưa xóa quy tắc nào.' ;;
    esac
done < <(ufw show added | sed -n '/^ufw /p')
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 443/tcp
ufw --force enable

if ! id moswordsvc >/dev/null 2>&1; then
    useradd --system --user-group --home-dir /var/lib/mosword --shell /usr/sbin/nologin moswordsvc
fi
[[ $(getent passwd moswordsvc | cut -d: -f7) == /usr/sbin/nologin ]] || die 'moswordsvc phải dùng /usr/sbin/nologin.'
install -d -o root -g root -m 755 /opt/mosword /opt/mosword/releases
install -d -o root -g root -m 700 /etc/mosword /etc/mosword/tls /var/backups/mosword
install -d -o root -g moswordsvc -m 750 /var/lib/mosword
for name in packages dataprotection; do
    install -d -o moswordsvc -g moswordsvc -m 700 "/var/lib/mosword/$name"
done
install -d -o root -g moswordsvc -m 750 /var/lib/mosword/keys
install -d -o root -g moswordsvc -m 750 /var/lib/mosword/incoming
[[ ! -e /opt/mosword/current || -L /opt/mosword/current ]] || die '/opt/mosword/current phải là symlink do bước publish quản lý.'

if [[ ! -e /swapfile ]]; then
    fallocate -l 2G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
fi
[[ $(stat -c %s /swapfile) -eq 2147483648 ]] || die '/swapfile đã tồn tại nhưng không đúng 2 GiB; kiểm tra thủ công.'
chmod 600 /swapfile
if ! swapon --noheadings --show=NAME | grep -Fxq /swapfile; then swapon /swapfile; fi
if ! grep -Eq '^/swapfile[[:space:]]' /etc/fstab; then printf '/swapfile none swap sw 0 0\n' >>/etc/fstab; fi

install -d -o root -g root -m 755 /etc/postgresql/16/main/conf.d
cat >/etc/postgresql/16/main/conf.d/99-mosword.conf <<'PGCONF'
listen_addresses = 'localhost'
shared_buffers = '512MB'
effective_cache_size = '1536MB'
work_mem = '8MB'
max_connections = 50
password_encryption = 'scram-sha-256'
PGCONF
chmod 644 /etc/postgresql/16/main/conf.d/99-mosword.conf
systemctl enable postgresql
systemctl restart postgresql

runtime_env=/etc/mosword/mosword.env
migrate_env=/etc/mosword/migrate.env
# Keep credentials stable across repeated runs, including a run interrupted before SQL.
if [[ -e $runtime_env || -e $migrate_env ]]; then
    [[ -f $runtime_env && ! -L $runtime_env && -f $migrate_env && ! -L $migrate_env ]] || die 'Thiếu một tệp môi trường hoặc có symlink; khôi phục bản sao lưu trước.'
    for env_file in "$runtime_env" "$migrate_env"; do
        [[ $(stat -c '%u:%a' "$env_file") == 0:600 ]] || die 'Tệp môi trường phải thuộc root và có quyền 600.'
    done
    app_password=$(sed -n "s/^ConnectionStrings__Database='Host=127.0.0.1;Port=5432;Database=mosword;Username=mosword_app;Password=\([0-9a-f]*\);Maximum Pool Size=20'$/\1/p" "$runtime_env")
    migrate_password=$(sed -n "s/^ConnectionStrings__Database='Host=127.0.0.1;Port=5432;Database=mosword;Username=mosword_migrator;Password=\([0-9a-f]*\);Maximum Pool Size=5'$/\1/p" "$migrate_env")
    [[ $app_password =~ ^[0-9a-f]{64}$ && $migrate_password =~ ^[0-9a-f]{64}$ ]] || die 'Định dạng credentials hiện tại không hợp lệ; không tự thay mật khẩu.'
else
    app_password=$(openssl rand -hex 32)
    migrate_password=$(openssl rand -hex 32)
    cat >"$runtime_env" <<ENV
ConnectionStrings__Database='Host=127.0.0.1;Port=5432;Database=mosword;Username=mosword_app;Password=$app_password;Maximum Pool Size=20'
ASPNETCORE_ENVIRONMENT='Production'
ASPNETCORE_URLS='http://127.0.0.1:5000'
ASPNETCORE_HTTPS_PORT='443'
Packages__StorageRoot='/var/lib/mosword/packages'
Packages__SigningKeyPath='/var/lib/mosword/keys/manifest-signing.key'
DataProtection__KeyPath='/var/lib/mosword/dataprotection'
ENV
    # The DDL credential never appears in the service EnvironmentFile.
    cat >"$migrate_env" <<ENV
ConnectionStrings__Database='Host=127.0.0.1;Port=5432;Database=mosword;Username=mosword_migrator;Password=$migrate_password;Maximum Pool Size=5'
ASPNETCORE_ENVIRONMENT='Production'
Packages__StorageRoot='/var/lib/mosword/packages'
Packages__SigningKeyPath='/var/lib/mosword/keys/manifest-signing.key'
DataProtection__KeyPath='/var/lib/mosword/dataprotection'
ENV
fi
chown root:root "$runtime_env" "$migrate_env"
chmod 600 "$runtime_env" "$migrate_env"
# Passwords are random hex; SQL goes through stdin, never argv or console output.
if ! runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 >/dev/null 2>&1 <<SQL
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_duration_statement = -1;
SET log_duration = off;
SELECT 'CREATE ROLE mosword_migrator LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='mosword_migrator')\gexec
SELECT 'CREATE ROLE mosword_app LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='mosword_app')\gexec
ALTER ROLE mosword_migrator WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$migrate_password';
ALTER ROLE mosword_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$app_password';
SELECT 'CREATE DATABASE mosword OWNER mosword_migrator' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='mosword')\gexec
ALTER DATABASE mosword OWNER TO mosword_migrator;
REVOKE ALL ON DATABASE mosword FROM PUBLIC;
GRANT CONNECT ON DATABASE mosword TO mosword_app;
\connect mosword
ALTER SCHEMA public OWNER TO mosword_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO mosword_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO mosword_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO mosword_app;
ALTER DEFAULT PRIVILEGES FOR ROLE mosword_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mosword_app;
ALTER DEFAULT PRIVILEGES FOR ROLE mosword_migrator IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO mosword_app;
SQL
then
    unset app_password migrate_password
    die 'Không cấu hình được PostgreSQL; kiểm tra dịch vụ/quyền DBA. Không in SQL hoặc mật khẩu ra log.'
fi
unset app_password migrate_password

signing_key=/var/lib/mosword/keys/manifest-signing.key
[[ ! -L $signing_key ]] || die 'Khóa ký không được là symlink.'
if [[ ! -e $signing_key ]]; then
    seed_temporary=$(mktemp /var/lib/mosword/keys/.seed.XXXXXXXX)
    openssl rand -out "$seed_temporary" 32
    chown root:moswordsvc "$seed_temporary"
    chmod 640 "$seed_temporary"
    mv -T -- "$seed_temporary" "$signing_key"
fi
[[ -f $signing_key && ! -L $signing_key && $(stat -c %s "$signing_key") -eq 32 ]] || die 'Khóa ký phải là raw Ed25519 seed 32 byte; không tự tạo lại khóa đã tồn tại.'
chown root:moswordsvc "$signing_key"
chmod 640 "$signing_key"

install -d -o root -g root -m 755 /usr/local/lib/mosword-deploy
for file in "$script_dir"/*; do
    [[ -f $file && ! -L $file ]] || continue
    [[ $script_dir != /usr/local/lib/mosword-deploy ]] || break
    install -o root -g root -m 644 "$file" /usr/local/lib/mosword-deploy/
done
find /usr/local/lib/mosword-deploy -maxdepth 1 -type f -name '*.sh' -exec chmod 755 {} +
install -o root -g root -m 644 "$script_dir/mosword.service" /etc/systemd/system/mosword.service
install -o root -g root -m 644 "$script_dir/mosword-backup.service" /etc/systemd/system/mosword-backup.service
install -o root -g root -m 644 "$script_dir/mosword-backup.timer" /etc/systemd/system/mosword-backup.timer
install -d -m 755 /etc/systemd/journald.conf.d
install -o root -g root -m 644 "$script_dir/journald.conf" /etc/systemd/journald.conf.d/99-mosword.conf
install -o root -g root -m 644 "$script_dir/fail2ban.local" /etc/fail2ban/jail.d/mosword.local
install -o root -g root -m 644 "$script_dir/50unattended-upgrades" /etc/apt/apt.conf.d/99-mosword-security
printf 'APT::Periodic::Update-Package-Lists "1";\nAPT::Periodic::Unattended-Upgrade "1";\n' >/etc/apt/apt.conf.d/20auto-upgrades
systemctl daemon-reload
systemctl enable --now mosword-backup.timer
systemctl restart systemd-journald
systemctl enable --now fail2ban unattended-upgrades
# Do not activate a site referencing certificates that do not exist yet.
if [[ -L /etc/nginx/sites-enabled/default && $(readlink -f /etc/nginx/sites-enabled/default) == /etc/nginx/sites-available/default ]]; then
    rm /etc/nginx/sites-enabled/default
fi
nginx -t
systemctl enable --now nginx

# A previous verbal check is not enough: verify keys and require a live confirmation.
ssh_user=mosword
ssh_home=$(getent passwd "$ssh_user" | cut -d: -f6) || die 'Chưa có tài khoản SSH mosword.'
[[ -n $ssh_home && -d $ssh_home && ! -L $ssh_home ]] || die 'Không xác minh được home của mosword.'
[[ $ssh_home == /home/mosword && ! -L /home/mosword/mosword-backups ]] || die 'Thư mục xuất backup phải ở /home/mosword/mosword-backups và không được là symlink.'
install -d -o mosword -g mosword -m 700 /home/mosword/mosword-backups
authorized_keys="$ssh_home/.ssh/authorized_keys"
if [[ -f $authorized_keys && ! -L $authorized_keys && ! -L $ssh_home/.ssh ]] &&
    [[ $(stat -c %U "$ssh_home") == "$ssh_user" ]] &&
    (( (8#$(stat -c %a "$ssh_home") & 0022) == 0 )) &&
    [[ $(stat -c %U "$authorized_keys") == "$ssh_user" ]] &&
    [[ $(stat -c %U "$ssh_home/.ssh") == "$ssh_user" ]] &&
    [[ $(stat -c %a "$authorized_keys") == 600 && $(stat -c %a "$ssh_home/.ssh") == 700 ]] &&
    awk '$1 == "ssh-ed25519" { found=1 } END { exit !found }' "$authorized_keys" &&
    ssh-keygen -l -f "$authorized_keys" >/dev/null &&
    id -nG "$ssh_user" | tr ' ' '\n' | grep -Fxq sudo; then
    printf 'Đã kiểm tra authorized_keys Ed25519 của mosword. Giữ phiên này và kiểm tra đăng nhập SSH khóa + sudo trong một phiên mới.\n'
    if [[ -t 0 && -t 1 ]]; then
        read -r -p 'Chỉ sau khi phiên mới thành công, nhập XAC NHAN SSH để tắt mật khẩu và root SSH (Enter để bỏ qua): ' confirmation
        if [[ $confirmation == 'XAC NHAN SSH' ]]; then
            ssh_config=/etc/ssh/sshd_config.d/00-mosword.conf
            [[ ! -L $ssh_config ]] || die 'Cấu hình SSH quản lý không được là symlink.'
            ssh_backup=$(mktemp /etc/ssh/sshd_config.d/.mosword-backup.XXXXXX)
            had_ssh_config=false
            if [[ -f $ssh_config ]]; then cp -p "$ssh_config" "$ssh_backup"; had_ssh_config=true; fi
            restore_ssh_config() {
                if [[ $had_ssh_config == true ]]; then cp -p "$ssh_backup" "$ssh_config"; else rm -f "$ssh_config"; fi
                rm -f "$ssh_backup"
            }
            cat >/etc/ssh/sshd_config.d/00-mosword.conf <<'SSHCONF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
SSHCONF
            if ! /usr/sbin/sshd -t; then
                restore_ssh_config
                die 'Kiểm tra SSH thất bại; đã khôi phục cấu hình cũ, chưa reload.'
            fi
            # Check both identities, including Match rules for this SSH client's address.
            ssh_address=${SSH_CONNECTION:-}
            ssh_address=${ssh_address%% *}
            ssh_address=${ssh_address:-127.0.0.1}
            for identity in mosword root; do
                if ! effective=$(/usr/sbin/sshd -T -C "user=$identity,host=localhost,addr=$ssh_address"); then
                    restore_ssh_config
                    die 'Không đọc được cấu hình SSH hiệu lực; đã khôi phục cấu hình cũ.'
                fi
                for setting in 'passwordauthentication no' 'kbdinteractiveauthentication no' 'permitrootlogin no' 'pubkeyauthentication yes'; do
                    if ! grep -Fxq "$setting" <<<"$effective"; then
                        restore_ssh_config
                        die 'Cấu hình SSH khác ghi đè hardening; đã khôi phục cấu hình cũ, chưa reload. Kiểm tra sshd -T và cấu hình Match.'
                    fi
                done
            done
            if ! systemctl reload ssh; then
                restore_ssh_config
                die 'Reload SSH thất bại; đã khôi phục cấu hình cũ. Giữ phiên đang mở và kiểm tra dịch vụ.'
            fi
            rm -f "$ssh_backup"
            printf 'Đã reload SSH. Kiểm tra lại bằng phiên mới trước khi đóng phiên hiện tại.\n'
        else
            printf 'Đã bỏ qua hardening SSH; cấu hình xác thực cũ vẫn giữ nguyên.\n'
        fi
    else
        printf 'Không có terminal tương tác: bỏ qua hardening SSH. Chạy lại trực tiếp sau khi kiểm tra SSH khóa.\n'
    fi
else
    printf 'Chưa đủ điều kiện xác minh khóa Ed25519/quyền tệp/sudo của mosword: không thay xác thực SSH.\n'
fi
printf 'Provision hoàn tất. Chạy 02-certificate.sh rồi publish; chưa khởi động mosword khi chưa có release.\n'
