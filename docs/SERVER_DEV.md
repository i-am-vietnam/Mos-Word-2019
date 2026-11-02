# Chạy server C0 trên máy phát triển

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

Mở [admin local](https://localhost:7118/Admin), đăng nhập bằng admin vừa tạo. Trang C0 chỉ xác nhận đăng nhập và logout; danh sách/tạo/cấp lượt tài khoản ở C1. Kiểm tra [liveness](https://localhost:7118/health) và [readiness](https://localhost:7118/health/ready). Cả hai trả `Healthy` khi sẵn sàng; readiness thiếu DB/migration trả 503 `Unhealthy`. HTTP 5273 chuyển sang HTTPS 7118; cookie admin/antiforgery chỉ dùng HTTPS.

Development certificate là cơ chế local của SDK. Không dùng nó cho VPS và không thêm tùy chọn bỏ qua TLS validation. Certificate self-signed/SPKI pin cho client thật thuộc C5/C6. Key ring Data Protection mặc định do ASP.NET Core lưu ngoài repo theo profile hệ điều hành. Có thể đặt `DataProtection__KeyPath` tới thư mục ngoài repo chỉ tài khoản chạy server đọc/ghi; Linux cần bảo vệ bằng quyền thư mục và cơ chế backup phù hợp. Key ring không phải khóa package/TLS.

## 6. Build và test

```powershell
dotnet build server/MosWord2019.Server.sln -c Debug --no-restore -warnaserror
dotnet test server/MosWord2019.Server.sln -c Debug --no-restore
dotnet build server/MosWord2019.Server.sln -c Release --no-restore -warnaserror
dotnet test server/MosWord2019.Server.sln -c Release --no-restore
```

Testcontainers tự tải `postgres:16-alpine`, tạo database riêng từng test, dùng password ngẫu nhiên lúc chạy và tự xóa container khi fixture kết thúc. Không đọc database dev, không đụng Word hoặc state client. Docker không chạy thì suite FAIL, không skip. Suite kiểm schema Up/Down/reapply, persistence/FK/unique/check/JSONB/UTC, bootstrap đồng thời và CLI, password hash, cookie/auth/CSRF/rate limit, readiness trước/sau migrate.

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

Không public app C0 ra VPS. Hướng dẫn nginx, firewall, TLS pin, systemd, backup và triển khai Ubuntu thuộc C6.
