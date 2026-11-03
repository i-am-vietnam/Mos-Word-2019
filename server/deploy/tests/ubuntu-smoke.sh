#!/usr/bin/env bash
set -euo pipefail
umask 077
apt-get remove -y curl >/dev/null

# This disposable container tests real apt/PostgreSQL/nginx/OpenSSL/.NET/filesystem operations.
# Only systemd, firewall and swap kernel effects are mocked; no VPS is contacted.
mkdir -p /test-bin /run/lock /run/sshd
cp -r /workspace/server/deploy /test-deploy
chmod 755 /test-deploy/*.sh
cd /test-deploy
shellcheck --external-sources /test-deploy/*.sh
for file in /test-deploy/*.sh; do bash -n "$file"; done
useradd -m -s /bin/bash -G sudo mosword
install -d -o mosword -g mosword -m 700 /home/mosword/.ssh
ssh-keygen -q -t ed25519 -N '' -f /tmp/test-login
install -o mosword -g mosword -m 600 /tmp/test-login.pub /home/mosword/.ssh/authorized_keys
ssh-keygen -A >/dev/null
cat >/test-bin/swapon <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
if [[ ${1:-} == --noheadings ]]; then printf '/swapfile\n'; fi
MOCK
cat >/test-bin/ufw <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
if [[ ${1:-} == show ]]; then printf 'ufw allow 22/tcp\nufw allow 443/tcp\n'; fi
if [[ ${1:-} == status ]]; then printf 'Status: active\nDefault: deny (incoming), allow (outgoing), disabled (routed)\n'; fi
MOCK
cat >/test-bin/systemctl <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
exec 9>&-
command=${1:-}; shift || true
service=${*: -1}
case "$service" in
    postgresql) if [[ $command == restart ]]; then pg_ctlcluster 16 main restart; fi; exit 0 ;;
    nginx) if [[ $command == reload ]]; then nginx -s reload; else nginx -t; if [[ ! -s /run/nginx.pid ]] || ! kill -0 "$(cat /run/nginx.pid)" 2>/dev/null; then nginx; fi; fi; exit 0 ;;
    mosword.service)
        case "$command" in
            is-active) [[ -f /run/test-mosword.pid ]] && kill -0 "$(cat /run/test-mosword.pid)" 2>/dev/null; exit $? ;;
            enable|is-enabled) exit 0 ;;
            start) if [[ -f /run/test-mosword.pid ]] && kill -0 "$(cat /run/test-mosword.pid)" 2>/dev/null; then exit 0; fi ;;
            stop|restart)
                if [[ -f /run/test-mosword.pid ]]; then kill -- "-$(cat /run/test-mosword.pid)" 2>/dev/null || true; rm -f /run/test-mosword.pid; fi
                [[ $command != stop ]] || exit 0 ;;
        esac
        if [[ -f /run/fail-next-start && $(readlink -f /opt/mosword/current) != "$(cat /run/fail-next-start)" ]]; then
            rm /run/fail-next-start
            setsid /usr/bin/sleep 300 >/tmp/test-mosword.log 2>&1 &
        else
            set -a
            # shellcheck disable=SC1091
            source /etc/mosword/mosword.env
            set +a
            cd /opt/mosword/current
            setsid runuser -m -u moswordsvc -- /usr/bin/dotnet /opt/mosword/current/MosWord2019.Server.dll >/tmp/test-mosword.log 2>&1 &
        fi
        printf '%s\n' "$!" >/run/test-mosword.pid
        exit 0 ;;
esac
exit 0
MOCK
chmod 755 /test-bin/*
# Provision does not override PATH, while production helper scripts intentionally do.
# Install the systemctl mock at its normal location only inside this disposable container.
cp /test-bin/systemctl /usr/bin/systemctl
cp /test-bin/ufw /usr/sbin/ufw
PATH=/test-bin:$PATH SUDO_USER=mosword bash /test-deploy/01-provision.sh </dev/null
command -v curl >/dev/null
[[ ! -f /etc/ssh/sshd_config.d/00-mosword.conf ]]
secret_before=$(sha256sum /etc/mosword/mosword.env /etc/mosword/migrate.env /var/lib/mosword/keys/manifest-signing.key)
PATH=/test-bin:$PATH SUDO_USER=mosword bash /test-deploy/01-provision.sh </dev/null
[[ $secret_before == "$(sha256sum /etc/mosword/mosword.env /etc/mosword/migrate.env /var/lib/mosword/keys/manifest-signing.key)" ]]
[[ $(stat -c '%U:%G:%a' /var/lib/mosword/keys) == root:moswordsvc:750 ]]
[[ $(stat -c '%U:%G:%a' /var/lib/mosword/keys/manifest-signing.key) == root:moswordsvc:640 ]]
runuser -u moswordsvc -- test -r /var/lib/mosword/keys/manifest-signing.key
if runuser -u moswordsvc -- test -w /var/lib/mosword/keys; then exit 1; fi
[[ -z $(dotnet --list-sdks) ]]
[[ $(runuser -u postgres -- psql -X -Atc "SHOW shared_buffers") == 512MB ]]
[[ $(runuser -u postgres -- psql -X -Atc "SHOW max_connections") == 50 ]]
printf 'PASS provision chạy lại và giữ bí mật; SSH không đổi khi không xác nhận.\n'

bash /test-deploy/02-certificate.sh --ip 192.0.2.10
certificate_before=$(sha256sum /etc/mosword/tls/server.crt /etc/mosword/tls/server.key)
bash /test-deploy/02-certificate.sh --ip 192.0.2.10
[[ $certificate_before == "$(sha256sum /etc/mosword/tls/server.crt /etc/mosword/tls/server.key)" ]]
openssl x509 -in /etc/mosword/tls/server.crt -noout -ext subjectAltName | grep -Fq 'IP Address:192.0.2.10'
nginx -T 2>&1 | grep -Fq 'ssl_protocols TLSv1.2 TLSv1.3;'
if ss -lnt | grep -Eq ':80[[:space:]]'; then printf 'FAIL nginx cổng80\n' >&2; exit 1; fi
printf 'PASS TLS SAN/P256, giữ pin qua rerun và nginx không nghe80.\n'

[[ -f /publish/MosWord2019.Server.dll ]]
tar -czf /tmp/server.tar.gz -C /publish .
digest=$(sha256sum /tmp/server.tar.gz | cut -d ' ' -f1)
bash /test-deploy/03-install-release.sh /tmp/server.tar.gz "$digest"
first=$(readlink -f /opt/mosword/current)
curl -fsS -H 'X-Forwarded-Proto: https' http://127.0.0.1:5000/health/ready >/dev/null
[[ $(runuser -u postgres -- psql -X -Atd mosword -c 'SELECT COUNT(*) FROM "__EFMigrationsHistory"') -ge 6 ]]
[[ $(runuser -u postgres -- psql -X -Atd mosword -c "SELECT has_schema_privilege('mosword_app','public','CREATE')") == f ]]
[[ $(runuser -u postgres -- psql -X -Atd mosword -c "SELECT has_table_privilege('mosword_app','\"Accounts\"','INSERT')") == t ]]
printf 'PASS publish linux-x64/runtime, migrate và runtime không có DDL.\n'

# A valid archive reaches migration, then fails health; the previous running release must recover.
printf '%s\n' "$first" >/run/fail-next-start
if bash /test-deploy/03-install-release.sh /tmp/server.tar.gz "$digest"; then printf 'FAIL phải rollback\n' >&2; exit 1; fi
[[ $(readlink -f /opt/mosword/current) == "$first" ]]
curl -fsS -H 'X-Forwarded-Proto: https' http://127.0.0.1:5000/health/ready >/dev/null
printf 'PASS health lỗi tự rollback về bản trước.\n'

# Repeated successful deployments retain only three release directories.
for _ in 1 2 3; do
    /usr/bin/sleep 2
    bash /test-deploy/03-install-release.sh /tmp/server.tar.gz "$digest"
done
[[ $(find /opt/mosword/releases -mindepth 1 -maxdepth 1 -type d | wc -l) -eq 3 ]]
python3 - <<'PY'
import io, tarfile
for kind in ('traversal', 'symlink'):
    with tarfile.open('/tmp/' + kind + '.tar.gz', 'w:gz') as archive:
        member = tarfile.TarInfo('../escaped' if kind == 'traversal' else 'link')
        if kind == 'symlink':
            member.type = tarfile.SYMTYPE
            member.linkname = '/etc/passwd'
            archive.addfile(member)
        else:
            member.size = 1
            archive.addfile(member, io.BytesIO(b'x'))
PY
for kind in traversal symlink; do
    if bash /test-deploy/03-install-release.sh "/tmp/$kind.tar.gz" "$(sha256sum "/tmp/$kind.tar.gz" | cut -d ' ' -f1)"; then exit 1; fi
done
[[ ! -e /opt/mosword/escaped ]]
printf 'PASS retention3 và từ chối archive traversal/symlink.\n'

cp -r /workspace/MosWord2019.WinForms/Projects /var/lib/mosword/incoming/smoke
bash /test-deploy/04-import-packages.sh smoke
[[ ! -e /var/lib/mosword/incoming/smoke ]]
[[ $(runuser -u postgres -- psql -X -Atd mosword -c 'SELECT COUNT(DISTINCT "Code") FROM "Packages"') == 28 ]]
python3 - <<'PY'
import datetime, pathlib
root = pathlib.Path('/var/backups/mosword')
today = datetime.datetime.now(datetime.timezone.utc).date()
for days in range(1, 17):
    name = 'daily-' + (today - datetime.timedelta(days=days)).strftime('%Y%m%d') + 'T010000Z.tar.gz'
    (root / name).write_bytes(b'retention-fixture')
for hour in range(1, 20):
    (root / ('daily-' + today.strftime('%Y%m%d') + f'T{hour:02d}0000Z.tar.gz')).write_bytes(b'retention-fixture')
PY
/usr/bin/sleep 2
bash /test-deploy/06-backup.sh
[[ $(find /var/backups/mosword -maxdepth 1 -name 'daily-*.tar.gz' | wc -l) -eq 14 ]]
backup=$(find /var/backups/mosword -maxdepth 1 -name 'daily-*.tar.gz' | sort | tail -1)
mkdir /tmp/restore-test
tar -xzf "$backup" -C /tmp/restore-test
runuser -u postgres -- createdb mosword_restore_test
runuser -u postgres -- pg_restore --exit-on-error --dbname=mosword_restore_test < /tmp/restore-test/database.dump
[[ $(runuser -u postgres -- psql -X -Atd mosword_restore_test -c 'SELECT COUNT(DISTINCT "Code") FROM "Packages"') == 28 ]]
tar -tzf /tmp/restore-test/state.tar.gz | grep -Fq 'var/lib/mosword/keys/manifest-signing.key'
printf 'PASS import28, dọn plaintext và backup/restore thật.\n'
python3 - "$backup" <<'PY'
import hashlib, os, pathlib, pty, secrets, select, subprocess, sys, time

def interact(arguments, answers):
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(arguments[0], arguments)
    received = b''
    deadline = time.monotonic() + 40
    while answers:
        if time.monotonic() > deadline:
            raise RuntimeError('Hết thời gian chờ prompt.')
        if not select.select([fd], [], [], 1)[0]:
            continue
        received += os.read(fd, 8192)
        prompt, answer = answers[0]
        if prompt.encode() in received:
            os.write(fd, answer.encode() + b'\n')
            answers.pop(0)
            received = b''
    while True:
        done, status = os.waitpid(pid, os.WNOHANG)
        if done:
            os.close(fd)
            if os.waitstatus_to_exitcode(status) != 0:
                raise RuntimeError('Lệnh terminal bị từ chối.')
            return
        if time.monotonic() > deadline:
            raise RuntimeError('Lệnh terminal không kết thúc.')
        if select.select([fd], [], [], 0.2)[0]:
            try:
                os.read(fd, 8192)
            except OSError:
                pass

password = 'Test-only-Aa1!' + secrets.token_urlsafe(24)
interact(['/test-deploy/05-create-admin.sh', 'owner-smoke'], [('Mật khẩu', password), ('Nhập lại', password)])
backup = pathlib.Path(sys.argv[1])
interact(['/test-deploy/07-export-backup.sh', backup.name], [('Mật khẩu', password), ('Nhập lại', password)])
encrypted = pathlib.Path('/home/mosword/mosword-backups') / (backup.name + '.enc')
output = pathlib.Path('/tmp/decrypted-test.tar.gz')
subprocess.run(['openssl', 'enc', '-d', '-aes-256-cbc', '-pbkdf2', '-iter', '600000', '-md', 'sha256',
                '-in', str(encrypted), '-out', str(output), '-pass', 'stdin'],
               input=(password + '\n').encode(), check=True, capture_output=True)
assert hashlib.sha256(output.read_bytes()).digest() == hashlib.sha256(backup.read_bytes()).digest()
assert encrypted.stat().st_mode & 0o777 == 0o600
print('PASS admin password ẩn/env và export backup mã hóa round-trip.')
interact(['/usr/bin/env', 'PATH=/test-bin:/usr/sbin:/usr/bin:/sbin:/bin', 'SUDO_USER=mosword', '/usr/bin/bash', '/test-deploy/01-provision.sh'],
         [('nhập XAC NHAN SSH', 'XAC NHAN SSH')])
settings = subprocess.check_output(['/usr/sbin/sshd', '-T']).decode()
assert 'passwordauthentication no\n' in settings and 'permitrootlogin no\n' in settings
print('PASS SSH chỉ harden sau xác nhận terminal; syntax/effective settings thật.')
PY
bash /test-deploy/08-public-values.sh
systemd-analyze verify /etc/systemd/system/mosword.service /etc/systemd/system/mosword-backup.service /etc/systemd/system/mosword-backup.timer
pin=$(openssl x509 -in /etc/mosword/tls/server.crt -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | openssl base64 -A)
bash /test-deploy/99-verify.sh 192.0.2.10 127.0.0.1 "sha256//$pin"
if bash /test-deploy/99-verify.sh 192.0.2.10 127.0.0.1 'sha256//AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='; then exit 1; fi
bash /workspace/server/deploy/tests/verify-ports.sh
printf 'PASS Ubuntu24.04 smoke; systemd/firewall/swapon/SSH interactive vẫn cần nghiệm thu VPS.\n'
