# Chạy server C0–C1 trên máy phát triển

Tất cả lệnh dưới đây chạy từ thư mục gốc repository Word, trừ đoạn ghi rõ `Set-Location server`. Cần .NET 10 SDK và Docker Desktop **Linux containers** đang chạy. Client vẫn build bằng VS MSBuild/.NET Framework 4.7.2; không thêm server vào solution client.

## 1. Kiểm tra điều kiện

```powershell
dotnet --list-sdks
docker info
```

Phải có SDK 10.x và Docker engine phản hồi. Nếu có PostgreSQL 16+ local thì có thể dùng cho chạy app; suite tự động vẫn dùng Testcontainers, bắt buộc Docker. Không thay bằng EF InMemory/SQLite và không bỏ qua integration tests khi Docker lỗi.

## 2. PostgreSQL phát triển

Nếu đã có local PostgreSQL, tạo database/user riêng cho MOS Word rồi chuyển bước 3. Hoặc dùng container sau; cổng 5432 chỉ bind loopback, không public ra LAN:

```powershell
$securePgPassword = Read-Host 'Dat mat khau PostgreSQL dev' -AsSecureString
$env:POSTGRES_PASSWORD = [System.Net.NetworkCredential]::new('', $securePgPassword).Password
docker run --name mosword-dev-db -d -e POSTGRES_USER=mosword -e POSTGRES_DB=mosword -e POSTGRES_PASSWORD -p 127.0.0.1:5432:5432 -v mosword-dev-pgdata:/var/lib/postgresql/data postgres:16-alpine
docker exec mosword-dev-db pg_isready -U mosword -d mosword
```

Đợi `accepting connections` trước khi migrate. Mật khẩu nhập thật không ghi vào script/repo. Container dev giữ password trong cấu hình Docker; không chia sẻ kết quả inspect. Volume có dữ liệu lâu dài, không dùng cho suite test. Lần sau khởi động lại bằng `docker start mosword-dev-db`; không tạo lại hoặc xóa volume khi còn dữ liệu cần giữ.

## 3. Cấu hình, restore và migrate

Connection string là bí mật. Tạo bằng builder để mật khẩu có dấu chấm phẩy không làm hỏng cú pháp. Trong PowerShell có thể dùng builder chuẩn .NET:

```powershell
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Host'] = '127.0.0.1'
$connection['Port'] = 5432
$connection['Database'] = 'mosword'
$connection['Username'] = 'mosword'
$connection['Password'] = $env:POSTGRES_PASSWORD
$env:ConnectionStrings__Database = $connection.ConnectionString
$env:DataProtection__KeyPath = Join-Path $env:LOCALAPPDATA 'MosWord2019.Server/keys'
Remove-Item Env:POSTGRES_PASSWORD
dotnet restore server/MosWord2019.Server.sln --locked-mode
dotnet run --project server/MosWord2019.Server --no-launch-profile -- migrate
```

Nếu dùng PostgreSQL local, nhập mật khẩu vào biến môi trường như bước 2 và đổi host/port/database/user cho đúng. Không in biến connection string lên terminal. Sau khi mở PowerShell mới phải cung cấp lại biến hoặc đọc từ tệp riêng ngoài repo có ACL hạn chế. Không ghi password vào appsettings hoặc launchSettings.

Ứng dụng web không tự migrate khi startup. Lệnh `migrate` dùng cùng migration EF đã commit, có thể chạy lại an toàn. Tài khoản DB cần quyền DDL cho migrate; triển khai tách quyền runtime/migration sẽ hướng dẫn ở C6.

## 4. Tạo admin đầu tiên

```powershell
$secureAdminPassword = Read-Host 'Mat khau admin (12-128 ky tu)' -AsSecureString
$env:MOSWORD_ADMIN_PASSWORD = [System.Net.NetworkCredential]::new('', $secureAdminPassword).Password
dotnet run --project server/MosWord2019.Server --no-launch-profile -- create-admin owner
Remove-Item Env:MOSWORD_ADMIN_PASSWORD
```

Username dài 3–64 ký tự ASCII chữ/số/`._-`. Lệnh chỉ tạo admin đầu tiên, trả exit code 1 nếu thiếu/mật khẩu yếu, trùng username hoặc đã có admin; không reset password của tài khoản cũ. Không đặt mật khẩu vào command-line argument. Tạo admin không cấp lượt học viên. Bảo vệ quyền chạy CLI và DB như quyền quản trị.

## 5. HTTPS và Razor Pages local

```powershell
dotnet dev-certs https --trust
dotnet run --project server/MosWord2019.Server --launch-profile https
```

Mở [admin local](https://localhost:7118/Admin), đăng nhập bằng admin vừa tạo và vào Accounts: tìm kiếm, Create, Edit để đặt MaxUses hoặc AddUses, expiryUTC, lock/unlock/reset password; Sessions xem lịch sử phiên. Khi cộng lượt, để trống ô đặt MaxUses tuyệt đối để cộng trên giá trị mới nhất từ DB. Để trống expiry để bỏ hạn; để trống new password để giữ mật khẩu. Không tạo/admin-promote qua trang học viên. Kiểm tra [liveness](https://localhost:7118/health) và [readiness](https://localhost:7118/health/ready). HTTP5273 chuyển sang HTTPS7118; cookie admin/antiforgery chỉ HTTPS.

Development certificate là cơ chế local của SDK, không dùng cho VPS hoặc bỏ TLS validation. SPKI pin thuộc C5/C6. C1 yêu cầu DataProtection__KeyPath ngoài Development (kể cả CLI mặc định Production); đường dẫn phải tuyệt đối và ngoài repo. Development được dùng key ring profile mặc định, nhưng nên giữ cùng path để token/cookie không mất khi đổi profile. Không xóa key ring giữa các deploy. Trên Linux C6 dùng thư mục riêng, owner service và mode700; bảo vệ/backup vì key filesystem không tự mã hóa at rest. Key ring không phải khóa package/TLS.

Đặc tả JSON/status API nằm trong docs/SERVER_ARCHITECTURE.md. Login learner cần idempotencyKey UUID mới cho mỗi lượt và giữ nguyên key/machineId khi retry mạng. Admin credentials luôn bị learner API từ chối. Sai password/unknown/temporary lockout cùng thông báo; sau10lỗi credential thử lại sau15phút. Không ghi response bearer/resume token vào log; C5 mới lưu resume bằng DPAPI. C1 chưa có heartbeat: cửa sổ resume tính từ server login/chọn mode/resume gần nhất; chạy bài dài cần C3/C5 trước nghiệm thu online.

Production sau nginx cùng host chỉ tin peer127.0.0.1/::1 và một hop; X-Forwarded-For/Proto từ remote khác bị bỏ qua. C6 phải bind Kestrel loopback và nginx ghi đè forwarded headers. Không dùng ASPNETCORE_FORWARDEDHEADERS_ENABLED để mở tin cậy mọi proxy.

## 6. Build và test

```powershell
dotnet build server/MosWord2019.Server.sln -c Debug --no-restore -warnaserror
dotnet test server/MosWord2019.Server.sln -c Debug --no-restore
dotnet build server/MosWord2019.Server.sln -c Release --no-restore -warnaserror
dotnet test server/MosWord2019.Server.sln -c Release --no-restore
```

Testcontainers dùng postgres:16-alpine, database riêng/test, password ngẫu nhiên và tự dọn container. Các connection test không pooling để không giữ pool idle cho hàng trăm database; production không bị đổi chính sách pool. Không đọc DB dev hoặc state Word. Docker lỗi thì FAIL, không skip. Suite kiểm C0 migration/CLI/health và C1 concurrency login/key/maxuses, mode/resume/end/revoke, lockout/thời gian server, admin forms/CSRF, trusted proxy, username/IP/session shared rate limit, durable DP tokens và nâng cấp dữ liệu C0.

Dependency NuGet ghim trong csproj và `packages.lock.json`; local tool `dotnet-ef` ghim trong `server/dotnet-tools.json`. Để tạo migration mới ở bước được cho phép:

```powershell
Set-Location server
dotnet tool restore
dotnet tool run dotnet-ef migrations add MigrationName --project MosWord2019.Server --output-dir Data/Migrations
Set-Location ..
```

Lệnh tooling cũng cần `ConnectionStrings__Database`. Review migration và chạy integration tests trước commit. Không gọi `EnsureCreated` thay migration trên database thật. Không sửa migration đã phát hành để thay đổi schema của bước sau.

## 7. Dừng môi trường dev

Ctrl+C dừng web; `docker stop mosword-dev-db` dừng DB và giữ volume. Khi không dùng nữa, xóa các biến bí mật khỏi PowerShell hiện tại:

```powershell
Remove-Item Env:ConnectionStrings__Database -ErrorAction SilentlyContinue
Remove-Item Env:MOSWORD_ADMIN_PASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue
```

Không public app C1 ra VPS. C6 sẽ publish framework-dependent linux-x64 trên PC, copy scp/rsync, không build trên VPS; thêm nginx/firewall/TLS pin/systemd/backup/rotation, tuning4GB/swap2GB và hướng dẫn chuyển VPS bằng pg_dump/restore. Key xác minh manifest công khai có thể commit đuôi .pub, private keys vẫn để ngoài repo.
