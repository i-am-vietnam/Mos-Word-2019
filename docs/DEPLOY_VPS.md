# Triển khai MOS Word 2019 trên VPS — C6

Hướng dẫn cho Ubuntu24.04 LTS x64,2vCPU/4GB/35GB, user `mosword` có sudo và SSH key. Owner đã xác nhận SSH key từ PC ngày2026-11-03; script vẫn kiểm tra lại file khóa và yêu cầu xác nhận trước khi tắt mật khẩu/root SSH. Agent không truy cập VPS. Không gửi IP/password/private key vào chat, không thêm chúng vào repository.

Các khối **PC** chạy PowerShell trên Windows tại thư mục repository. Các khối **VPS** chạy trong terminal SSH. Không chạy `dotnet build/publish` trên VPS: VPS chỉ cài ASP.NET Core Runtime10 và chạy bản publish từ PC. Giữ cửa sổ SSH hiện tại cùng provider web console để dự phòng.

## 1. Chuẩn bị trên PC và chép bộ deploy

Cần .NET10 SDK, Visual Studio MSBuild/.NET Framework4.7.2 targeting pack, PowerShell và OpenSSH Client (`ssh`, `scp`, `tar`). Các script PowerShell hỗ trợ Windows PowerShell5.1 và PowerShell7. Không đổi thư mục đích thành source hoặc thư mục cá nhân.

```powershell
Set-Location 'C:\Users\HUYNH HAU\source\repos\MosWord2019'
$VpsIp = Read-Host 'IPv4 VPS'
$SshKey = Read-Host 'Đường dẫn private SSH key đã dùng đăng nhập (chỉ giữ trên PC)'
Get-Item -LiteralPath $SshKey
dotnet --list-sdks
ssh -i $SshKey -o PasswordAuthentication=no -o StrictHostKeyChecking=yes "mosword@$VpsIp"
```

Nếu báo host key unknown/mismatch, đối chiếu fingerprint với provider console trước, không dùng `StrictHostKeyChecking=no`. Kiểm tra sudo trong cửa sổ mới: `sudo -v`. Giữ cửa sổ này mở. Quay về một cửa sổ PowerShell thứ hai:

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
tar -czf artifacts/mosword-deploy-tools.tar.gz -C server deploy
scp -i $SshKey -o StrictHostKeyChecking=yes artifacts/mosword-deploy-tools.tar.gz "mosword@${VpsIp}:/home/mosword/"
```

`.gitattributes` giữ shell scripts LF trên Windows. Nếu tải ZIP ngoài Git và gặp `bash\r`, đổi riêng tệp deploy sang UTF8/LF trước khi chép; không sửa file bài tập.

## 2. Provision một lần, có thể chạy lại

**VPS**:

```bash
mkdir -p /home/mosword/mosword-setup
tar -xzf /home/mosword/mosword-deploy-tools.tar.gz -C /home/mosword/mosword-setup
sudo bash /home/mosword/mosword-setup/deploy/01-provision.sh
```

Script cài runtime10/PostgreSQL16/nginx/ufw/fail2ban/security updates; mở incoming22/443, không mở80/5000/5432. Nếu UFW có rule khác, script dừng để bạn kiểm `sudo ufw show added`; không tự xóa rule của bạn. Chỉ xử lý sau khi hiểu rule đó và chắc SSH dùng22. Host khác/multi-service cần kế hoạch riêng.

Script tạo swap2GiB; tuning `shared_buffers=512MB`, `effective_cache_size=1536MB`, `work_mem=8MB`, `max_connections=50`; PostgreSQL chỉ localhost. User runtime `mosword_app` chỉ CRUD, `mosword_migrator` có DDL trên riêng DB `mosword`, không superuser/createdb/createrole. Password ngẫu nhiên ở VPS, không in ra.

Cuối script, mở thêm **một SSH mới từ PC**, kiểm khóa + `sudo -v`, rồi chỉ khi thành công mới gõ chính xác `XAC NHAN SSH` ở prompt provision. Script kiểm authorized_keys Ed25519/quyền file/sudo và cấu hình sshd hiệu lực trước reload. Enter để bỏ qua; khi chạy không có terminal, không đổi SSH. Không đóng phiên cũ trước khi kiểm tra đăng nhập sau hardening. Lỗi cấu hình SSH được phục hồi managed drop-in; provider console là đường dự phòng.

Chạy lại không đổi DB password/signing key/TLS. Nếu một nửa cặp env/key thiếu hoặc config bị sửa sai, script dừng; khôi phục backup, không xóa secret để ép sinh mới.

## 3. Tạo TLS và tùy chọn giới hạn trang Admin

**VPS** nhập IP bằng biến trong terminal, không sửa script:

```bash
read -r -p 'IPv4 VPS: ' VPS_IP
sudo /usr/local/lib/mosword-deploy/02-certificate.sh --ip "$VPS_IP"
```

Kết quả: chứng thư self-signed ECDSA P256, SAN đúng IPv4,3650ngày; nginx443 TLS1.2/1.3, HSTS, body512KiB, proxy127.0.0.1:5000. Dòng Base64 là **SPKI public pin**, có thể ghi vào nơi lưu cấu hình build cá nhân. Private key tại `/etc/mosword/tls/server.key`, không chép vào repo.

Nếu IP Internet của owner ổn định, có thể giới hạn Admin (chỉ IPv4/CIDR, không khoảng trắng):

```bash
read -r -p 'IPv4/CIDR được mở Admin, ngăn bằng dấu phẩy: ' ADMIN_IPS
sudo /usr/local/lib/mosword-deploy/02-certificate.sh --ip "$VPS_IP" --admin-allow-list "$ADMIN_IPS"
```

Không truyền allow-list nghĩa là Admin mở cho mọi IP nhưng vẫn bắt buộc login/CSRF/rate limit/secure cookie. Chạy lại không allow-list sẽ bỏ giới hạn cũ. Thay IP PC có thể làm bạn nhận403; sửa từ SSH bằng lệnh trên, không mở thêm firewall. Trình duyệt sẽ cảnh báo self-signed: đối chiếu certificate/fingerprint qua SSH trước khi chấp nhận ngoại lệ cho IP này; không tắt kiểm TLS toàn hệ thống. Client không dựa vào ngoại lệ trình duyệt mà kiểm pin nhúng.

## 4. Publish server từ PC

**PC**, tại repository (giữ các biến `$VpsIp`, `$SshKey`):

```powershell
./server/deploy/publish.ps1 -VpsHost $VpsIp -IdentityFile $SshKey
```

Script publish Release `linux-x64 --self-contained false`, tạo archive/SHA256 trong `artifacts/deploy/`, SCP rồi SSH gọi installer bằng sudo. Nhập password sudo trực tiếp nếu được hỏi; không ghi password vào command. SSH dùng host-key checking và BatchMode; nếu private key có passphrase, nạp key vào SSH agent trước: ở PowerShell Run as administrator chạy `Set-Service ssh-agent -StartupType Automatic; Start-Service ssh-agent`, quay về PowerShell thường chạy `ssh-add $SshKey` rồi publish. Không bỏ BatchMode bằng cách đưa password vào script. Có thể chỉ tạo archive local bằng `-PrepareOnly` (không cần VpsHost, không kết nối VPS).

Installer khóa thao tác, root-copy/hash/validate archive, tạo release root-owned, backup trước migration khi đã có bản cũ, dừng service, chạy `migrate` bằng DDL credential riêng, atomic switch `current`, restart và đợi `/health/ready`. Lỗi migration/start/health trả exit khác0 và phục hồi binary/symlink bản trước; không rollback schema tự động. Chỉ dùng migration tương thích ngược, kiểm trên staging trước; migration phá schema cần maintenance/backup/restore có kế hoạch. Lần đầu không có bản trước thì dừng service lỗi, không giữ symlink lỗi. Giữ3release mới sau deploy thành công.

Kiểm **VPS**:

```bash
sudo systemctl status mosword --no-pager
sudo journalctl -u mosword -n 50 --no-pager
curl -fsS -H 'X-Forwarded-Proto: https' http://127.0.0.1:5000/health/ready
```

Mong đợi `active (running)` và `Healthy`. Header ở curl nội bộ giả lập nginx loopback đã được trust; API public không được tự trust forwarded header từ Internet. Thư mục:

| Đường dẫn | Mục đích |
|---|---|
| `/opt/mosword/releases/<timestamp>-<id>`, `/opt/mosword/current` | Binary bất biến và symlink bản chạy |
| `/usr/local/lib/mosword-deploy` | Script quản trị root-owned |
| `/etc/mosword/mosword.env`, `migrate.env` | root600, credential runtime/DDL tách biệt |
| `/var/lib/mosword/{packages,keys,dataprotection}` | Package mã hóa + per-package keys, signing seed, key ring bền vững |
| `/etc/mosword/tls` | Certificate và private key TLS |
| `/var/backups/mosword` | Backup root-only |

Không xóa/đổi signing key hoặc key ring khi cập nhật. Journald bị giới hạn dung lượng; nginx có log rotation do gói Ubuntu. Security upgrades tự chạy, không tự reboot; owner theo dõi `/var/run/reboot-required` và chọn maintenance.

## 5. Nhập 28 gói và tạo admin đầu tiên

**PC**:

```powershell
tar -czf artifacts/mosword-projects.tar.gz -C MosWord2019.WinForms Projects
scp -i $SshKey -o StrictHostKeyChecking=yes artifacts/mosword-projects.tar.gz "mosword@${VpsIp}:/home/mosword/"
```

**VPS** (archive bạn vừa tạo, không nhận archive bài tập từ người lạ):

```bash
sudo install -d -o root -g moswordsvc -m 750 /var/lib/mosword/incoming/initial
sudo tar -xzf /home/mosword/mosword-projects.tar.gz --strip-components=1 -C /var/lib/mosword/incoming/initial --no-same-owner
sudo /usr/local/lib/mosword-deploy/04-import-packages.sh initial
rm /home/mosword/mosword-projects.tar.gz
sudo /usr/local/lib/mosword-deploy/05-create-admin.sh owner
sudo /usr/local/lib/mosword-deploy/08-public-values.sh
```

Import phải báo đủ28project; chỉ sau DB count đúng mới shred best-effort và xóa thư mục `incoming/initial`. Lần nhập lại version giống cũ có thể tạo0version mới nhưng vẫn xác minh đủ28. Nếu lỗi, giữ upload để sửa, không xóa package/key/version trong state. NVMe/snapshot/provider backup không bảo đảm xóa vật lý bằng shred; giữ upload ngắn, tránh sao lưu plaintext. Archive trên PC cũng là plaintext: chỉ giữ ở nơi owner kiểm soát ngoài bản phân phối, không ship cho learner.

Password admin nhập ẩn hai lần, chỉ qua environment tạm, không qua argv/history/log. CLI chỉ tạo admin đầu tiên; lần sau dùng admin web, không tự ghi đè tài khoản. Mở `https://IP/Admin/Login`, login owner, tạo learner3lượt để kiểm client.

`08-public-values.sh` chỉ in `SPKI_SHA256` và `ED25519_PUBLIC_KEY` Base64. Raw signing seed32byte đã sinh trên VPS, không phải public key; không copy seed vào build script/PC repository. Mất/đổi seed buộc build/phát hành client mới.

## 6. Build client production trên PC

```powershell
$SpkiPin = Read-Host 'Giá trị SPKI_SHA256 (Base64, không có tiền tố sha256//)'
$ManifestPublicKey = Read-Host 'Giá trị ED25519_PUBLIC_KEY (Base64)'
./server/deploy/build-client.ps1 -VpsIp $VpsIp -SpkiPin $SpkiPin -ManifestPublicKey $ManifestPublicKey
```

Script build Release bằng VS MSBuild, nhúng URL/pin trong EXE và public manifest key trong assembly Projects, không sửa source `.pub` hoặc App.config. Output mới ở `artifacts/client-production/...`; kiểm `public-trust.json`/SHA256 và thông báo verify thành công. Phân phối **toàn bộ thư mục output** cho learner (không chỉ EXE), cài .NET Framework4.7.2+/Word2019 theo yêu cầu client. Không phân phối Projects plaintext hoặc certificate private key.

Release bỏ qua dev override trong config, từ chối cert khác pin, và không nhận offline credentials. Đổi config cạnh EXE không thay trust. Test login/Training/P01/Testing/upload/admin theo `C5_LOCAL_TEST.md`; không dùng package/dev key khác để bỏ kiểm chữ ký. Nếu máy learner sai ngày giờ làm certificate chưa hợp lệ/hết hạn, chỉnh Windows time; licensing vẫn theo server.

## 7. Backup daily và tải về PC mỗi tuần

**VPS**, chạy backup đầu tiên rồi verify:

```bash
sudo /usr/local/lib/mosword-deploy/06-backup.sh
sudo systemctl list-timers mosword-backup.timer --no-pager
read -r -p 'SPKI_SHA256 Base64: ' SPKI_PIN
sudo /usr/local/lib/mosword-deploy/99-verify.sh "$VPS_IP" "$VPS_IP" "sha256//$SPKI_PIN"
sudo ls -l /var/backups/mosword
```

Timer03:15 **UTC** (10:15 giờ Việt Nam/Thái Lan), `Persistent=true` bù khi VPS tắt. Backup dừng app ngắn để DB/state nhất quán rồi start/health lại; chọn giờ phù hợp việc học trước khi phát hành. Giữ14ngày UTC, một snapshot/ngày (chạy thủ công/deploy cùng ngày thay snapshot atomically sau khi bản mới bền vững), và8weekly (Chủ nhật UTC), gồm pg_dump custom format + package/per-package keys/signing key/DP keys/env/TLS và release path. DB/gói lớn sẽ tăng thời gian dừng và dung lượng: theo dõi `df -h`, tải backup offsite; provider backup không thay pg_dump này.

Hằng tuần chọn **tên backup** từ `ls` (không nhập đường dẫn bất kỳ), ví dụ bằng biến do bạn copy:

```bash
read -r -p 'Tên daily-...tar.gz hoặc weekly-...tar.gz: ' BACKUP_NAME
sudo /usr/local/lib/mosword-deploy/07-export-backup.sh "$BACKUP_NAME"
```

Nhập passphrase riêng ít nhất20ký tự hai lần, lưu trong password manager. Kết quả ciphertext owner600 tại `/home/mosword/mosword-backups/<tên>.enc` và dòng SHA256 từ root; bao gồm mọi critical secret nêu trên, không cần SCP từng private key. **PC**:

```powershell
$BackupName = Read-Host 'Tên backup vừa export, không thêm .enc'
$BackupDirectory = Join-Path $env:USERPROFILE 'MosWordBackups'
New-Item -ItemType Directory -Force $BackupDirectory | Out-Null
scp -i $SshKey -o StrictHostKeyChecking=yes "mosword@${VpsIp}:/home/mosword/mosword-backups/${BackupName}.enc" $BackupDirectory
Get-FileHash -Algorithm SHA256 (Join-Path $BackupDirectory "$BackupName.enc")
```

Đối chiếu hash PC với dòng SHA256 vừa in qua SSH; nếu khác, không dùng file đó để restore. Giữ ít nhất hai bản offsite ở ổ mã hóa, passphrase nơi khác. Giữ thêm archive publish từ artifacts/deploy và commit Git tương ứng để có binary tương thích khi restore; backup state chỉ ghi release path, không mang binary. Không đặt backup vào repo/cloud công khai. AES256CBC/PBKDF2 600000 vòng ở helper là lớp bảo mật ciphertext; giữ SHA256 từ kênh SSH tin cậy để đối chiếu khi vận chuyển, không coi CBC là chữ ký chống sửa. Sau tải/kiểm đủ, có thể xóa riêng ciphertext cũ ở thư mục owner; không xóa backup root hoặc khóa đang chạy.

## 8. Thử restore một lần trước khi phát hành

Thử trên bản sao DB mới, không ghi đè DB thật. **VPS**, chọn một backup local như mục7 rồi:

```bash
sudo -i
read -r -p 'Tên backup local đã kiểm: ' BACKUP_NAME
RESTORE_DIR=$(mktemp -d /var/backups/mosword/restore-test.XXXXXXXX)
tar -xzf "/var/backups/mosword/$BACKUP_NAME" -C "$RESTORE_DIR"
runuser -u postgres -- createdb mosword_restore_test
runuser -u postgres -- pg_restore --exit-on-error --dbname=mosword_restore_test < "$RESTORE_DIR/database.dump"
runuser -u postgres -- psql -X -d mosword_restore_test -c 'SELECT COUNT(DISTINCT "Code") FROM "Packages";'
tar -tzf "$RESTORE_DIR/state.tar.gz"
```

Mong đợi count28 và đủ state/key/env/TLS. Nếu DB test đã có, dùng tên test mới hoặc kiểm/dọn đúng DB test trước; không dùng `DROP` trên `mosword`. Sau kiểm thành công:

```bash
runuser -u postgres -- dropdb mosword_restore_test
# Xác minh trước khi xóa duy nhất thư mục test vừa mktemp tạo.
printf '%s\n' "$RESTORE_DIR"
case "$RESTORE_DIR" in /var/backups/mosword/restore-test.*) rm -rf --one-file-system -- "$RESTORE_DIR" ;; *) exit 1 ;; esac
exit
```

Restore production phải có maintenance, backup trạng thái hiện tại và dừng app; giữ nguyên private keys khớp DB. Nếu từ PC ciphertext, SCP lên thư mục private owner, sau `sudo -i` giải mã bằng OpenSSL (prompt passphrase, không truyền `-k PASSWORD`):

```bash
umask 077
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in /home/mosword/mosword-backups/BACKUP_NAME.tar.gz.enc -out /var/backups/mosword/recovered.tar.gz
```

`BACKUP_NAME` là placeholder cần thay. Đối chiếu hash backup ciphertext, kiểm archive do chính bạn tạo và dùng provider console/restore rehearsal trước khi thay production. Không untar backup không tin cậy bằng root.

## 9. Khôi phục production hoặc chuyển VPS lớn hơn

1. Đặt maintenance, kết thúc các phiên và chờ queue kết quả client upload xong trước khi đổi IP (queue ghim origin cũ, không tự chuyển sang IP mới). Ngừng learner login và dừng app cũ để không có ghi mới. Chạy backup cuối cùng khi service dừng, export mã hóa và kiểm tải PC. Không chạy hai VPS cho cùng DB/key/storage trong quá trình chuyển.
2. Trên VPS mới Ubuntu24.04, chuẩn bị user/SSH-key như mục1, chạy provision và có sẵn bản publish tương thích. Trước restore, `sudo systemctl stop mosword` và dừng timer backup. Không import packages hoặc tạo admin mới.
3. SCP ciphertext từ PC tới `/home/mosword/mosword-backups`, giải mã vào root-only path; untar outer backup vào thư mục root-only. Phục hồi `state.tar.gz` vào `/` **trên VPS mới** (các path đã được kiểm) và chown packages/dataprotection cho `moswordsvc`, nhưng giữ thư mục keys root:moswordsvc750 và signing seed root:moswordsvc640 (service chỉ được đọc); envroot600. Không thay UID từ tar một cách mù quáng; dùng tên user của VPS mới.
4. Sau restore env chứa DB passwords cũ, chạy lại01provision để đồng bộ role/password/privileges mà không sinh secret mới. Dừng timer/app trở lại trước restore DB. Với DB rỗng đã provision: `sudo -u postgres pg_restore --exit-on-error --dbname=mosword` nhận stdin từ database.dump (root mở file như mục8). Nếu DB đã có dữ liệu, **dừng** và dùng restore rehearsal/maintenance riêng; không dùng `--clean` mù quáng trên production.
5. Publish đúng binary/version tương thích schema, giữ signing key/DP/per-package keys/env khớp backup. **Dựng lại site nginx trước verify:** chạy02 với IP đích và allow-list mong muốn; nếu IP không đổi, không truyền --rotate để giữ cert/pin; nếu IP mới, truyền --rotate. Backup state giữ TLS nhưng không giữ site/proxy nginx hoặc allow-list. Lấy08public-values và build client mới nếu URL/pin đổi. Health/28package/admin/results và login thử phải đúng dữ liệu cũ; bật service/timer, chạy06backup rồi99verify.
6. Giữ VPS cũ dừng/maintenance cho đến xác nhận dữ liệu đầy đủ; không xóa nó ngay. Nếu nhà cung cấp chuyển giữ nguyên IPv4 và TLS key, client không cần đổi trust; IP mới cần certificate SAN/URL/pin mới và client mới như bước5.

Lệnh restore mẫu sau đây **chỉ cho VPS mới đã provision, DB mosword chưa có bảng**, sau khi giải mã backup do chính bạn tạo thành `/var/backups/mosword/recovered.tar.gz`. Không chạy trên VPS đang phục vụ learner:

```bash
sudo -i
systemctl stop mosword.service mosword-backup.timer
TABLES=$(runuser -u postgres -- psql -X -Atd mosword -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';")
if [ "$TABLES" != 0 ]; then printf 'DB không rỗng; dừng để tránh ghi đè.\n'; exit 1; fi
RESTORE_DIR=$(mktemp -d /var/backups/mosword/restore-production.XXXXXXXX)
tar -xzf /var/backups/mosword/recovered.tar.gz -C "$RESTORE_DIR"
tar -tzf "$RESTORE_DIR/state.tar.gz"
# Chỉ tiếp tục sau khi kiểm đúng các path state/key/env/TLS của chính bạn.
tar -xzf "$RESTORE_DIR/state.tar.gz" -C /
chown -R --no-dereference moswordsvc:moswordsvc /var/lib/mosword/packages /var/lib/mosword/dataprotection
chown root:moswordsvc /var/lib/mosword/keys /var/lib/mosword/keys/manifest-signing.key
chmod 750 /var/lib/mosword/keys
chmod 640 /var/lib/mosword/keys/manifest-signing.key
chown root:root /etc/mosword/mosword.env /etc/mosword/migrate.env
chmod 600 /etc/mosword/mosword.env /etc/mosword/migrate.env
/usr/local/lib/mosword-deploy/01-provision.sh
systemctl stop mosword.service mosword-backup.timer
runuser -u postgres -- pg_restore --exit-on-error --dbname=mosword < "$RESTORE_DIR/database.dump"
runuser -u postgres -- psql -X -d mosword -c 'SELECT COUNT(DISTINCT "Code") FROM "Packages";'
exit
```

Sau đó làm bước5–6: publish binary tương thích, chạy02 để dựng nginx (rotate nếu IP đổi), bật lại `sudo systemctl enable --now mosword-backup.timer`, chạy06/99 và kiểm admin/result/learner. Giữ thư mục restore root-only cho đến nghiệm thu xong; chỉ dọn đúng thư mục `mktemp` sau khi xác minh đường dẫn như mục8. Không xóa backup gốc.
Rollback binary qua installer không tự rollback DB. Muốn trở lại schema cũ sau migration phá vỡ thì phục hồi **cả DB và state cùng backup** trong maintenance, chấp nhận mất các ghi phát sinh sau backup; phải được owner quyết định, không có lệnh tự động phá dữ liệu.

## 10. Cập nhật và rotate certificate

Cập nhật server thường: backup offsite gần nhất, review migration, chạy lại `publish.ps1` từ PC; xem exit code/health/journal. Không build trên VPS, không đổi env/key ring, không tự tạo lại DB. Nếu sửa chính bộ deploy, upload lại bộ tools và chạy01 theo mục1–2 để cài root-owned scripts mới trước publish.

Rotate có chủ ý **VPS**:

```bash
sudo /usr/local/lib/mosword-deploy/02-certificate.sh --ip "$VPS_IP" --rotate
sudo /usr/local/lib/mosword-deploy/08-public-values.sh
```

Private key mới làm pin thay đổi. Client cũ sẽ bị từ chối đúng thiết kế, nên làm maintenance và phát hành client mới với pin mới. Không có runtime override/bỏ TLS validation để chữa tạm. Mất signing key cũng cần public key/client mới; phục hồi bản backup đúng khóa sẽ tránh thay trust. Data Protection key ring mất làm cookie/token cũ mất hiệu lực, không tự hoàn lượt.

## 11. Lỗi thường gặp và xác nhận cuối

| Dấu hiệu | Cách kiểm/xử lý |
|---|---|
| SSH/host key lỗi | Giữ phiên cũ, đối chiếu provider console/fingerprint, kiểm authorized_keys600/.ssh700/sudo; không tắt host checking |
| apt không có runtime10 | Kiểm đúng Ubuntu24.04x64, `sudo apt-get update`, kho noble-updates/security; không đổi sang.NET8 hoặc SDK trên VPS |
| UFW rule khác | `sudo ufw show added`; xác minh trước khi xử lý bằng tay, script không xóa hộ |
|502 hoặc readiness lỗi| `systemctl status mosword`, `journalctl -u mosword -n100`; kiểm DB/key/env permissions. Installer rollback binary, giữ log và backup |
| Certificate mismatch | Đối chiếu08public-values với public-trust.json; saiIP/SAN/pin thì build lại đúng, không sửa App.config Release |
| Manifest signature sai | Public key Release phải khớp key trên VPS; không tự sửa manifest/dev public key để bỏ chữ ký |
| Admin403 | Kiểm allow-list và IP Internet hiện tại, sửa qua SSH bằng02 |
| Disk gần đầy | `df -h`, kiểm backup/package growth/journal; tải backup offsite và mở rộng VPS trước khi hết đĩa; không xóa key/package version được DB tham chiếu |
| Backup fail/thiếu mới | `journalctl -u mosword-backup.service`, kiểm dung lượng/PG; chạy06 thủ công và99verify; xác nhận app đã running lại |

`99-verify.sh` kiểm service, health, self-signed CA+hostname+SPKI bằng curl, cổng listen, timer/backup mới và disk<90%. Kiểm thêm từ PC chỉ22/443 truy cập được; container test không chứng minh firewall, systemd sandbox hoặc SSH thật. C7 sẽ review và nghiệm thu đầy đủ. Tài liệu gốc: [Microsoft .NET/Ubuntu](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install?pivots=os-linux-ubuntu-2404&tabs=dotnet10), [systemd hardening](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html), [nginx TLS](https://nginx.org/en/docs/http/ngx_http_ssl_module.html), [PostgreSQL16 pg_dump](https://www.postgresql.org/docs/16/app-pgdump.html).
