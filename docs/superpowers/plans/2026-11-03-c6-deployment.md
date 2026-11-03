# Kế hoạch C6 — Triển khai VPS

Phạm vi được owner phê duyệt: Ubuntu24.04, VPS2CPU/4GB/35GB, không truy cập VPS từ agent, không commit IP/bí mật, chỉ publish trên PC. Giữ client472/C#7.3/Word và grader/nội dung; server10/PostgreSQL16.

1. `server/deploy/01-provision.sh`, `02-certificate.sh`, nginx/systemd/journald/fail2ban/security-updates: cài runtime, localhost DB, runtime CRUD/migrator riêng, swap/tuning, private paths, SSH xác nhận và TLS P25610năm. Không thay khóa khi chạy lại; SSH phải xác minh authorized_keys và xác nhận tường minh.
2. `common.sh`, `03-install-release.sh`: đọc env root600, CLI service-user, khóa operation, backup trước migrate, archive/SHA/path validation, release root-owned, atomic symlink, health/rollback và giữ3release. Schema không tự downgrade; migration phải backward-compatible.
3. `04-import-packages.sh`, `05-create-admin.sh`: importer có sẵn, count28, chỉ dọn plaintext upload được chứng minh; password đọc ẩn/env tạm. Không bí mật trong argv/log.
4. `06-backup.sh`, timer03:15UTC, `07-export-backup.sh`, `99-verify.sh`: backup DB và state/key nhất quán,14daily/8weekly, export mã hóa tải tuần, health/pin/ports/disk.
5. `publish.ps1`, `build-client.ps1`: PC publish framework-dependent linux-x64/SCP/SSH installer; build Release với URL/SPKI/Ed25519 public key đã kiểm. Public trust sinh trong obj/output, không đổi source hoặc cấu hình cá nhân. Release không nhận override runtime.
6. Test: ShellCheck/bash syntax, PowerShell parse/negative parameters, Ubuntu24.04 container nếu Docker hoạt động. Provision chạy hai lần và giữ hash bí mật; certificate SAN/pin/rotation; PostgreSQL quyền runtime; release health/rollback/archive attacks; backup/restore/import/admin khi khả thi. Container không thay nghiệm thu systemd/firewall/SSH thật.
7. VS MSBuild Debug/Release0warnings, client1049case baseline; dotnet build/test0warnings server203case PostgreSQL thật. Kiểm public trust build riêng, wrong-certificate rejection; package/hash và protected sources không đổi.
8. `docs/DEPLOY_VPS.md`, architecture/dev và4context: lệnh PC/VPS, expected outputs, backup/restore thử, migration VPS/rotation và giới hạn test. Review diff/secrets/status, commit cục bộ; không push, dừng C6.
