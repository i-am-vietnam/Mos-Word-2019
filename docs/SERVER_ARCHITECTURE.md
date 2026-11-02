# Kiến trúc server MOS Word 2019

## Phạm vi C0–C2

C0 tạo nền tảng server; C1 thêm tài khoản/lượt/phiên và quản trị học viên. C2 bổ sung chính sách phiên tập trung, phân phối package mã hóa, manifest ký và thư viện cache client. Chưa nối đăng nhập/giao diện online hoặc heartbeat C3; Release mặc định không mang Projects và chưa dùng độc lập cho người học trước C5. Có thể bật package local rõ ràng cho phát triển.

Client tiếp tục WinForms, .NET Framework 4.7.2, C# 7.3 và Word Interop. `server/MosWord2019.Server.sln` không nằm trong `MosWord2019.slnx`, không tham chiếu project WinForms. `server/MosWord2019.ExamComposition` biên dịch liên kết các model/composer hiện có của Core cho server, giữ cùng thuật toán đề và fingerprint. App ASP.NET Core .NET 10 chứa cả HTTP API và Razor Pages; EF Core 10 dùng Npgsql/PostgreSQL 16+. Chọn một app và một database để phù hợp VPS 2 vCPU, 4 GB RAM, 35 GB NVMe.

VPS đã chốt là NVME R2: 2 vCPU Xeon Gold, 4 GB RAM, 35 GB NVMe, 100 Mbps, một IPv4, backup nhà cung cấp hằng ngày, Ubuntu 24.04 LTS, không domain. Không build trên VPS: C6 sẽ publish framework-dependent `linux-x64` bằng `dotnet publish -c Release` trên PC rồi copy bằng scp/rsync. C6 cấu hình PostgreSQL shared_buffers512MB/effective_cache_size1536MB/work_mem8MB/max_connections50, swap2GB và pg_dump/rotation bên cạnh backup nhà cung cấp.

## Các thành phần đã có

| Tệp/thư mục | Trách nhiệm |
|---|---|
| `server/MosWord2019.Server/Program.cs` | DI, pipeline HTTPS/auth/rate limit, health và CLI quản trị/import/key công khai |
| `server/MosWord2019.Server/Data/Entities.cs`, `server/MosWord2019.Server/Data/ServerDbContext.cs` | Năm model, mapping PostgreSQL và ràng buộc |
| `server/MosWord2019.Server/Data/Migrations/` | `InitialFoundation`, `AccountsAndUses`, `SessionLifetimes`, `PackageDistribution`, snapshot và Up/Down |
| `server/MosWord2019.Server/Security/AdminBootstrap.cs` | Transaction/advisory lock để tạo đúng một admin đầu tiên |
| `server/MosWord2019.Server/Security/AdminAccess.cs` | Chuẩn hóa username, PasswordHasher, quyền admin/lock/expiry |
| `server/MosWord2019.Server/Security/LearnerSessions.cs`, `server/MosWord2019.Server/Security/LearnerEndpoints.cs` | Transaction/API login, mode, end, resume |
| `server/MosWord2019.Server/Security/SessionTokens.cs`, `server/MosWord2019.Server/Security/LoginThrottle.cs`, `server/MosWord2019.Server/Security/KeyStorage.cs` | Token khác purpose, limiter chung, key ring ngoài repo |
| `server/MosWord2019.Server/Security/AccountAdministration.cs` | Tạo/sửa học viên dưới row lock và audit |
| `server/MosWord2019.Server/Pages/Admin/` | Login, trang nền tảng được bảo vệ và POST logout |
| `server/MosWord2019.Server/Pages/Admin/Accounts/` | Tìm kiếm/phân trang, Create/Edit, xem phiên |
| `server/MosWord2019.Server/Security/SessionPolicy.cs` | Quyền active, deadline, kết thúc/audit một lần và quyền upload hẹp |
| `server/MosWord2019.Server/Packages/` | Import/storage/manifest/download/report và snapshot đề |
| `MosWord2019.Projects/OnlinePackages/` | Trust, HTTPS transport, kiểm manifest, giải mã/cache và ledger recovery |
| `server/MosWord2019.Server.Tests/` | xUnit, HTTP qua TestServer, CLI subprocess và PostgreSQL Testcontainers |

`GET /health` trả 200 khi tiến trình hoạt động, không yêu cầu DB. `GET /health/ready` trả 200 khi kết nối được DB và mọi migration đã áp dụng; 503 nếu thiếu cấu hình, mất DB hoặc còn migration. Phản hồi không chứa connection string. Health đi qua HTTPS như các endpoint khác. Không tự migrate hoặc tự tạo admin khi web khởi động.

## Dữ liệu

| Bảng | Nội dung/ràng buộc |
|---|---|
| `Accounts` | UUID, username duy nhất, password hash, cờ admin/lock, lượt và expiry; `0 <= Used <= MaxUses`; FailedLoginCount0..10, LockoutUntil, CredentialVersion |
| `Sessions` | UUID, account FK, LoginRequestId duy nhất trong account, AccountCredentialVersion, SHA-256 machine ID, mode nullable hoặc Training/Testing, mốc bắt đầu/chọn mode/heartbeat/kết thúc, EndReason, seed/hash đề và snapshot pool bất biến |
| `Results` | UUID, một result trên session FK duy nhất, payload JSONB, mốc nhận |
| `Packages` | UUID, code/version duy nhất, hash ZIP làm version, hash ciphertext, đường dẫn blob tương đối, kích thước không âm, metadata/catalog/task tối thiểu; key AES ở storage ngoài repo |
| `AuditLog` | Identity bigint, account FK nullable, action, JSONB không chứa bí mật, mốc tạo |

FK dùng Restrict để không xóa dây chuyền session/result/audit. PostgreSQL dùng `timestamp with time zone`, mặc định `CURRENT_TIMESTAMP`; dịch vụ phiên/expiry/lockout dùng TimeProvider UTC phía server. Client không gửi thời gian làm căn cứ cấp phép. Migration C1 giữ hash/lượt/expiry của account C0 và cấp CredentialVersion mới; bản ghi session C0 chưa có token không tự trở thành phiên online.

## Hợp đồng API phiên

| POST | Request | Hành vi |
|---|---|---|
| `/api/auth/login` | JSON username, password, machineId, idempotencyKey(UUID khác rỗng) | 200: sessionId, sessionToken, resumeToken, remainingUses, serverTime, mode(null ban đầu) |
| `/api/sessions/mode` | Bearer sessionToken + JSON mode: Training hoặc Testing | Chọn một lần; retry cùng mode200, đổi mode409 |
| `/api/sessions/{id}/end` | Bearer token của đúng phiên | Kết thúc, không hoàn lượt; retry200 chỉ receipt UserEnded sau kiểm identity, token phiên khác401 |
| `/api/sessions/resume` | JSON resumeToken, machineId | Cùng máy và tuổi lần liên hệ gần nhất <=15phút: 200, không tiêu lượt; sai/hết cửa sổ/đã end401 |

Login khóa row account, xác thực password/role, tìm request cũ, rồi thực hiện `UPDATE Accounts SET Used=Used+1 WHERE Used<MaxUses AND NOT Locked AND NOT expired RETURNING Used` có parameter; tạo session/audit trong cùng transaction. Unique index và row lock chống retry/concurrency trừ lặp. Request cũ cùng máy còn hiệu lực trả cùng SessionId ngay cả khi đã hết lượt; khác máy, đã kết thúc hoặc credential đã reset trả409 và không tạo lượt. Request key mới, kể cả máy khác, là lượt mới. Token ciphertext có thể khác giữa các response do mã hóa ngẫu nhiên nhưng cùng định danh phiên.

Machine ID đầu vào là SHA-256 hex64 của giá trị ổn định trên máy, client C5 sẽ tính; server chuẩn hóa lowercase, không nhận tên máy/user/hardware gốc. C2 chưa nối UI hoặc thêm machine-id collector online; phần đó thuộc C5. Access/resume dùng purpose Data Protection khác nhau; không dùng lẫn. Token được kiểm account role/lock/expiry/lockout/credential version và phiên server; không lưu bearer/resume thô trong DB/audit. Reset password thu hồi token/cookie cũ. Response API/admin có Cache-Control no-store.

LastHeartbeatAt hiện được server ghi khi login, chọn mode lần đầu, resume. C3 sẽ bổ sung heartbeat30s để giữ mốc liên hệ khi đang làm bài. Resume không đổi StartedAt hoặc mode; Training không thể gia hạn cửa sổ4giờ qua resume; Testing không đổi mốc chọn mode. End có row lock và chỉ audit lần chuyển trạng thái đầu. Chưa có timer/Word lock hoặc network-loss detection phía client.

Sai password/unknown user/temporary lockout/admin dùng learner API đều401 cùng `invalid_credentials` và thông báo “Invalid credentials. Please try again later.” Chỉ sau password đúng của learner, permanent lock/expiry/no uses mới trả403 với account_locked/account_expired/no_uses_left. Input sai400, mode/key conflict409, rate limit429. Admin không dùng learner API và không tiêu lượt; learner không vào admin. Chỉ owner tạo/cấp lượt qua web, không đăng ký công khai hoặc thanh toán.

## Admin và bí mật

- `migrate` áp dụng migration rõ ràng; `create-admin <username>` đọc `MOSWORD_ADMIN_PASSWORD`, chỉ tạo admin đầu tiên. Advisory lock trong transaction chống hai tiến trình bootstrap cùng lúc. Không reset/promote tài khoản trùng; admin đã tồn tại thì từ chối.
- Username 3–64 ký tự ASCII thuộc chữ/số/`._-`, chuẩn hóa uppercase invariant; mật khẩu bootstrap 12–128 ký tự, không toàn khoảng trắng. PasswordHasher ASP.NET Core Identity PBKDF2, 210.000 vòng; không lưu mật khẩu thô.
- Cookie admin có tiền tố `__Host-`, Secure, HttpOnly, SameSite Strict, hạn 30 phút, không tự gia hạn. Mỗi request có cookie kiểm tra lại quyền admin, lock và expiry từ database. Admin web login không phải lượt sử dụng của học viên, không tạo session hoặc tăng Used.
- Razor Pages áp authorization cho toàn bộ `/Admin`, chỉ Login anonymous; POST login/logout dùng antiforgery mặc định. Logout GET không thay đổi trạng thái. Lỗi xác thực dùng chung thông báo.
- Sau10lần sai credential liên tiếp, account tạm khóa15phút; lần login được chấp nhận reset counter/lockout. Cả admin và learner dùng chung dịch vụ/row lock. Failed login, denial do khóa/hết hạn/hết lượt và lockout đều audit metadata, không password/token. Chính sách hết lượt/expiry không tự cấp lại hoặc hoàn lượt.
- Rate limit: admin login5/IP/phút theo endpoint metadata, admin khác60/IP/phút; learner login10/IP/phút và10/username/phút; API còn lại60/session/phút, access và resume chia cùng singleton budget; unauthenticated/resume thêm30/IP/phút. Không xếp hàng, vượt429. Limiter trong bộ nhớ một app, không phải cơ chế đếm lượt bền vững; restart không làm mất lockout/lượt trong PostgreSQL.
- ForwardedHeaders chỉ tin127.0.0.1 và::1, ForwardLimit1, nhận X-Forwarded-For/Proto trước HSTS, HTTPS redirect, rate limiting và authentication. Partition IP dùng địa chỉ đã qua middleware. Header từ remote khác bị bỏ qua. C6 nginx cùng host phải ghi đè header bằng IP peer/scheme thực và bind Kestrel loopback; không tin tùy ý header từ Internet.
- Connection string từ ConnectionStrings__Database, bootstrap password từ environment. Ngoài Development bắt buộc DataProtection__KeyPath tuyệt đối và ngoài repo, thiếu thì fail startup (kể cả CLI). Giữ key ring qua restart/deploy để cookie/token còn đọc được, hạn chế quyền đọc/ghi và backup; Linux filesystem key ring không tự mã hóa at rest. Không đưa private key TLS, key ký/mã hóa package, backup/secret config vào Git. Public Ed25519 verification key C2 dùng đuôi `.pub` hoặc code, được commit; không nới ignore `.key`/`.pem`/`.pfx` cho private key.

## Chính sách phiên C2

`Security/SessionPolicy.cs` là nơi quyết định quyền; `LearnerSessions.FindAsync` khóa account rồi session và dùng policy cho mọi thao tác có token, gồm manifest/download/report. Caller giữ transaction và phải commit cả nhánh401 để không rollback kết thúc/audit. Login retry cũng chạy policy, không hồi sinh phiên bị bỏ. Server lấy lại UTC sau khi chờ row lock để retry đồng thời không bị so với mốc bắt đầu mới hơn.

Phiên active phải chưa EndedAt; account là learner, không khóa, chưa expiry/lockout và credential version khớp cả token lẫn session. Khi chưa chọn mode: `now < StartedAt+15 phút`; Training: `now < StartedAt+4 giờ`; Testing: `now < ModeSelectedAt+2 giờ`. Khoảng từ heartbeat gần nhất tới now phải không quá15phút, đúng15phút còn hợp lệ cho phiên đã chọn mode; quá15phút bị chốt Abandoned và không resume lại. Deadline mode là exclusive. Các mốc đều theo server, không theo đồng hồ client.

Lần quan sát đầu tiên thấy không hợp lệ ghi EndedAt tại thời điểm quan sát, EndReason là Expired/TimeLimit/Abandoned; end chủ động ghi UserEnded. Chuyển trạng thái chỉ tạo một audit SessionEnded có sessionId/reason. Token sai credential của một phiên hiện hành không có quyền làm kết thúc phiên đó. Retry UserEnded chỉ trả receipt sau khi kiểm identity/account; không cấp quyền active cho mode/resume/download.

Access token dùng ITimeLimitedDataProtector30phút; resume255phút, purpose riêng. Mỗi phản hồi thành công của API phiên cấp access token mới; manifest/download/report cấp header Session-Token. Envelope có thêm expiry theo TimeProvider server để kiểm bằng clock được tiêm; thời hạn của Data Protection vẫn kiểm bằng đồng hồ thực. Một lớp Data Protection riêng giữ identity hint xác thực được khi timed token hết hạn: hint chỉ giúp quan sát/chốt phiên đã inactive, tuyệt đối không cấp quyền active hoặc receipt end. Không ghi token/key vào audit.

Testing có upload token purpose riêng, hạn1575phút. `CanUpload` chỉ là tiền đề cho C4, chưa có endpoint upload: identity/role/lock/lockout/version vẫn hợp lệ; chỉ Testing đã kết thúc; ModeSelectedAt<=finalizedAt<=EndedAt và finalizedAt<=ModeSelectedAt+2giờ. Cửa sổ nhận là EndedAt<=now<=min(EndedAt,ModeSelectedAt+2giờ)+24giờ, inclusive; account chỉ hết expiry sau bài thi không tự chặn upload. Quan sát end muộn không gia hạn cửa sổ. Upload token không cấp quyền manifest/download hoặc bất kỳ endpoint active nào.

## Package, manifest và đề C2

`Packages/PackageImporter.cs` đọc `MosWord2019.WinForms/Projects`, kiểm toàn bộ pool bằng ExamComposer hiện có trước khi ghi. ZIP có thứ tự/timestamp ổn định; SHA-256 ZIP vừa là version vừa là ZipSha256. Mỗi package có key AES256 ngẫu nhiên riêng. Blob: header ASCII `MWPKG001` (8 byte), nonce 12 byte, tag GCM 16 byte, rồi ciphertext ZIP; AAD UTF-8 chính xác `MosWord2019.Package.v1|<code>|<version>`. Sha256 trong descriptor là hash toàn blob, khác hash ZIP. Server giữ `<id:N>.bin` và `<id:N>.key` ngoài repo; download không chứa key trong blob.

GET `/api/packages/manifest` yêu cầu phiên active đã chọn mode. Envelope chỉ có payload/signature Base64; Ed25519 ký toàn bộ byte UTF-8 payload, gồm schema/session/mode, seed/hash, metadata pool và descriptors chứa id/code/version/hash/kích thước/downloadPath/key. Client xác minh chữ ký trước khi đọc bất kỳ field payload nào, rồi kiểm session/mode, đường dẫn, hash, giới hạn và GCM/AAD. Key xác minh biên dịch trong `MosWord2019.Projects/OnlinePackages/PackageTrust.cs`, đối chiếu bản công khai `server/manifest-verification.pub`; không nhận key từ manifest hay App.config. Private seed ký hiện ở `%LOCALAPPDATA%/MosWord2019.Server/secrets/manifest-signing.key`, ngoài Git; bảo quản và backup riêng cùng storage/key ring.

Lần chọn Testing đầu tiên, ServerExamPool cấp seed64bit ngẫu nhiên và snapshot toàn pool metadata/catalog tối thiểu, dùng lại ExamComposer và ExamDefinitionFingerprint. Snapshot giữ metadata đủ tái tạo đề; không phát key/asset của project không được chọn. Manifest Testing có đúng7descriptor phiên bản được ghim, cùng seed dạng chuỗi và definitionHash; full pool chỉ chứa metadata cần compose, không có starter/assets/key ngoài7project. Client compose lại bằng cùng thuật toán và phải khớp hash, POST `/api/sessions/definition` để xác nhận trước download. Hash sai trả409; đúng ghi ReportedDefinitionHash và một audit ExamDefinitionConfirmed. GET `/api/packages/{id}/download` của Testing chỉ nhận7id trong snapshot và sau report hợp lệ; mỗi download thành công audit session/package/code/version/size. Training nhận các phiên bản mới nhất theo code.

Import dùng PostgreSQL advisory transaction lock chung cho filesystem và DB. Import lại ZIP cùng version không ghi đè blob/key, nhưng cập nhật catalog policy cho các phiên được tạo sau; snapshot các bài đang thi không đổi. Storage write tạo file mới/atomic rename và từ chối reparse/symlink; rollback/crash có thể để file orphan. Lần import tiếp theo dưới cùng lock dọn đúng tên blob/key/temp không còn DB tham chiếu; giữ phiên bản cũ còn DB tham chiếu và không xóa file ngoài mẫu hay signing key. Linux tạo storage700 và file600, owner service; path tuyệt đối ngoài repo, không dùng symlink.

## Cache client và bản phát hành

`MosWord2019.Projects/OnlinePackages` cung cấp HttpPackageTransport, OnlinePackageCache và PackageCacheLedger cho C5 sử dụng. Transport chỉ HTTPS, không tự redirect và giữ xác minh TLS bình thường; C6 mới có SPKI pin. Cache mặc định `%LOCALAPPDATA%/MosWord2019/Packages/<session>/<instance>`, ACL riêng SID người dùng; không giải mã vào thư mục cài đặt. Kiểm ZIP traversal, duplicate/case collision, symlink/reparse, giới hạn kích thước và tỷ lệ nén trước giải nén.

Ledger bổ sung ghi nhận native file identity bền vững trước byte plaintext đầu tiên, rồi hash khi hoàn tất; lease chống recovery đụng phiên còn mở. Dispose/end dọn file sở hữu; startup gọi RecoverIncompletePackages trước recovery chung để xử lý crash/partial extraction. File còn đúng identity do cache tạo được dọn cả khi nội dung thay đổi; file/thư mục đã bị thay bằng identity khác hoặc reparse không bị xóa. Không suy ownership từ tên/hash riêng lẻ và không xóa thư mục người dùng đệ quy.

WinForms mặc định UseLocalPackages=true ở Debug và false ở Release; `/p:UseLocalPackages=true` là lựa chọn phát triển rõ ràng. Release không copy Projects; target build xóa bản nguồn đã từng ship và fail nếu còn bất kỳ file Projects stale nào trong output. C2 mới cung cấp thư viện và startup cleanup, chưa nối online login/UI, session-token refresh vào vòng đời app hoặc heartbeat C3. Vì vậy Release mặc định chưa dùng độc lập cho người học trước C5; không coi đây là bản online production đã nghiệm thu.

## Các bước tiếp theo đã chốt, chưa triển khai

- C3: heartbeat 30 giây; offline sau 3 lần lỗi hoặc 90 giây. Training 4 giờ theo server, đồng hồ vẫn chạy khi offline và Word bị khóa. Testing giữ cơ chế pause/offline hiện có; crash downtime vẫn tính. Chờ mạng tối đa 30 phút rồi kết thúc; kết quả Testing chờ upload lại.
- C4: nhận result B4 có HMAC bằng key từng session; upload idempotent/retry; lưu flags chênh lệch thời gian và trang kết quả admin.
- C5: thay `OfflineAuthenticationService` sau `IAuthenticationService`, giữ login UI U1, DPAPI resumeToken. Chỉ pre-warm Word sau login thành công. Training thoát sớm vẫn kết thúc lượt, xác nhận “Thoát sẽ kết thúc lượt sử dụng này. Bạn còn hh:mm.” Testing giữ50phút,7project,4–6task/project,34–40task,1000điểm, từ700 “Đạt”, dưới700 “Cần Luyện Tập thêm”.
- C6: Ubuntu 24.04 LTS, PostgreSQL/nginx/systemd/ufw/backup/rotation; không domain, TLS self-signed và SPKI SHA-256 pin compile trong client. Release không cho App.config thay pin; từ chối mọi certificate khác, không tắt validation toàn cục. C0 chỉ dùng HTTPS development certificate cho chạy local.
- C7: review bảo mật và checklist nghiệm thu online. Chưa tuyên bố server C2 sẵn sàng public trên VPS.

C2 không đổi grader, engine, nội dung package hoặc starter. Cache bổ sung ledger riêng và móc recovery lúc startup, giữ quy tắc ownership của journal chung. C3–C7 vẫn là công việc tiếp theo, chưa có triển khai VPS hoặc đường bỏ qua TLS.

## Tài liệu gốc

- [EF Core 10 và Npgsql](https://www.npgsql.org/efcore/release-notes/10.0.html).
- [Antiforgery ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
- [PostgreSQL Testcontainers](https://dotnet.testcontainers.org/modules/postgres/).
- [Trusted proxy ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
- [Rate limiting ASP.NET Core](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).
