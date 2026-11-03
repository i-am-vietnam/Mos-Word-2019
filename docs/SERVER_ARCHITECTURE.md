# Kiến trúc server MOS Word 2019

## Phạm vi C0–C6

C0–C4 cung cấp nền tảng, tài khoản/lượt/phiên, package ký/mã hóa, heartbeat và kết quả. C5 nối client WinForms online từ login/mode đến recovery/upload; hướng dẫn thử local ở C5_LOCAL_TEST.md. C6 cung cấp bộ deploy PC/VPS, owner tự thực thi theo DEPLOY_VPS.md; agent không truy cập VPS. C7 nghiệm thu bảo mật còn lại.

Client tiếp tục WinForms, .NET Framework 4.7.2, C# 7.3 và Word Interop. `server/MosWord2019.Server.sln` không nằm trong `MosWord2019.slnx`, không tham chiếu project WinForms. `server/MosWord2019.ExamComposition` biên dịch liên kết các model/composer hiện có của Core cho server, giữ cùng thuật toán đề và fingerprint. App ASP.NET Core .NET 10 chứa cả HTTP API và Razor Pages; EF Core 10 dùng Npgsql/PostgreSQL 16+. Chọn một app và một database để phù hợp VPS 2 vCPU, 4 GB RAM, 35 GB NVMe.

VPS đã chốt là NVME R2: 2 vCPU Xeon Gold, 4 GB RAM, 35 GB NVMe, 100 Mbps, một IPv4, backup nhà cung cấp hằng ngày, Ubuntu 24.04 LTS, không domain. Không build trên VPS: C6 sẽ publish framework-dependent `linux-x64` bằng `dotnet publish -c Release` trên PC rồi copy bằng scp/rsync. C6 cấu hình PostgreSQL shared_buffers512MB/effective_cache_size1536MB/work_mem8MB/max_connections50, swap2GB và pg_dump/rotation bên cạnh backup nhà cung cấp.

## Các thành phần đã có

| Tệp/thư mục | Trách nhiệm |
|---|---|
| `server/MosWord2019.Server/Program.cs` | DI, pipeline HTTPS/auth/rate limit, health và CLI quản trị/import/key công khai |
| `server/MosWord2019.Server/Data/Entities.cs`, `server/MosWord2019.Server/Data/ServerDbContext.cs` | Model, mapping PostgreSQL và ràng buộc |
| `server/MosWord2019.Server/Data/Migrations/` | `InitialFoundation`, `AccountsAndUses`, `SessionLifetimes`, `PackageDistribution`, `HeartbeatHistory`, snapshot và Up/Down |
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
| `server/MosWord2019.Server/Security/SessionHeartbeats.cs`, `SessionTiming.cs` | Heartbeat, deadline, offline gaps và cờ thời gian Testing |
| `MosWord2019.Projects/OnlineSessions/` | HTTPS heartbeat và monitor/countdown theo clock đơn điệu |
| `server/MosWord2019.Server.Tests/` | xUnit, HTTP qua TestServer, CLI subprocess và PostgreSQL Testcontainers |

`GET /health` trả 200 khi tiến trình hoạt động, không yêu cầu DB. `GET /health/ready` trả 200 khi kết nối được DB và mọi migration đã áp dụng; 503 nếu thiếu cấu hình, mất DB hoặc còn migration. Phản hồi không chứa connection string. Health đi qua HTTPS như các endpoint khác. Không tự migrate hoặc tự tạo admin khi web khởi động.

## Dữ liệu

| Bảng | Nội dung/ràng buộc |
|---|---|
| `Accounts` | UUID, username duy nhất, password hash, cờ admin/lock, lượt và expiry; `0 <= Used <= MaxUses`; FailedLoginCount0..10, LockoutUntil, CredentialVersion |
| `Sessions` | UUID, account FK, LoginRequestId duy nhất trong account, AccountCredentialVersion, SHA-256 machine ID, mode nullable hoặc Training/Testing, mốc bắt đầu/chọn mode/heartbeat/kết thúc, EndReason, seed/hash đề và snapshot pool bất biến |
| `Heartbeats` | Session FK, thời điểm nhận, client elapsed nullable và WasOffline |
| `OfflineGaps` | Session FK, StartedAt/EndedAt, Kind Network hoặc Unobserved |
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

Machine ID đầu vào là SHA-256 hex64 của giá trị ổn định trên máy, client C5 sẽ tính; server chuẩn hóa lowercase, không nhận tên máy/user/hardware gốc. C2 chưa nối UI hoặc thêm machine-id collector online; phần đó thuộc C5. Access/resume dùng purpose Data Protection khác nhau; không dùng lẫn. Token active được kiểm account role/lock/expiry/credential version và phiên server; temporary lockout chỉ chặn password login mới; resume hợp lệ không bị chặn bởi lockout tạm; không lưu bearer/resume thô trong DB/audit. Reset password thu hồi token/cookie cũ. Response API/admin có Cache-Control no-store.

LastHeartbeatAt được server ghi khi login, chọn mode lần đầu, resume và heartbeat được chấp nhận. C3 có heartbeat30giây và watchdog client; C5 mới nối monitor vào giao diện/vòng đời online. Resume không đổi StartedAt hoặc mode; Training không thể gia hạn cửa sổ4giờ qua resume; Testing không đổi mốc chọn mode. End có row lock và chỉ audit lần chuyển trạng thái đầu. Monitor C3 cung cấp kết nối/countdown cho C5; chưa nối điều khiển Word/giao diện online.

Sai password/unknown user/temporary lockout/admin dùng learner API đều401 cùng `invalid_credentials` và thông báo “Invalid credentials. Please try again later.” Chỉ sau password đúng của learner, permanent lock/expiry/no uses mới trả403 với account_locked/account_expired/no_uses_left. Input sai400, mode/key conflict409, rate limit429. Admin không dùng learner API và không tiêu lượt; learner không vào admin. Chỉ owner tạo/cấp lượt qua web, không đăng ký công khai hoặc thanh toán.

## Admin và bí mật

- Temporary lockout chặn đăng nhập admin mới nhưng không thu hồi cookie admin đang hoạt động. Cookie vẫn bị từ chối khi role/Locked/expiry/credential version thay đổi; không dùng lockout đăng nhập như cơ chế kết thúc phiên.
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

Phiên active phải chưa EndedAt; account là learner, không khóa, chưa expiry và credential version khớp cả token lẫn session. Khi chưa chọn mode: `now < StartedAt+15 phút`; Training: `now < StartedAt+4 giờ`; Testing: `now < ModeSelectedAt+2 giờ`. C5-FIX: khoảng từ heartbeat gần nhất tới now không quá35phút (inclusive) cho phiên đã chọn mode; quá35phút bị chốt Abandoned. Heartbeat sau20/34phút có thể nối lại bằng token trong bộ nhớ và WasOffline=true. /resume kiểm thêm cửa sổ15phút từ liên hệ cuối; quá15phút từ chối, không chốt phiên còn active35phút chỉ vì request resume cũ. Client vẫn kết thúc sau30phút mất mạng, nên35phút là dung sai server, không thêm thời gian cho người học. Deadline mode là exclusive. Các mốc đều theo server, không theo đồng hồ client.

Lần quan sát đầu tiên thấy không hợp lệ ghi EndedAt tại thời điểm quan sát, EndReason là Expired/TimeLimit/Abandoned; end chủ động ghi UserEnded. Chuyển trạng thái chỉ tạo một audit SessionEnded có sessionId/reason. Token sai credential của một phiên hiện hành không có quyền làm kết thúc phiên đó. Retry UserEnded chỉ trả receipt sau khi kiểm identity/account; không cấp quyền active cho mode/resume/download.

Access token dùng ITimeLimitedDataProtector40phút; resume255phút, purpose riêng. Mỗi phản hồi thành công của API phiên cấp access token mới; manifest/download/report cấp header Session-Token. Envelope có thêm expiry theo TimeProvider server để kiểm bằng clock được tiêm; thời hạn của Data Protection vẫn kiểm bằng đồng hồ thực. Một lớp Data Protection riêng giữ identity hint xác thực được khi timed token hết hạn: hint chỉ giúp quan sát/chốt phiên đã inactive, tuyệt đối không cấp quyền active hoặc receipt end. Không ghi token/key vào audit.

Testing có upload token purpose riêng, hạn1575phút. Endpoint C4 dùng `CanUpload`: identity/role/lock/version vẫn hợp lệ; chỉ Testing đã kết thúc; ModeSelectedAt<=finalizedAt<=EndedAt và finalizedAt<=ModeSelectedAt+2giờ. Cửa sổ nhận là EndedAt<=now<=min(EndedAt,ModeSelectedAt+2giờ)+24giờ, inclusive; account chỉ hết expiry sau bài thi không tự chặn upload. Quan sát end muộn không gia hạn cửa sổ. Upload token không cấp quyền manifest/download hoặc bất kỳ endpoint active nào.

## Heartbeat và thời gian C3

POST `/api/sessions/{id}/heartbeat` dùng Bearer access token của đúng phiên và JSON `{ "clientElapsedMilliseconds": null, "wasOffline": false }`. Elapsed có thể null hoặc số nguyên0..86400000; sai trả400, chưa chọn mode409, sai/không còn quyền401. Reply có sessionId, mode, serverTime, expiresAt, remainingSeconds và sessionToken mới. SessionHeartbeats dùng FindAsync với row lock account/session, commit cả denied để giữ EndedAt/audit. Limiter heartbeat riêng6request/phút theo SessionId, không reset khi token đổi; bucket API chung60/session/phút vẫn áp dụng.

Temporary credential lockout chỉ chặn NEW login và resume, không cắt phiên active, heartbeat, end hoặc quyền upload hẹp. Admin Locked, role sai hoặc credential reset vẫn thu hồi quyền active theo policy. Không dùng lỗi mật khẩu từ máy khác để khóa bài đang làm.

Migration HeartbeatHistory thêm Heartbeats và OfflineGaps, cùng Session.TestingClockStartedAt, TestingClockBaselineMilliseconds và TimingInconsistent. Mỗi heartbeat được nhận lưu ReceivedAt, elapsed nullable và WasOffline; update LastHeartbeatAt. Khoảng không có liên hệ quá90giây ghi gap từ lastHeartbeat+90 tới now, cắt tại hard deadline, phân biệt Network với Unobserved. Thiếu heartbeat/crash không tự được coi là network pause để trừ thời gian.

WasOffline là quan sát client về kết nối, không phải bằng chứng server không nhận POST. Nếu POST tới nhưng response bị mất, request WasOffline sau đó dùng heartbeat cuối có WasOffline=false cộng30giây để ước lượng network gap; gap Network đang mở được kéo dài thay vì tạo pause trùng. Gap Network được lấy hợp các khoảng giao với thời gian kiểm, không cộng chồng. Network gap có thể dài0 khi start=end để giữ dấu vết episode suy luận. Chẩn đoán lấy tolerance nền90giây cộng90giây cho mỗi episode Network riêng biệt sau khi hợp/clip; các đợt mất response ngắn lặp lại không được cộng sai số thành false flag. Chỉ freeze vượt tổng bất định này mới bị flag. Đây là tín hiệu tham khảo thô, không phải bằng chứng gian lận. WasOffline là hint không tin cậy, không quyết định cấp quyền; hard deadline không trừ bất kỳ Network gap nào.

Testing heartbeat đầu tiên có elapsed neo TestingClockStartedAt=server now và baseline=elapsed, loại thời gian chờ màn hình Rules khỏi phép đối chiếu active timer. Elapsed đi trước thời gian từ chọn mode quá90giây bị flag ngay; các heartbeat sau so client elapsed với server elapsed từ anchor, trừ đúng gap Network và cộng baseline, áp dụng tổng bất định theo episode ở trên. Unobserved/crash gap vẫn tính. TimingInconsistent là sticky, chỉ audit TestingTimingFlagged lần đầu; không sửa điểm hay grader. C4 sẽ gắn Result qua SessionId, giữ flag và gọi SessionTiming.IsInconsistent khi nhận kết quả, kể cả offline gap tới lúc finalize; chưa có endpoint C4.

MosWord2019.Projects/OnlineSessions cung cấp HttpHeartbeatTransport và SessionConnectivityMonitor. Monitor gửi mỗi30giây, watchdog mỗi1giây, timeout10giây; không có request chồng nhau và bỏ response muộn/cancel/dispose. Ban đầu offline cho tới reply hợp lệ đầu, request đầu WasOffline=false; sau khi đã có reply,3lỗi liên tiếp HOẶC90giây không có reply thành công làm offline. Reply sai identity/mode/deadline/clock hoặc401/410 khóa quyền monitor. Token mới phát qua event; các event chạy trên background, C5 phải marshal về UI.

ProjectedServerTime và TrainingRemaining/TestingRemaining được suy từ server reply cộng clock đơn điệu, không dùng DateTime.UtcNow của client. Kiểm drift cho phép2giây để chịu NTP slew nhỏ, hard deadline không tăng và remaining bị clamp để không bù lại thời gian đã trôi; server UTC thực sự lùi vẫn bị từ chối. Training4giờ vẫn giảm khi offline; TestingRemaining là hard cap2giờ server, tách biệt active exam timer50phút và pause/offline engine hiện có. C3 chưa thay monitor vào UI/auth hoặc sửa Training/Testing/Word lock; C5 sẽ nối các property/event này vào luồng online. Các luồng offline hiện có và quy tắc chờ mạng/kết thúc của engine giữ nguyên.

## Package, manifest và đề C2

`Packages/PackageImporter.cs` đọc `MosWord2019.WinForms/Projects`, kiểm toàn bộ pool bằng ExamComposer hiện có trước khi ghi. ZIP có thứ tự/timestamp ổn định; SHA-256 ZIP vừa là version vừa là ZipSha256. Mỗi package có key AES256 ngẫu nhiên riêng. Blob: header ASCII `MWPKG001` (8 byte), nonce 12 byte, tag GCM 16 byte, rồi ciphertext ZIP; AAD UTF-8 chính xác `MosWord2019.Package.v1|<code>|<version>`. Sha256 trong descriptor là hash toàn blob, khác hash ZIP. Server giữ `<id:N>.bin` và `<id:N>.key` ngoài repo; download không chứa key trong blob.

GET `/api/packages/manifest` yêu cầu phiên active đã chọn mode. Envelope chỉ có payload/signature Base64; Ed25519 ký toàn bộ byte UTF-8 payload, gồm schema/session/mode, seed/hash, đề server đã compose, metadata/task được chọn và descriptors chứa id/code/version/hash/kích thước/downloadPath/key. Client xác minh chữ ký trước khi đọc bất kỳ field payload nào, rồi kiểm session/mode, đường dẫn, hash, giới hạn và GCM/AAD. Key xác minh biên dịch trong `MosWord2019.Projects/OnlinePackages/PackageTrust.cs`, đối chiếu bản công khai `server/manifest-verification.pub`; không nhận key từ manifest hay App.config. Private seed ký hiện ở `%LOCALAPPDATA%/MosWord2019.Server/secrets/manifest-signing.key`, ngoài Git; bảo quản và backup riêng cùng storage/key ring.

Lần chọn Testing đầu tiên, ServerExamPool cấp seed64bit ngẫu nhiên, dùng ExamComposer và ExamDefinitionFingerprint hiện có, rồi ghim version/package và cả ExamDefinition trong snapshot server. Manifest schemaVersion2 chỉ phát đề server đã compose, seed/hash, metadata/task thực sự được chọn và đúng7descriptor/version/key; không phát catalog hoặc toàn pool cho client. Client bình thường xác minh chữ ký, cấu trúc/hash của đề và dùng trực tiếp ExamDefinition đã ký; không compose lại đề hoặc nhận thông tin task/project ngoài đề. Composer phía client chỉ còn phục vụ chẩn đoán/tests parity. Server giữ pool/catalog nội bộ trong snapshot bất biến; phiên C2 cũ thiếu Definition được compose từ chính snapshot cũ một lần, kiểm hash rồi persist Definition trước khi phát manifest schema2.

Client POST `/api/sessions/definition` để xác nhận hash trước download. Hash sai trả409; đúng ghi ReportedDefinitionHash và một audit ExamDefinitionConfirmed. GET `/api/packages/{id}/download` của Testing chỉ nhận7id trong snapshot sau report hợp lệ. PackageStorage.OpenBlob mở stream giới hạn kích thước, kiểm header/hash toàn blob rồi reset Position0; endpoint commit audit PackageDownloaded trước trả Results.File(FileStream), ASP.NET Core quản lý dispose khi gửi xong, nhánh lỗi dispose ngay. Không gom toàn ciphertext thành byte[] cho mỗi download. Training nhận các phiên bản mới nhất theo code.

Import dùng PostgreSQL advisory transaction lock chung cho filesystem và DB. Import lại ZIP cùng version không ghi đè blob/key, nhưng cập nhật catalog policy cho các phiên được tạo sau; snapshot các bài đang thi không đổi. Storage write tạo file mới/atomic rename và từ chối reparse/symlink; rollback/crash có thể để file orphan. Lần import tiếp theo dưới cùng lock dọn đúng tên blob/key/temp không còn DB tham chiếu; giữ phiên bản cũ còn DB tham chiếu và không xóa file ngoài mẫu hay signing key. Linux tạo storage700 và file600, owner service; path tuyệt đối ngoài repo, không dùng symlink.

## Cache client và bản phát hành

`MosWord2019.Projects/OnlinePackages` cung cấp HttpPackageTransport, OnlinePackageCache và PackageCacheLedger cho C5 sử dụng. Transport chỉ HTTPS, không tự redirect và giữ xác minh TLS bình thường; C6 mới có SPKI pin. Cache mặc định `%LOCALAPPDATA%/MosWord2019/Packages/<session>/<instance>`, ACL riêng SID người dùng; không giải mã vào thư mục cài đặt. Kiểm ZIP traversal, duplicate/case collision, symlink/reparse, giới hạn kích thước và tỷ lệ nén trước giải nén.

Ledger bổ sung ghi nhận native file identity bền vững trước byte plaintext đầu tiên, rồi hash khi hoàn tất; lease chống recovery đụng phiên còn mở. Dispose/end dọn file sở hữu; startup gọi RecoverIncompletePackages trước recovery chung để xử lý crash/partial extraction. File còn đúng identity do cache tạo được dọn cả khi nội dung thay đổi; file/thư mục đã bị thay bằng identity khác hoặc reparse không bị xóa. Không suy ownership từ tên/hash riêng lẻ và không xóa thư mục người dùng đệ quy.

WinForms mặc định UseLocalPackages=true ở Debug và false ở Release; `/p:UseLocalPackages=true` là lựa chọn phát triển rõ ràng. Release không copy Projects; target build xóa bản nguồn đã từng ship và fail nếu còn bất kỳ file Projects stale nào trong output. C2 mới cung cấp thư viện và startup cleanup, chưa nối online login/UI, session-token refresh và monitor heartbeat C3 vào vòng đời app. Vì vậy Release mặc định chưa dùng độc lập cho người học trước C5; không coi đây là bản online production đã nghiệm thu.

## Kết quả C4: quyền, kiểm điểm và idempotency

SessionReply cấp resultSigningKey Base64(32byte) khi login thành công, mode và resume; mỗi session có key dẫn xuất SHA256 từ32byte CSPRNG, random source bị hủy. DB chỉ giữ key dẫn xuất, không random seed; không ghi key/token/signature/payload vào audit hoặc log. Đây là secret phiên, cần backup/protect PostgreSQL như password hashes; không phải package signing/encryption key. Phiên C3 cũ được migration ResultUploads cấp key dẫn xuất độc lập bằng SHA256 của hai UUID ngẫu nhiên. Client lấy key qua reply hợp lệ, không nhận từ JSON result. Chỉ phát key qua HTTPS; TLS/pin C5/C6 vẫn bắt buộc trước production.

POST /api/results nhận raw UTF8 JSON B4 version1, tối đa256KiB, Authorization Bearer access hoặc uploadToken và header X-Result-Signature=Base64(HMAC-SHA256(resultSigningKey, exact body bytes)). Decode key Base64 trước HMAC. So sánh signature constant-time; JSON duplicate properties/depth>64/trailing data bị từ chối. Giữ byte payload cho hash SHA256 exact; DB JSONB lưu bản JSON để đối chiếu. Đổi whitespace cũng đổi hash/signature, không serialize lại payload khi retry.

Active access phải qua FindAsync/SessionPolicy. Upload purpose tách biệt, chỉ Testing active hoặc CanUpload: cùng account/credential version, không admin Locked, finalizedAt trong [ModeSelectedAt, min(EndedAt,ModeSelectedAt+2h)], now không quá min(EndedAt,hardcap)+24h. Token expiry vẫn là defense in depth. Expired access không được đổi thành upload permission; dùng uploadToken riêng. UploadToken không cấp mode/end/heartbeat/package hoặc mint access token, kể cả khi Testing còn active. Khi server lần đầu quan sát session inactive, chốt EndedAt/EndReason/audit như chính sách chung. HMAC không chứng minh thời điểm finalize: completedUtc/elapsed là evidence bị kiểm bounds, không gia hạn quyền theo clock client.

Account rồi session row lock trong transaction bảo vệ credential revocation/idempotency. Unique Results.SessionId giữ một kết quả: cùng exact payloadHash trả200 receipt cũ, khác hash trả409 result_conflict và audit ResultUploadConflict (chỉ sessionId/hash). Quyền/key vẫn phải hợp lệ khi retry. Receipt gồm sessionId/payloadHash/score/passed/flags/serverTime; sessionToken mới chỉ cấp khi request dùng active access. Upload purpose có chung bucket60/session/phút; token sai vẫn bị budget IP. Giới hạn body256KiB chỉ áp endpoint này, endpoint khác giữ16KiB.

ResultValidation đọc ExamDefinition từ snapshot server đã ghim/ký ở manifest, kiểm hash, seed, definition client và đầy đủ task IDs/instruction keys/outcomes Pass/Fail/Error, từ chối duplicate/missing/foreign task. Server dùng nguyên linked-source ExamScorer/ScoreAllocator; không tin total/passed/points/awardedPoints/perProject client. Điểm1000 và ngưỡng700 giữ nguyên. Sai/Error được lưu WrongTasksJson với projectId/taskId/instructionKey/instruction: instruction lấy từ đúng packageId/version/lang đã ghim, kiểm blob/hash/GCM/AAD/ZIP hash rồi chỉ đọc lang/vi.json hoặc lang/en.json có giới hạn. Không tin đường dẫn hoặc instruction text client. Thiếu/hỏng storage/key/instruction trả503 để retry sau phục hồi, không nhận kết quả thiếu instruction.

Result lưu TimingInconsistent sticky từ Session và đánh giá elapsed cuối bằng SessionTiming.IsInconsistent; lưu snapshot offline gaps của server. Ongoing Network gap chỉ kéo tới finalizedAt khi last heartbeat đã WasOffline và khớp endpoint gap; silence/crash vẫn Unobserved và không pause. ClockRollbackDetected từ B4 được giữ trong flags và audit ResultClockRollback, chỉ lần accept đầu. Flags không tự sửa điểm/verdict và không phải kết luận gian lận. HMAC xác minh sở hữu secret, không attestation client hoặc Word grader.

Razor /Admin/Results liệt kê global hoặc accountId, lọc ngày UTC/phân trang50; Details hiển thị điểm, Đạt/Cần Luyện Tập thêm, flags/gaps và instruction từng task sai. Kế thừa policy Admin/cookie/rate limit, render Razor encode, không expose signing key hoặc raw payload. Có link từ Accounts/Sessions và admin home.

## Queue client C4 và giữ dữ liệu

OnlineResults tách IResultUploadTransport để test/fake; transport HTTPS dùng validation hệ thống, không redirect hoặc bypass TLS. C5 đã nối pinned transport và online auth. Enqueue yêu cầu B4 đã có đúng server SessionId, key32byte và upload token từ phiên; không sửa B4 để gắn định danh sau khi đã persist. C5 giữ binding này xuyên finalize/recovery. Kết quả offline lịch sử không có credential nên không tự đưa lên server.

ResultUploadStore lưu DPAPI CurrentUser ở LocalAppData/MosWord2019/ResultUploads, SID-only ACL, từ chối repo/reparse paths. Record chứa exact payload/key/token/origin/hash/retry state đã mã hóa; file tạm cũng chỉ ciphertext, flush và atomic commit. Pump lease riêng chống nhiều worker; metadata lease ngắn/retry không giữ qua network, nên đăng ký bài mới không bị chặn bởi upload bài cũ. Tối đa1024 pending; accepted record chuyển atomic sang Receipts riêng và không chiếm cap pending. Bảo quản Windows profile/DPAPI, không copy credential sang máy khác.

Program chạy worker lúc startup chỉ cho entry đã đăng ký; timeout request15s, backoff5s nhân đôi tối đa1h, quét5s và hỗ trợ cancellation. Crash trước/sau gửi đều retry exact bytes. Chỉ receipt200 đúng session/hash, score0..1000 và passed==(score>=700), flags/time hợp lệ mới chuyển accepted; server có thể sửa total client. Mọi failure/401/409/hết quyền giữ pending và evidence, không tự sửa kết quả hoặc cấp lại quyền. JSON B4 gốc và pendingUpload=true giữ bất biến để recovery SameCommittedResult không thay đổi; trạng thái pending hiệu lực qua GetStatus/receipt riêng. Không xóa local result kể cả sau accepted.

HeartbeatRetentionWorker chạy lúc startup để bù downtime rồi mỗi24h theo TimeProvider UTC; scoped ExecuteDelete chỉ Heartbeats.ReceivedAt < now−90ngày (giữ boundary bằng cutoff). Lỗi log metadata rồi retry daily, cancellation dừng sạch. Giữ nguyên Sessions, OfflineGaps, Results và AuditLog; không áp cascade retention lên chúng. Backup/dung lượng những bảng giữ lâu thuộc C6. Production vẫn bắt buộc DataProtection:KeyPath ngoài repo để cookie/token sống qua restart, cùng backup DB và package keys/blob; không public C4 trước cấu hình C5/C6.

## C5 — Client online và biên giới bảo mật

OnlineAuthenticationService thực thi IAuthenticationService, có async interface cho LoginForm. Login thành công hiển thị lượt, mode POST thành công mới mở/pre-warm Word. OfflineAuthenticationService chỉ biên dịch Debug; Release không nhận offline credentials. Login lỗi credential/unknown/temporary lockout dùng thông báo chung VI/EN có hướng dẫn thử lại sau, tránh account enumeration.

MachineIdentity SHA256 MachineGuid, không gửi raw. ResumeCredentialStore dùng DPAPI CurrentUser và ACL user-only ngoài repo, ghi atomic; normal end xóa, crash/shutdown giữ. ServerConnectionSettings sinh compiled URL/SPKI trong EXE bằng MSBuild; Release không đọc override App.config, Debug có development URL/pin. PinnedHttpClientFactory kiểm SHA256 SPKI/hostname/validity theo instance và chặn redirect/origin khác, không thay global TLS. Pin mặc định zero fail-closed; C6 cần build với pin thật.

OnlineSessionContext giữ SessionConnectivityMonitor, credential refresh và package leases. MainForm dùng monitor cho overlay/owned Word lock; Training countdown ở ô giữa đỏ đậm, chiếu server UTC+monotonic, vẫn trôi offline. Training tải catalog trước, chỉ tải package khi Go. Normal exit xác nhận thời gian còn lại rồi end; expiry/admin revoke/timeout đóng owned path và về login. Testing giữ engine/timer/score hiện có và đề ký server, chỉ 7 gói được cấp. ServerSessionClock neo UTC response server, không dùng wall clock Windows cho licensing; timestamp finalize kẹp tại hard cap Testing. ModeSelectedAt trong SessionReply là mốc persist, chuẩn hóa microsecond PostgreSQL trước lưu để response đầu/retry/resume giống nhau.

Startup gọi server resume trước local cleanup/recovery. Bị từ chối: chỉ finalize saved-only và queue bằng quyền upload, không pre-warm Word. Không chứng minh được ownership hoặc cache/state bị thay thì giữ bằng chứng để owner xử lý. RetainRecoveryPackages giữ cache lease cũ trong lúc redownload; không xóa đầu vào chấm trước khi recovery hoàn tất. ResultPersisted enqueue sau persist B4 trước close runtime/archive; lỗi enqueue giữ Completed state và B4. Queue dùng pinned transport, retry startup/sau login, chỉ ACK đúng mới xác nhận uploaded. Normal end xóa resume, upload ledger riêng vẫn bền vững; offline Debug lịch sử không tự upload.

Chỉ thay orchestration, không grader/assertions/nội dung/starter/framework. docs/C5_LOCAL_TEST.md có quy trình dev certificate/PG/admin/learner và checklist Word thật; smoke tự động không thay nghiệm thu Word native.

## C6 — Triển khai và vận hành

server/deploy/01 tạo runtime10/PG16/nginx/UFW22+443/fail2ban/security updates, swap2GiB/tuning4GB; SSH xác minh khóa+sự xác nhận terminal, có config rollback. Runtime moswordsvc nologin chỉ ghi state trong /var/lib/mosword theo systemd; packages/DP svc700, signing seedroot:svc640 và keysroot:svc750 chỉ đọc. Runtime DB CRUD khác migrator DDL, hai envroot600, CLI dùng env kế thừa thay secretargv.

02TLS ECDSAP256 SANIPv4/3650ngày, preserve qua rerun, rotate phải client mới. Nginx TLS1.2/1.3 chỉ443, body512k, HSTS, optional Admin allow-list, overwriteXFF/proto từ edge; Kestrel5000 vàPG5432 localhost. publish.ps1 chỉ build PC linux-x64 framework-dependent;03snapshot/hash/validate archive, backup/migrate, atomic current, restart/health/binaryrollback và3release. Không tự downgrade schema; chỉ migration tương thích ngược.

04import đủ28 trước dọn plaintext best-effort;05bootstrap passwordẩn/env;08chỉin public values.06backup DB+package/per-package keys/signingseed/DP/env/TLS trong lúc app dừng ngắn,03:15UTC,14ngày/8weekly;07export AES256CBC/PBKDF2 bằngpassFD vàciphertext600, SHA256 từroot để đối chiếu downloadPC. Không coi CBC là chữ ký; bảo quản passphrase/offsite.99kiểm service/ready/CA+SPKI/UFW/TCP-UDP/timer/backup/disk. Restore/move phải giữ DB+state đồng bộ và dựng lại nginx02/allow-list; đổiIP cần client mới, drain queue ghim origin trước cutover.

Client production key sinh trongobj khi MSBuild PackageManifestPublicKey được truyền, defaultdev/test source public key không đổi. build-client.ps1 kiểm URL/SPKI/key, build Release không Projects plaintext và phản chiếu compiled trust; không có runtime override mới. Không thay grader/framework/nội dung. Container test realapt/PG/nginx/runtime/crypto, mocksystemctl/UFW/swapon; owner phải nghiệm thu host thật theo docs/DEPLOY_VPS.md.

## Các bước tiếp theo chưa triển khai

- C7: review bảo mật và nghiệm thu online thực tế sau khi owner triển khai C6. Không coi Docker mock là nghiệm thu VPS/firewall/systemd/SSH thật.
## Tài liệu gốc

- [EF Core 10 và Npgsql](https://www.npgsql.org/efcore/release-notes/10.0.html).
- [Antiforgery ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
- [PostgreSQL Testcontainers](https://dotnet.testcontainers.org/modules/postgres/).
- [Trusted proxy ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
- [Rate limiting ASP.NET Core](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).
