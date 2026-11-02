# Chạy server C0–C3 trên máy phát triển

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

Development certificate là cơ chế local của SDK, không dùng cho VPS hoặc bỏ TLS validation. SPKI pin thuộc C5/C6. Ngoài Development yêu cầu DataProtection__KeyPath (kể cả CLI mặc định Production); đường dẫn phải tuyệt đối và ngoài repo. Development được dùng key ring profile mặc định, nhưng nên giữ cùng path để token/cookie không mất khi đổi profile. Không xóa key ring giữa các deploy. Trên Linux C6 dùng thư mục riêng, owner service và mode700; bảo vệ/backup vì key filesystem không tự mã hóa at rest. Key ring không phải khóa package/TLS.

Đặc tả JSON/status API nằm trong docs/SERVER_ARCHITECTURE.md. Login learner cần idempotencyKey UUID mới cho mỗi lượt và giữ nguyên key/machineId khi retry mạng. Admin credentials luôn bị learner API từ chối. Sai password/unknown/temporary lockout cùng thông báo; sau10lỗi credential thử lại sau15phút. Không ghi response bearer/resume token vào log; C5 mới lưu resume bằng DPAPI. C3 đã có endpoint heartbeat và monitor thư viện; C5 mới nối auth/UI. Cửa sổ resume tính từ heartbeat/login/chọn mode/resume gần nhất theo server; temporary lockout chặn login mới/resume nhưng không cắt phiên active/end/upload.

Production sau nginx cùng host chỉ tin peer127.0.0.1/::1 và một hop; X-Forwarded-For/Proto từ remote khác bị bỏ qua. C6 phải bind Kestrel loopback và nginx ghi đè forwarded headers. Không dùng ASPNETCORE_FORWARDEDHEADERS_ENABLED để mở tin cậy mọi proxy.

## Package C2: cấu hình và import

Cấu hình package độc lập với Data Protection và TLS. Giữ đường dẫn tuyệt đối ngoài repo; không đặt storage/key dưới thư mục source/output hoặc symlink/reparse. CLI mặc định Production vẫn cần ConnectionStrings__Database và DataProtection__KeyPath từ bước3.

```powershell
$packageHome = Join-Path $env:LOCALAPPDATA 'MosWord2019.Server'
$packageSecrets = Join-Path $packageHome 'secrets'
$env:Packages__StorageRoot = Join-Path $packageHome 'packages'
$env:Packages__SigningKeyPath = Join-Path $packageSecrets 'manifest-signing.key'
New-Item -ItemType Directory -Path $packageSecrets -Force | Out-Null
icacls $packageSecrets /inheritance:r /grant:r "$($env:USERDOMAIN)\$($env:USERNAME):(OI)(CI)F"
if (-not (Test-Path -LiteralPath $env:Packages__SigningKeyPath)) {
    $signingSeed = New-Object byte[] 32
    $packageRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $packageRng.GetBytes($signingSeed)
        [System.IO.File]::WriteAllBytes($env:Packages__SigningKeyPath, $signingSeed)
    }
    finally {
        [Array]::Clear($signingSeed, 0, $signingSeed.Length)
        $packageRng.Dispose()
    }
}
dotnet run --project server/MosWord2019.Server --no-launch-profile -- package-public-key
dotnet run --project server/MosWord2019.Server --no-launch-profile -- import-packages MosWord2019.WinForms/Projects
```

Seed là đúng32byte ngẫu nhiên thô, không phải chuỗi hex/Base64/PEM. Đoạn trên chỉ tạo khi thiếu, không ghi đè key hiện có. CLI package-public-key chỉ in public key Base64 và trạng thái hoàn tất; không in seed. Key private hiện tại trên PC ở `%LOCALAPPDATA%/MosWord2019.Server/secrets/manifest-signing.key`; giữ và backup ngoài repo cùng storage, DB và DP key ring. Không tự sinh key mới trên máy deploy nếu phải giữ trust của client đang phát hành.

Trước dùng package đã ký, đối chiếu dòng public key CLI với `server/manifest-verification.pub` và hằng `PackageTrust.PublicKey` trong `MosWord2019.Projects/OnlinePackages/PackageTrust.cs`; cả hai phải khớp. Chỉ public key được commit/biên dịch. Nếu chủ động đổi private key thì cập nhật public key và build/phát hành lại client tương ứng; không thay trust bằng dữ liệu manifest hoặc cấu hình runtime. Key tự tạo khác key đang biên dịch sẽ bị client từ chối đúng thiết kế.

Importer đọc source Projects, không chỉnh starter/assets/questions. Cần toàn bộ catalog/pool hợp lệ; không import từng thư mục project thiếu testing-catalog.json. Lần đầu tạo ZIP ổn định, version=SHA256 ZIP, blob AES256-GCM/key riêng và hàng Packages. Import lặp cùng ZIP không đổi blob/key nhưng cập nhật catalog policy cho phiên mới; đề Testing đang hoạt động dùng snapshot bất biến. Transaction advisory lock tuần tự hóa import và dọn orphan đúng mẫu vào lần import tiếp theo; giữ các version còn DB tham chiếu. Không tự xóa blob/key bằng tên project hoặc dọn toàn storage khi app đang chạy.

Trên Linux C6, chọn thư mục ví dụ `/var/lib/mosword/packages`, `/var/lib/mosword/secrets/manifest-signing.key` và `/var/lib/mosword/keys`, owner là service account; thư mục700, file private/key/blob600. Mã import tạo mode tương ứng cho thư mục/file mới; quản trị viên phải kiểm quyền các path đã tồn tại. Copy private seed hiện có qua kênh an toàn, không đưa vào Git, command-line argument, artifact publish hoặc log. Đây là hợp đồng đường dẫn/quyền, chưa phải triển khai VPS hoặc cấu hình nginx/TLS.

## Kiểm tra luồng package và giới hạn phiên

Login learner rồi chọn mode qua API hiện có. Chọn Testing tạo seed/hash/snapshot, trả examSeed/definitionHash/uploadToken; khi pool không đủ trả503 và không chốt mode. GET `/api/packages/manifest` bằng access token active trả envelope payload/signature ký Ed25519. Payload schemaVersion2 chứa ExamDefinition đã compose tại server, metadata/task được chọn và đúng7descriptor/version/key; không phát catalog/toàn pool hoặc task/project ngoài đề. Client xác minh chữ ký toàn payload, session/mode, cấu trúc và hash đề rồi dùng trực tiếp; POST `/api/sessions/definition` với definitionHash phải được chấp nhận trước GET `/api/packages/{id}/download`. Testing không tải id ngoài snapshot; download/report có audit. Không ghi manifest chứa key hoặc bearer token ra log.

Pending hết hạn tại15phút từ StartedAt; Training4giờ từ StartedAt; Testing2giờ từ ModeSelectedAt. Phiên còn phải liên hệ trong15phút gần nhất và account/version hợp lệ. Quá idle15phút bị Abandoned vĩnh viễn; login retry cùng key không hồi sinh phiên. Heartbeat C3 update LastHeartbeatAt sau khi được chấp nhận; API package không tự thay heartbeat. C5 vẫn cần nối monitor vào vòng đời online trước nghiệm thu bài dài. Server quan sát inactivity sẽ lưu EndedAt/EndReason và đúng một audit, kể cả phản hồi401; giữ commit nhánh denied nếu viết caller mới của FindAsync.

Access token30phút, resume255phút; dùng token mới từ response phiên hoặc header Session-Token của API package. Identity hint trong token hết hạn chỉ cho phép quan sát/chốt phiên vốn đã inactive, không cấp quyền. End retry chỉ là receipt UserEnded và trả sessionToken mới, không mở lại phiên. Upload token purpose riêng1575phút là nền tảng C4, chưa có endpoint upload; policy yêu cầu finalizedAt không sau2giờ từ chọn Testing và không sau EndedAt, nhận tới min(EndedAt,ModeSelectedAt+2giờ)+24giờ inclusive. End quan sát muộn không kéo dài quyền upload.

## Client C2 và build phát triển

`OnlinePackageCache`/`HttpPackageTransport` là thư viện chuẩn bị cho C5, chưa được nối vào màn hình login/Training/Testing online. Transport chỉ nhận HTTPS origin, từ chối redirect và giữ kiểm TLS chuẩn; không dùng callback bỏ xác minh certificate. SPKI pin trong client thuộc C5, script certificate/pin thuộc C6. Cache giải mã dưới `%LOCALAPPDATA%/MosWord2019/Packages/<session>/<instance>` với ACL SID người dùng, kiểm signature/hash/GCM/AAD và ZIP trước mở project.

PackageCacheLedger ghi native identity trước byte plaintext đầu tiên, hash khi ghi xong và nối journal recovery; startup Program gọi RecoverIncompletePackages trước recovery chung. Dispose/end và startup sau crash dọn file còn đúng identity của cache, kể cả khi nội dung đã sửa; không xóa file đã thay bằng identity khác, symlink/reparse hoặc file người dùng ngoài claim. Giữ lease khi session còn sống; không dùng lệnh xóa đệ quy cache thay cơ chế này.

Debug mặc định UseLocalPackages=true; Release mặc định false, không ship Projects. Build Release dọn file package nguồn cũ trong output và fail nếu còn file Projects stale; dùng output sạch cho phát hành. Để chạy thủ công local bằng cấu hình Release trong giai đoạn chưa nối C5, bật dev switch rõ ràng bằng VS MSBuild:

```powershell
MSBuild.exe MosWord2019.slnx /t:Rebuild /p:Configuration=Release /p:UseLocalPackages=true
```

Switch này dành cho phát triển, không phải cấu hình distribution. Release mặc định hiện chưa dùng độc lập cho người học vì online UI/auth/session refresh và kết nối monitor heartbeat C3 còn chờ C5; không kết luận bản production đã sẵn sàng chỉ vì build không có Projects. Grader, nội dung bài và engine hiện có vẫn giữ nguyên.

## Heartbeat C3: kiểm API và monitor

POST heartbeat chỉ dùng Bearer access token, không gửi username/password/resume token/key package. Chuẩn bị $sessionId và $sessionToken trong bộ nhớ từ response login/mode đã xác thực, không dùng giá trị ví dụ trên tài khoản production. Đoạn sau chạy một request, chỉ hiển thị field không bí mật và giữ access token mới trong biến:

```powershell
$heartbeatUri = "https://localhost:7118/api/sessions/$sessionId/heartbeat"
$heartbeatHeaders = @{ Authorization = "Bearer $sessionToken" }
$heartbeatBody = @{ clientElapsedMilliseconds = $null; wasOffline = $false } | ConvertTo-Json -Compress
$heartbeatReply = Invoke-RestMethod -Uri $heartbeatUri -Method Post -Headers $heartbeatHeaders -ContentType 'application/json' -Body $heartbeatBody
$sessionToken = $heartbeatReply.sessionToken
$heartbeatReply | Select-Object sessionId, mode, serverTime, expiresAt, remainingSeconds
Remove-Variable heartbeatHeaders, heartbeatBody, heartbeatReply -ErrorAction SilentlyContinue
```

Dùng certificate development đã trust ở bước5; không thêm SkipCertificateCheck, curl -k hoặc callback bỏ TLS. Không in toàn response vì chứa sessionToken, không dùng verbose/trace/transcript ghi headers. Với Testing, clientElapsedMilliseconds là active elapsed từ engine hoặc null khi chưa chạy; hợp lệ0..86400000. WasOffline phản ánh client đã quan sát offline sau reply hợp lệ trước đó, không phải giá trị luôn true; request đầu phải false. Endpoint trả400 với elapsed sai,409 khi chưa chọn mode,401 khi phiên sai/hết quyền và429 khi vượt giới hạn. Heartbeat có bucket riêng6/phút theo SessionId kể cả token đã refresh; API chung60/session/phút vẫn giữ. Một script kiểm thủ công không thay monitor hay bằng chứng nghiệm thu UI.

Reply có serverTime/expiresAt/remainingSeconds để chiếu deadline server: Training StartedAt+4giờ, Testing ModeSelectedAt+2giờ. Heartbeat không gia hạn deadline; Training vẫn trôi khi mất mạng. Temporary lockout chỉ chặn password login mới; resume hợp lệ không bị chặn bởi lockout tạm; heartbeat/end/active package/upload hẹp không bị temporary lockout cắt, nhưng admin Locked/credential reset vẫn thu hồi quyền.

SessionHeartbeats lưu từng heartbeat và gap server vào DB. Khoảng lastHeartbeat+90giây tới now là Unobserved nếu không có quan sát offline, hoặc Network khi client WasOffline phù hợp. Nếu request vẫn tới nhưng response bị mất, server dùng last heartbeat WasOffline=false cộng30giây để ước lượng/extend Network gap, không suy online chỉ từ việc nhận POST. Chỉ Network gap được trừ khi đối chiếu Testing active elapsed; crash/Unobserved vẫn tính. Heartbeat Testing đầu có elapsed tạo anchor server và baseline để không tính Rules wait; elapsed đi trước mode wall hơn90giây đặt flag; các lần sau dùng tolerance nền90giây cộng90giây mỗi episode Network riêng biệt sau hợp/clip. Gap start=end vẫn được ghi để giữ episode suy luận, tránh false flag khi nhiều lần mất response ngắn trung thực; freeze vượt tổng bất định mới đặt TimingInconsistent sticky/audit một lần. Đây là tín hiệu tham khảo, không chứng minh gian lận. WasOffline không tin cậy và không cấp quyền; deadline4giờ/2giờ không trừ Network gap. C4 đọc cờ theo SessionId và gọi SessionTiming.IsInconsistent tại upload; xem contract kết quả bên dưới.

SessionConnectivityMonitor gửi mỗi30giây, watchdog1giây, timeout10giây, không overlap; bỏ reply muộn/cancel/dispose. Ban đầu offline, sau đó offline khi3lỗi liên tiếp hoặc90giây từ reply thành công cuối. Chiếu thời gian server bằng monotonic clock, tolerance drift2giây cho NTP slew nhỏ và clamp remaining không tăng, nhưng UTC server thực sự lùi vẫn bị từ chối; không căn đồng hồ vào UtcNow client. TrainingRemaining là countdown4giờ liên tục; TestingRemaining là server hard cap2giờ, không thay active timer50phút/pause hiện có. Notification FIFO chạy ngoài state lock, drainer không chờ để tránh khóa chéo với Testing engine; event có thể ở caller/background thread nên C5 cần marshal về UI. C3 chưa nối auth/UI/Word lock, nên các luồng offline đang chạy giữ nguyên.

Manifest C3 schema2 chỉ đưa đề server đã compose và metadata/task của7project/task được chọn; client không nhận catalog/toàn pool để tự compose. Snapshot server giữ pinned pool và Definition nội bộ; snapshot C2 cũ thiếu Definition được server compose một lần rồi persist. Download dùng OpenBlob kiểm hash bằng stream có giới hạn, reset Position0, commit audit rồi trả FileStream để framework gửi/dispose; không load toàn blob vào RAM mỗi request.

## 6. Build và test

### Kết quả và retention C4

Chạy `migrate` sau cập nhật C4 như bước database ở trên: migration thứ6 ResultUploads thêm derived session HMAC key và các field kết quả. Không chỉnh migration C0–C3. Key được cấp trong reply login/mode/resume, giữ trong bộ nhớ hoặc DPAPI client, không in toàn response/header và không bật trace/HTTP-body logging. Private package keys và DP ring vẫn ngoài repo; DataProtection:KeyPath bắt buộc ngoài Development và phải backup cho C6.

POST /api/results: Content-Type application/json, Bearer access hoặc uploadToken, X-Result-Signature là Base64 HMAC-SHA256 trên đúng byte UTF8 file B4 bằng key đã Base64-decode, tối đa256KiB. B4 phải chứa server SessionId và definition/seed/task đã nhận từ signed manifest. Retry dùng cùng buffer byte, không parse/serialize lại. Receipt200 gồm sessionId/payloadHash/score/passed/flags/serverTime; xác minh SHA256 buffer/session và verdict trước xác nhận. Cùng byte200 lại; payload khác409/audit. Chỉ access còn active nhận sessionToken mới; uploadToken không thể cấp access hoặc gọi heartbeat/package.

Server tính lại điểm từ pinned definition/outcome bằng scorer hiện có, không tin total client; task sai hiển thị instruction từ đúng ZIP package đã import. Giữ package blob/key/version cũ khi session còn tham chiếu. Nếu thiếu/hỏng pinned language/key/blob,503 giữ queue; phục hồi storage từ backup rồi retry. Admin login → Kết quả hoặc liên kết tài khoản để xem danh sách/chi tiết; lọc ngày dựa ReceivedAt UTC. Flags timing/rollback là dữ liệu kiểm tra, không tự phạt điểm.

Upload sau khi kết thúc chỉ Testing, finalizedAt trong bounds server và tối đa24h sau min(end,hardcap). Hết hạn/admin lock/reset credential vẫn401; giữ bản local và queue để owner xử lý, không gia hạn bằng clock client. Temporary failed-password lockout không chặn resume possession token hợp lệ/cùng máy; vẫn chặn login password mới. Worker dọn Heartbeats cũ hơn90ngày lúc startup/mỗi24h; không xóa Sessions/OfflineGaps/Results/AuditLog. Không chạy cleanup script xóa các bảng được giữ.

Client ResultUploadQueue.Enqueue nhận đường dẫn JSON B4 đã persist, serverSessionId, HTTPS origin, key32byte và uploadToken; queue bảo vệ DPAPI CurrentUser+ACL ngoài repo. Program retry entry đăng ký qua restart; backoff5s→1h/timeout15s. JSON B4 gốc giữ pendingUpload=true và không xóa/sửa; dùng GetStatus.PendingUpload/receipt riêng để biết server đã nhận. Test fake transport và DPAPI bao phủ mất ACK/crash/restart, receipt sai,409, backoff, cancellation, enqueue đồng thời. C5 mới nối binding/sessionId/key/Enqueue với finalize online; offline hiện tại không upload. Không dùng certificate bypass để thử self-signed; pin sẽ được tích hợp C5/C6.

```powershell
dotnet build server/MosWord2019.Server.sln -c Debug --no-restore -warnaserror
dotnet test server/MosWord2019.Server.sln -c Debug --no-restore
dotnet build server/MosWord2019.Server.sln -c Release --no-restore -warnaserror
dotnet test server/MosWord2019.Server.sln -c Release --no-restore
```

Testcontainers dùng postgres:16-alpine, database riêng/test, password ngẫu nhiên và tự dọn container. Các connection test không pooling để không giữ pool idle cho hàng trăm database; production không bị đổi chính sách pool. Không đọc DB dev hoặc state Word. Docker lỗi thì FAIL, không skip. Suite kiểm nền tảng C0–C4: migration/CLI/health, concurrency login/key/maxuses, deadline/idle/end/audit/persistence, lockout/thời gian server, admin/CSRF/proxy/rate limit, durable DP tokens, import/crypto/manifest/download/report/snapshot và nâng cấp dữ liệu; C3 có persistence heartbeat/gaps, rate limit theo session, lockout không cắt phiên active và timing flags. Client có kiểm chữ ký/GCM/hash/tampering, đề schema2 tối thiểu, compose parity trong chẩn đoán/tests, extraction/ACL/ownership/cleanup, monitor/watchdog/monotonic/countdown và regression hiện có.

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
Remove-Item Env:Packages__StorageRoot -ErrorAction SilentlyContinue
Remove-Item Env:Packages__SigningKeyPath -ErrorAction SilentlyContinue
```

Chưa public app C4 ra VPS. C6 sẽ publish framework-dependent linux-x64 trên PC, copy scp/rsync, không build trên VPS; thêm nginx/firewall/TLS pin/systemd/backup/rotation, tuning4GB/swap2GB và hướng dẫn chuyển VPS bằng pg_dump/restore. Key xác minh manifest công khai có thể commit đuôi .pub, private keys vẫn để ngoài repo.
