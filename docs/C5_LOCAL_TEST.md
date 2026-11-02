# C5 — Chạy và kiểm thử online trên máy Windows

Hướng dẫn này dành cho owner chạy server và ứng dụng trên **cùng một máy Windows**, trước khi triển khai VPS. Mở **PowerShell 7** (`pwsh`), chuyển vào thư mục gốc `MosWord2019`. Cần .NET SDK 10, Visual Studio có MSBuild/.NET Framework 4.7.2 targeting pack, Microsoft Word 2019 desktop và PostgreSQL 16+. Nếu dùng Docker, bật Docker Desktop với Linux containers. Không dùng tài khoản admin để đăng nhập ứng dụng học viên.

## 1. Chuẩn bị database, admin và bài tập

Thực hiện lần đầu các bước 1–4 và phần **Package C2: cấu hình và import** trong [SERVER_DEV.md](SERVER_DEV.md). Các lệnh quan trọng dưới đây phải chạy trong cùng cửa sổ PowerShell dùng để khởi động server.

Nếu chưa có PostgreSQL riêng, tạo container phát triển một lần:

```powershell
$securePgPassword = Read-Host 'Mat khau PostgreSQL dev' -AsSecureString
$env:POSTGRES_PASSWORD = [Net.NetworkCredential]::new('', $securePgPassword).Password
docker run --name mosword-dev-db -d -e POSTGRES_USER=mosword -e POSTGRES_DB=mosword -e POSTGRES_PASSWORD -p 127.0.0.1:5432:5432 -v mosword-dev-pgdata:/var/lib/postgresql/data postgres:16-alpine
docker exec mosword-dev-db pg_isready -U mosword -d mosword
```

Đợi thông báo `accepting connections`. Container đã tồn tại thì dùng `docker start mosword-dev-db`, không chạy lại lệnh tạo hoặc xóa volume. Trong cửa sổ PowerShell mới, nhập lại mật khẩu cũ để đặt `$env:POSTGRES_PASSWORD`; không đặt lại mật khẩu database bằng cách tạo container khác.

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

Nếu dùng PostgreSQL sẵn có, đổi host/port/database/user cho đúng. Không in connection string. Migrate dùng dữ liệu dev riêng, không dùng database production để diễn tập.

Chỉ khi chưa có admin:

```powershell
$secureAdminPassword = Read-Host 'Mat khau owner (12-128 ky tu)' -AsSecureString
$env:MOSWORD_ADMIN_PASSWORD = [Net.NetworkCredential]::new('', $secureAdminPassword).Password
dotnet run --project server/MosWord2019.Server --no-launch-profile -- create-admin owner
Remove-Item Env:MOSWORD_ADMIN_PASSWORD
```

Nếu admin đã tồn tại, dùng tài khoản đó; lệnh trên không reset mật khẩu. Package import cần cấu hình storage/key như phần Package C2 của SERVER_DEV. Giữ **private signing key hiện có**, không tự sinh khóa thay thế để vượt lỗi chữ ký:

```powershell
$packageHome = Join-Path $env:LOCALAPPDATA 'MosWord2019.Server'
$env:Packages__StorageRoot = Join-Path $packageHome 'packages'
$env:Packages__SigningKeyPath = Join-Path $packageHome 'secrets/manifest-signing.key'
dotnet run --project server/MosWord2019.Server --no-launch-profile -- package-public-key
dotnet run --project server/MosWord2019.Server --no-launch-profile -- import-packages MosWord2019.WinForms/Projects
```

Nếu thiếu `manifest-signing.key`, làm đúng bước tạo seed32byte và ACL trong SERVER_DEV, sau đó đối chiếu public key với `server/manifest-verification.pub` và `PackageTrust.PublicKey`. Sai public key thì client phải từ chối package; chưa thể kiểm UI online tới khi owner phục hồi đúng khóa hoặc chủ động cập nhật trust và build lại theo quy trình.

## 2. Tạo HTTPS local và lấy public pin

Dùng script C5 bên dưới **thay phần HTTPS dùng SDK development certificate trong SERVER_DEV**. Script tạo certificate tự ký có SAN `localhost`, `127.0.0.1`, `::1`, EKU serverAuth và hạn một năm. PFX cùng password lưu ngoài repo dưới `%LOCALAPPDATA%/MosWord2019.Server/secrets`, ACL chỉ Windows user hiện tại. Script không cài certificate vào trust store và không in private key/password.

```powershell
$devTls = . ./server/tools/New-DevCertificate.ps1
$devTls | Format-List
```

Dấu chấm đầu lệnh là dot-source. Giữ nguyên cửa sổ PowerShell: script đặt biến môi trường **của tiến trình hiện tại** cho Kestrel. Kết quả chỉ có `CertificatePath` và `SpkiSha256`; pin là thông tin công khai, copy nguyên chuỗi Base64 kể cả dấu `=` cuối. Chạy lại script sẽ đọc cặp PFX/password ngoài repo và dùng lại pin, không tạo certificate mới hoặc ghi đè tệp hiện có. Nếu thiếu một nửa cặp hoặc certificate hết hạn, script từ chối; owner cần phục hồi cặp đúng hoặc chủ động xóa riêng các tệp certificate dev cũ trước khi tạo lại. Tạo lại sẽ đổi pin; cập nhật config/build client tương ứng.

```powershell
dotnet run --project server/MosWord2019.Server --no-launch-profile
```

Kestrel phải thông báo nghe `https://localhost:7118`. Dùng `--no-launch-profile` để launchSettings không thay URL/certificate đã đặt. Không đóng PowerShell này trong khi test. Mỗi lần mở cửa sổ mới cần đặt lại connection/package/DP key variables, rồi dot-source script trước khi chạy server. Không chạy script bằng một tiến trình `pwsh -File` riêng rồi mong biến môi trường tự truyền ngược vào cửa sổ hiện tại.

Mở [trang quản trị local](https://localhost:7118/Admin). Browser có thể báo certificate tự ký chưa tin cậy: owner kiểm certificate là `localhost` do mình vừa tạo rồi chấp nhận thủ công cho trang dev này. Có thể xem file công khai `dev-server.cer` cùng thư mục PFX; nếu muốn trust cho browser, owner tự quyết định việc trust public certificate qua giao diện Windows. Script không thay trust store. Client học viên dùng pin SPKI riêng và chấp nhận certificate tự ký chỉ khi đúng pin, hostname và thời hạn; không cần bỏ kiểm TLS. Không dùng `SkipCertificateCheck` hoặc `curl -k`.

## 3. Tạo học viên có 3 lượt

Đăng nhập web bằng `owner`, vào **Accounts → Create**, tạo tài khoản learner riêng, ví dụ `learner-c5`, mật khẩu riêng, **MaxUses=3**, chưa khóa, để trống expiry nếu không cần hết hạn. Kiểm tài khoản có `Used=0`, còn3lượt. Admin không có quyền học viên và đăng nhập admin vào client phải bị từ chối.

Giữ trang Sessions/Accounts mở để đối chiếu. Trong các bài kiểm bên dưới, **mỗi lần password login mới thành công tiêu một lượt ngay trước khi chọn chế độ**. Thoát sớm không trả lượt. Resume cùng session/cùng máy không tiêu lượt mới. Vì có nhiều bài kiểm, sau mỗi nhóm owner dùng **AddUses** để cấp thêm lượt dev; để trống ô đặt MaxUses tuyệt đối khi dùng AddUses. Không kết luận lỗi khi tài khoản hết3lượt sau ba lần login mới hợp lệ.

## 4. Cấu hình và chạy client Debug

Build client bằng Visual Studio hoặc VS MSBuild. Thay đường dẫn MSBuild nếu bản Visual Studio cài khác:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' MosWord2019.slnx /t:Build /p:Configuration=Debug
```

Đóng client trước khi sửa **file output** `MosWord2019.WinForms/bin/Debug/MosWord2019.exe.config` (dùng tên `.exe.config` thực tế trong output nếu tên assembly khác). Trong `<appSettings>`, đặt:

```xml
<add key="DevelopmentServerUrl" value="https://localhost:7118/" />
<add key="DevelopmentServerSpkiPin" value="DAN_CHUOI_SpkiSha256_VUA_NHAN" />
<add key="UseOfflineAuthentication" value="false" />
```

Chỉ thay giá trị, giữ các phần XML khác. Config output có thể bị ghi đè khi rebuild; kiểm lại sau mỗi build. Không cần sửa source App.config để lưu cấu hình cá nhân. Các key này chỉ có hiệu lực Debug. Không đặt password/token hoặc PFX password vào config.

Chạy file `.exe` trong output Debug. Chọn ngôn ngữ EN/VI, nhập learner/password rồi bấm **Đăng nhập/Login**. Khi thành công, màn hình hiển thị **Số lượt còn lại/Remaining uses**, bật chọn Training/Testing và nút đổi thành **Tiếp tục/Continue**. Chọn chế độ rồi bấm Tiếp tục. Training chọn và tự mở Project1 khi Word pre-warm xong; selector/Bắt đầu vẫn dùng để chuyển bài. Use mới luôn bắt đầu P01, không nhớ bài của use trước. Nếu mở lỗi, kiểm tra thông báo rồi bấm Bắt đầu để thử lại, không có retry tự động. Không coi bước chọn mode là lượt login thứ hai.

## 5. Bảng kiểm thủ công

Ghi lại số lượt, SessionId ở trang Sessions và trạng thái trước/sau. Dùng starter working copy do app tạo; không chỉnh starter đóng gói. Các tình huống Word/Task Manager dưới đây chỉ thực hiện với tài liệu thử thuộc phiên này; lưu tài liệu cá nhân trước khi bắt đầu.

| Tình huống | Thao tác | Kết quả cần kiểm |
|---|---|---|
| Sai mật khẩu | Với learner còn3lượt, nhập sai một lần | Báo sai thông tin/tạm khóa; vẫn còn3lượt. Không spam10lần vì có khóa tạm15phút. |
| Login đúng, Training | Login đúng, xem lượt trước khi chọn mode; chọn Training, mở bài rồi thoát bình thường | Lượt3→2 ở lần login đúng; chọn mode không trừ thêm; countdown gần04:00 tại ô giữa, đỏ và đậm, giảm theo server; thoát hỏi “Thoát sẽ kết thúc lượt sử dụng này. Bạn còn hh:mm.”; đồng ý đóng Word thuộc phiên và end, không hoàn lượt. |
| Testing và kết quả | Bổ sung lượt nếu cần; login mới, chọn Testing, bắt đầu bài, nộp bài | Có đúng một lượt mới, đề server tải được, kết quả local xuất hiện; Admin → Results có kết quả tương ứng sau upload thành công. Score/verdict do server tính lại. |
| Crash và resume Testing | Trong Testing đang chạy, ghi SessionId/lượt; Task Manager kết thúc **MosWord2019 của phiên thử**, mở lại app cùng Windows user/máy trong15phút từ liên hệ server gần nhất | Có luồng phục hồi do server cấp quyền trước local resume; SessionId/lượt giữ nguyên, không password login mới để thay thế. Thời gian app đóng vẫn tính; không reset đề/timer. Nếu Word cũ còn mở, làm theo hộp thoại phục hồi của app. |
| Mất mạng Training | Đang Training, dừng server bằng Ctrl+C; chờ monitor nhận mất kết nối | Có lớp chặn/overlay online theo trạng thái; countdown server vẫn trôi; không thể dùng việc mất mạng để cấp thêm thời gian. Khởi động lại server cùng env/key để nối lại. |
| Mất mạng Testing | Khi Testing đang chạy, dừng server rồi chờ trạng thái offline; sau đó chạy lại server | Word bị chặn theo luồng online, engine áp dụng network pause hiện có; nối lại cùng phiên, không thêm lượt. Hard cap server2giờ không được gia hạn. Không kỳ vọng overlay xuất hiện ngay: heartbeat mỗi30giây, offline sau3lỗi hoặc90giây không có reply thành công. |
| Hết lượt | Dùng learner dev mới MaxUses=0 hoặc đặt MaxUses bằng Used ở admin, rồi login mới | Báo hết lượt; không mở mode/Word và không tạo session có quyền học. AddUses để tiếp tục kiểm các nhóm khác. |
| Admin khóa | Khi phiên thử đang chạy, khóa đúng learner trên web; chờ heartbeat | App từ chối tiếp tục khi server thu hồi quyền; không cho đổi mode hoặc tự login offline. Mở khóa rồi login mới là lượt mới, không hồi sinh phiên đã kết thúc. |
| Sai pin | Đóng client, đổi một ký tự Base64 hợp lệ trong pin output Debug rồi mở/login | Báo certificate mismatch, không tiêu lượt; khôi phục pin đúng trước khi thử tiếp. |

### Thử mất mạng 20 phút (C5-FIX)

1. Build client mới và restart server bằng bản đã có C5-FIX. Server đang chạy dùng DLL cũ không tự nhận thay đổi; dừng bằng Ctrl+C ở cửa sổ server của bạn rồi chạy lại cùng biến môi trường/DB/key/certificate. Không tạo lại database hoặc khóa.
2. Login Training, ghi Used/SessionId ở admin. Chờ P01 tự mở, kiểm countdown đỏ đậm và footer không nhấp nháy. Đổi P02 bằng selector/Bắt đầu để kiểm chuyển bài vẫn hoạt động; use mới phải tự trở về P01.
3. Khi đã có heartbeat thành công, dừng server bằng Ctrl+C để giả lập mất mạng local. Giữ client và Word đang chạy. Chờ overlay/Word lock; Training countdown tiếp tục giảm. Giữ mất kết nối **20 phút**, không đóng/restart client.
4. Chạy lại cùng server/cấu hình. Trong kỳ heartbeat kế tiếp (khoảng30giây), overlay biến mất, Word dùng được; SessionId/Used giữ nguyên, countdown đã trừ20phút. Không password login lại. Lặp với Testing: timer exam dừng trong khoảng offline đã phát hiện, tiếp tục khi nối lại, không đổi đề/lượt; Training4h/Testing hardcap2h không gia hạn.
5. Trong một use dev khác, giữ mất mạng ít nhất30phút sau khi overlay xuất hiện: Training đóng Word thuộc phiên, thông báo mất kết nối30phút và về login; Testing finalize/queue như trước, upload khi mạng trở lại. Nếu tick UI bị trễ và mạng về đúng lúc đủ30phút, Training vẫn kết thúc. Cần cấp thêm lượt dev giữa các lần login mới.
6. Phân biệt crash: Task Manager kill/restart vẫn chỉ được server resume trong15phút từ liên hệ cuối. Cửa sổ active35phút/access token40phút chỉ để client còn chạy hồi phục mạng; không tăng cửa sổ crash resume hoặc thời gian học.

Bảng kiểm60tick tự động xác nhận cả Training/Testing không layout lại header/footer; nghiệm thu mắt thường cần làm trên máy đã thấy nhấp nháy. Nếu auto-open thất bại, selector và Bắt đầu phải còn dùng được; không thấy thông báo lặp mỗi giây. Xác nhận thoát trong lúc pre-warm thì app không tải/mở P01 sau đó.
Máy local dùng `localhost`: rút cáp mạng/Wi-Fi thường **không ngắt loopback**, nên không chứng minh offline bằng cách rút cáp khi server vẫn chạy trên chính máy này. Dừng/chạy lại server là cách thử local rõ ràng. Khi thử server trên máy khác/VPS mới có thể rút mạng client; chưa dùng bảng này để kết luận triển khai VPS đã xong.

Nếu mất mạng đúng lúc login, bấm thử lại trong cùng màn hình để giữ idempotency key; không chủ động mở một lần password login mới chỉ để retry response bị mất. Kiểm server chỉ tăng Used một lần cho key đó. Không in response/token/key ra console để kiểm. Khi resume bị từ chối vì idle/hết hạn/admin khóa, giữ dữ liệu kết quả local để owner kiểm; không sửa đồng hồ hoặc database nhằm kéo dài quyền.

## 6. Release: URL và pin được biên dịch vào EXE

Release bỏ qua `DevelopmentServerUrl`, `DevelopmentServerSpkiPin`, `UseOfflineAuthentication` trong `.exe.config`. Build với URL/pin công khai thật cần thử:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' MosWord2019.slnx /t:Rebuild /p:Configuration=Release /p:OnlineServerUrl=https://localhost:7118/ "/p:OnlineServerSpkiPin=$($devTls.SpkiSha256)"
```

Target build tạo `CompiledServerSettings.g.cs` trong `obj`, biên dịch URL/pin vào EXE. Không sửa tệp sinh này và không đưa bí mật vào MSBuild argument. Pin SPKI là public. Release mặc định không ship plaintext Projects; gói được tải theo session online. Nếu không truyền pin triển khai, giá trị mặc định toàn số0 không khớp certificate thật và kết nối phải bị từ chối. Đây là fail closed; chưa có pin/VPS thật thì không phát hành bản Release cho học viên.

Đổi certificate/key trên server đòi hỏi đối chiếu SPKI và build/phát hành lại client với pin tương ứng. Sửa config cạnh EXE Release không chuyển server/pin. Hướng dẫn này không triển khai nginx/VPS hoặc thay quy trình C6.

## 7. Dừng và giữ dữ liệu dev

Đóng client bình thường, Ctrl+C server; có thể `docker stop mosword-dev-db` để dừng DB mà giữ volume. Không xóa key ring, private signing key, package storage hoặc database để dọn phiên. Certificate/password được giữ ngoài repo cho lần sau. Khi không dùng nữa, xóa biến bí mật của cửa sổ hiện tại:

```powershell
Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue
Remove-Item Env:ConnectionStrings__Database -ErrorAction SilentlyContinue
Remove-Item Env:MOSWORD_ADMIN_PASSWORD -ErrorAction SilentlyContinue
```

Không chia sẻ nội dung `.secret`, PFX, connection string, cookie hoặc session/resume/upload token. Public pin và `dev-server.cer` có thể dùng để đối chiếu certificate. Kiểm thủ công cần ghi kết quả thực tế; tài liệu này không tự khẳng định các ca Word native/UI đã chạy.
