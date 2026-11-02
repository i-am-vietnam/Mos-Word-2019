# Kiến trúc server MOS Word 2019

## Phạm vi và kế hoạch C0

C0 tạo nền tảng server độc lập, chưa nối client online. Thực hiện theo thứ tự: kiểm tra SDK/Docker; tạo solution và kiểm thử health; thêm model/migration PostgreSQL; bootstrap và bảo vệ admin; kiểm thử persistence/bảo mật; build/test hai cấu hình; cập nhật ngữ cảnh và commit cục bộ. Không triển khai C1–C7 khi chưa được yêu cầu.

Client tiếp tục WinForms, .NET Framework 4.7.2, C# 7.3 và Word Interop. `server/MosWord2019.Server.sln` không nằm trong `MosWord2019.slnx`, không tham chiếu project client. App ASP.NET Core .NET 10 chứa cả HTTP API và Razor Pages; EF Core 10 dùng Npgsql/PostgreSQL 16+. Chọn một app và một database để phù hợp VPS 2 vCPU, 4 GB RAM, 35 GB NVMe.

## Các thành phần đã có

| Tệp/thư mục | Trách nhiệm |
|---|---|
| `server/MosWord2019.Server/Program.cs` | DI, pipeline HTTPS/auth/rate limit, health và hai lệnh CLI |
| `server/MosWord2019.Server/Data/Entities.cs`, `server/MosWord2019.Server/Data/ServerDbContext.cs` | Năm model, mapping PostgreSQL và ràng buộc |
| `server/MosWord2019.Server/Data/Migrations/` | Migration `InitialFoundation`, snapshot, Up/Down |
| `server/MosWord2019.Server/Security/AdminBootstrap.cs` | Transaction/advisory lock để tạo đúng một admin đầu tiên |
| `server/MosWord2019.Server/Security/AdminAccess.cs` | Chuẩn hóa username, PasswordHasher, quyền admin/lock/expiry |
| `server/MosWord2019.Server/Pages/Admin/` | Login, trang nền tảng được bảo vệ và POST logout |
| `server/MosWord2019.Server.Tests/` | xUnit, HTTP qua TestServer, CLI subprocess và PostgreSQL Testcontainers |

`GET /health` trả 200 khi tiến trình hoạt động, không yêu cầu DB. `GET /health/ready` trả 200 khi kết nối được DB và mọi migration đã áp dụng; 503 nếu thiếu cấu hình, mất DB hoặc còn migration. Phản hồi không chứa connection string. Health đi qua HTTPS như các endpoint khác. Không tự migrate hoặc tự tạo admin khi web khởi động.

## Dữ liệu C0

| Bảng | Nội dung/ràng buộc |
|---|---|
| `Accounts` | UUID, username chuẩn hóa duy nhất, password hash, cờ admin/lock, `MaxUses`, `Used`, expiry; `0 <= Used <= MaxUses` |
| `Sessions` | UUID, account FK, `LoginRequestId` duy nhất trong account, SHA-256 machine ID dạng hex, mode nullable hoặc Training/Testing, mốc bắt đầu/heartbeat/kết thúc |
| `Results` | UUID, một result trên session FK duy nhất, payload JSONB, mốc nhận |
| `Packages` | UUID, code/version duy nhất, SHA-256, đường dẫn lưu, kích thước không âm; chưa chứa package/key thực |
| `AuditLog` | Identity bigint, account FK nullable, action, JSONB không chứa bí mật, mốc tạo |

FK dùng Restrict để không xóa dây chuyền session/result/audit. PostgreSQL dùng `timestamp with time zone`; mặc định tạo/nhận/bắt đầu lấy `CURRENT_TIMESTAMP`. Logic expiry admin dùng `TimeProvider` của server UTC, không nhận giờ máy client. Unique index login request chỉ là nền schema; C0 chưa có API tiêu lượt hoặc bảo đảm replay nghiệp vụ của C1.

## Admin và bí mật

- `migrate` áp dụng migration rõ ràng; `create-admin <username>` đọc `MOSWORD_ADMIN_PASSWORD`, chỉ tạo admin đầu tiên. Advisory lock trong transaction chống hai tiến trình bootstrap cùng lúc. Không reset/promote tài khoản trùng; admin đã tồn tại thì từ chối.
- Username 3–64 ký tự ASCII thuộc chữ/số/`._-`, chuẩn hóa uppercase invariant; mật khẩu bootstrap 12–128 ký tự, không toàn khoảng trắng. PasswordHasher ASP.NET Core Identity PBKDF2, 210.000 vòng; không lưu mật khẩu thô.
- Cookie admin có tiền tố `__Host-`, Secure, HttpOnly, SameSite Strict, hạn 30 phút, không tự gia hạn. Mỗi request có cookie kiểm tra lại quyền admin, lock và expiry từ database. Admin web login không phải lượt sử dụng của học viên, không tạo session hoặc tăng Used.
- Razor Pages áp authorization cho toàn bộ `/Admin`, chỉ Login anonymous; POST login/logout dùng antiforgery mặc định. Logout GET không thay đổi trạng thái. Lỗi xác thực dùng chung thông báo.
- Rate limit mỗi IP: POST Login 5/phút, chọn theo metadata endpoint Razor Pages để cả `/Admin/Login/` và khác hoa/thường dùng cùng hạn mức; các request admin khác 60/phút, không xếp hàng, vượt trả 429. C1 sẽ thêm giới hạn riêng cho API đăng nhập học viên. Limiter nằm trong bộ nhớ một app; chưa cấu hình reverse proxy/forwarded headers ở C0.
- Connection string lấy từ `ConnectionStrings__Database`; bootstrap password từ biến môi trường; Data Protection key ring dùng vị trí hệ điều hành bên ngoài repo hoặc `DataProtection__KeyPath` trỏ thư mục ngoài repo có quyền hạn chế. Không đưa private key TLS, key ký/mã hóa package, backup hoặc secret config vào Git. `.gitignore` bổ sung mẫu loại trừ; đây không thay thế việc giữ bí mật ngoài repo.

## Hợp đồng Phase C đã chốt, chưa triển khai trong C0

- C1: login học viên trừ đúng một lượt bằng UPDATE có điều kiện và RETURNING trong transaction; idempotency key bảo đảm retry không trừ thêm. Chọn mode một lần sau login. Login máy khác là lượt riêng. Resume cùng machine bằng resumeToken trong 15 phút, không trừ lượt.
- C2: zip AES-GCM/key từng package; manifest ký Ed25519; seed đề do server cấp, dùng lại ExamComposer hiện có. Client giải mã vào LocalAppData với ACL riêng người dùng, journal quản lý và cleanup. Release bỏ Projects, Debug/dev có đường local.
- C3: heartbeat 30 giây; offline sau 3 lần lỗi hoặc 90 giây. Training 4 giờ theo server, đồng hồ vẫn chạy khi offline và Word bị khóa. Testing giữ cơ chế pause/offline hiện có; crash downtime vẫn tính. Chờ mạng tối đa 30 phút rồi kết thúc; kết quả Testing chờ upload lại.
- C4: nhận result B4 có HMAC bằng key từng session; upload idempotent/retry; lưu flags chênh lệch thời gian và trang kết quả admin.
- C5: thay `OfflineAuthenticationService` sau `IAuthenticationService`, giữ login UI U1. Chỉ bắt đầu pre-warm Word sau login thành công. Training thoát sớm vẫn kết thúc lượt, xác nhận “Thoát sẽ kết thúc lượt sử dụng này. Bạn còn hh:mm.” Testing giữ 50 phút, 7 project, 34–40 task, 1000 điểm, đạt từ 700.
- C6: Ubuntu 24.04 LTS, PostgreSQL/nginx/systemd/ufw/backup/rotation; không domain, TLS self-signed và SPKI SHA-256 pin compile trong client. Release không cho App.config thay pin; từ chối mọi certificate khác, không tắt validation toàn cục. C0 chỉ dùng HTTPS development certificate cho chạy local.
- C7: review bảo mật và checklist nghiệm thu online. Chưa tuyên bố server C0 sẵn sàng public trên VPS.

Không sửa grader, engine, recovery, journal hoặc tài nguyên bài tập trong C0. Các cột/token/key và migration bổ sung sẽ đi cùng bước có nghiệp vụ tương ứng, không giả lập API của các bước sau.

## Tài liệu gốc

- [EF Core 10 và Npgsql](https://www.npgsql.org/efcore/release-notes/10.0.html).
- [Antiforgery ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).
- [PostgreSQL Testcontainers](https://dotnet.testcontainers.org/modules/postgres/).
