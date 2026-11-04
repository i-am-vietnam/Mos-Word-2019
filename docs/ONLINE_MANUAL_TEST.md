# C7 — Nghiệm thu online bằng Word thật

Ngày lập: 04/11/2026. Đây là **checklist để owner chạy**, không phải báo cáo Codex đã chạy Word/VPS hoặc kiểm thử xâm nhập. Owner đã xác nhận nghiệm thu production bước 1–7 của đợt triển khai trước PASS, upload kết quả HTTP 200; giữ nguyên bằng chứng đó. Các ca mở rộng dưới đây chỉ ghi PASS sau khi thực hiện và đối chiếu kết quả cụ thể.

## Chuẩn bị và ghi bằng chứng

- Dùng bản client Release đúng URL/pin/public key đã phát hành và server tương ứng. Chụp phiên bản/release ID; không chụp mật khẩu, token, private key, connection string. Dùng `VPS_IP`, `TEST_SERVER_URL` làm placeholder, không điền bí mật vào tài liệu.
- Chuẩn bị hai máy Windows có Word desktop, tài khoản học viên thử riêng cho từng ca và quyền Admin web. Ghi trước/sau: UTC, tài khoản thử (ẩn khi chia sẻ), Used/MaxUses, SessionId, chế độ, kết quả mong đợi/thực tế. Mẫu ghi: `ID | UTC | release | PASS/FAIL/CHƯA CHẠY | quan sát | bằng chứng`.
- Những ca dừng server, crash, mất mạng dài hoặc đổi chứng thư chỉ làm trên **môi trường thử riêng**. Không có học viên/phiên thật đang chạy tại đó; giữ SSH/provider console. Không diễn tập bằng sửa giờ Windows/server hoặc sửa JSON/state/token.
- Lưu tài liệu cá nhân trước khi thử crash. Chỉ kết thúc tiến trình Trainer thử đã xác định; không End task toàn bộ WINWORD. Không xóa `%LOCALAPPDATA%\MosWord2019`, không copy DPAPI sang máy/user khác, không sửa `starter.docx`, gói, đề hoặc điểm để tạo PASS.

## A. Đăng nhập, lượt và quyền

| ID | Thao tác | Kết quả cần thấy |
|---|---|---|
| A1 | Admin tạo học viên có 2 lượt. Nhập sai mật khẩu một lần, sau đó đúng. | Sai: không mở bài, không trừ lượt. Đúng: còn 1 lượt và có đúng một phiên mới trong Admin → Phiên. |
| A2 | Tạo học viên 0 lượt; thử đăng nhập đúng. | Báo hết lượt, không mở Word/bài, Used không tăng. |
| A3 | Tạo học viên hết hạn; thử đúng mật khẩu. | Báo hết hạn, không tiêu lượt. Không sửa đồng hồ để thử. |
| A4 | Admin khóa học viên; thử login, rồi trên một tài khoản thử khác đang chạy hãy khóa từ Admin. | Login bị chặn. Phiên đang chạy nhận thu hồi qua liên hệ server rồi khóa/kết thúc theo UI; không tiếp tục thao tác sau khi nhận từ chối. Used không được hoàn tự động. |
| A5 | Tài khoản đúng 1 lượt: vào Training rồi thoát bình thường (xác nhận thoát), đăng nhập mới. | Lần đầu Used=1; lần sau hết lượt. Thoát sớm không hoàn lượt. |
| A6 | Tài khoản đúng 1 lượt: trên hai máy bấm login gần đồng thời. | Chỉ một phiên mới thành công, Used=1; máy kia hết lượt. Đây là hai login mới, không phải resume bằng copy file. |
| A7 | Tài khoản đúng 2 lượt: hai máy login mới, mỗi máy chọn một mode. | Hai phiên khác SessionId; Used=2; một máy không chiếm state/bài của máy kia. Login thứ ba hết lượt. |
| A8 | Login, chọn Training (hoặc Testing); khởi động lại và resume hợp lệ. | Một SessionId chỉ có một mode; không quay lại đổi Training↔Testing. Chọn lại cùng mode do retry không tạo đề mới/tiêu thêm lượt. API mode đối nghịch cần người kỹ thuật kiểm riêng trên test: HTTP409. |
| A9 | Trên test, làm rơi phản hồi login rồi thử lại **cùng yêu cầu/idempotency key**, nhờ người kỹ thuật tái gửi yêu cầu bằng công cụ không ghi bí mật. | Trả cùng SessionId, chỉ tăng Used một lần. Đóng app rồi login mới không thay thế phép thử cùng key. Cùng key nhưng machine khác bị409. |
| A10 | Admin reset password tài khoản thử đang chạy; thử token cũ/resume cũ bằng client đang có state, rồi login mới bằng mật khẩu mới. | Token cũ không tiếp tục cấp quyền; mật khẩu cũ bị chặn. Login mới hợp lệ tiêu lượt mới. Kiểm riêng logout/login Admin, learner không vào được Admin và admin không đăng nhập learner API. |
| A11 | Trên test, thử nhiều mật khẩu sai, có khoảng cách để không chỉ chạm HTTP429. | Sau10 lỗi, password login tạm khóa15phút; lỗi không tiêu lượt. Resume hợp lệ cùng máy vẫn được phép trong15phút nếu không bị Admin khóa/hết hạn/reset credential. Không spam production. |

## B. Training

| ID | Thao tác | Kết quả cần thấy |
|---|---|---|
| B1 | Login Training mới; đợi Word chuẩn bị. Chỉnh một câu, chuyển bài rồi mở lại bằng Go/Restart theo UI. | P01 tự mở một lần; mỗi phiên/bài Training mới dùng starter sạch theo quy tắc hiện có. Đồng hồ ổn định, không tiêu thêm lượt khi chuyển bài. |
| B2 | Bấm thoát trước giờ; xem câu hỏi xác nhận, chọn Không, sau đó thử lại và chọn Có. | Thông báo có thời gian còn lại; Không giữ phiên/bài. Có kết thúc phiên, không hoàn lượt; lần login mới dùng lượt mới. |
| B3 | Duy trì mạng/heartbeat và để Training chạy đủ4giờ từ login. | Đến giới hạn server, bài khóa/kết thúc; không gia hạn bằng đóng/mở hoặc đổi giờ máy. Ghi UTC bắt đầu/kết thúc; không coi test đồng hồ giả là PASS4giờ thật. |
| B4 | Ngắt mạng test, quan sát sau khi UI nhận mất mạng; nối lại khoảng20phút từ lúc mất mạng. Lặp riêng một phiên để mất mạng đủ30phút. | Word của phiên khóa khi offline; **đồng hồ Training tiếp tục chạy**. Nối lại trước30phút cho tiếp tục nếu còn quyền. Đủ30phút phiên kết thúc với thông báo, không hồi sinh do nối mạng muộn. |
| B5 | Crash chỉ Trainer thử; mở lại cùng Windows user/máy trong15phút từ liên hệ server cuối. | Không trừ lượt mới; Training tiếp tục quyền thời gian phiên cũ nhưng bài bắt đầu từ starter sạch, không phục hồi đáp án Training cũ. |

## C. Testing, crash và mất mạng

| ID | Thao tác | Kết quả cần thấy |
|---|---|---|
| C1 | Login Testing, bắt đầu, sửa câu rồi dùng Next để lưu/khóa bài hiện tại. Ghi SessionId/Used. Crash chỉ Trainer thử, chờ xử lý owned Word theo hướng dẫn recovery, mở lại **cùng máy/user trong15phút từ heartbeat cuối**. | Đề/fingerprint, SessionId, đáp án đã lưu và trạng thái khóa bài giữ nguyên; Used không tăng. Thời gian crash vẫn tính, không coi là network pause. Bấm Next tiếp rồi Submit được; Admin → Kết quả có đúng phiên, receipt200 khớp. |
| C2 | Lặp C1 trên phiên mới, chờ **hơn15phút từ heartbeat cuối** rồi mở lại. | Server từ chối resume; chốt từ dữ liệu đã lưu (saved-only), giữ kết quả/queue, không mở starter thay đáp án để tiếp tục thi. Upload nếu còn quyền; Admin thấy kết quả/flags tương ứng. Không tự sửa thời gian/state để ép tiếp tục. |
| C3 | Mở một tài liệu Word cá nhân riêng. Trong Testing ngắt mạng; đợi UI overlay offline (có thể cần3lỗi/90giây). Thử gõ vào Word bài thi; quan sát đồng hồ rồi nối lại trước30phút. | Word **của bài thi** khóa, timer thi dừng trong khoảng offline được engine ghi nhận; tài liệu cá nhân không bị đóng/kill. Nối lại hợp lệ thì mở khóa, timer tiếp tục; Next/Submit hoạt động. Phát hiện mất mạng không tức thì, ghi cả giờ rút mạng và giờ UI khóa. |
| C4 | Một phiên mới ngắt mạng đủ30phút; giữ client chạy. | Đạt ngưỡng engine thì chốt bài theo dữ liệu lưu, không thi tiếp sau nối mạng. Giới hạn server Testing2giờ từ chọn mode vẫn có hiệu lực; offline không kéo dài hard cap này. |
| C5 | Khi online, chờ hết50phút thi; thử riêng biến thể dialog Word đang mở lúc hết giờ. | Timeout đi qua flow chốt/lưu hiện có; không cho thêm thời gian làm bài. Nếu Word/I/O chưa cho lưu, UI recovery giữ bằng chứng, không báo kết quả giả. Ghi trạng thái cụ thể, không dùng test rút ngắn giờ để xác nhận50phút thật. |
| C6 | Submit một bài VI và một bài EN, có ít nhất một câu sai. Mở Admin detail. | Điểm0–1000, đỗ từ700; có đúng câu sai/instruction theo ngôn ngữ và package version đã ghim. Không sửa payload để tạo điểm mong muốn. |

## D. Server tạm ngừng, queue và chứng thư

1. Chỉ trên server test riêng: trong Testing ghi UTC và SessionId, chạy `sudo systemctl stop mosword.service`. Tạo/giữ kết quả theo UI, xác nhận file B4 local vẫn có và queue pending. Nginx có thể trả502 khi app dừng; để thử chính xác503 ứng dụng cần môi trường fault injection test do người kỹ thuật quản lý, không làm hỏng package/key production.
2. Chạy `sudo systemctl start mosword.service`, xác nhận ready bằng lệnh ở phần F. Mở lại client/login nếu cần để thử ngay; nếu giữ client chạy, backoff tăng5giây đến1giờ nên không đòi ACK tức thì. Mong đợi mạng/429/503 giữ queue để retry; receipt hợp lệ200 đưa record vào Receipts, B4 không bị xóa và Admin chỉ có một kết quả cho SessionId đó. Giữ ACK mất/rồi retry để xác nhận không nhân đôi kết quả. Server vẫn quyết định quyền upload24giờ. HTTP401/403 được retry; chỉ khi server tiếp tục trả401/403 và giờ UTC local **lớn hơn** `completedUtc + 25giờ` mới park thành `http_401_expired`/`http_403_expired` (đúng25giờ vẫn retry). Thiếu/sai định dạng `completedUtc` thì401/403 tiếp tục retry, không suy ra hết quyền. HTTP400/404/409/413/422 park ngay; các4xx khác trừ401/403/429 cũng park. Không tự gia hạn token, sửa payload hoặc đổi giờ máy để diễn tập ngưỡng này.
3. Sai SPKI: trên **server test HTTPS riêng** dùng certificate test có SAN/hostname hợp lệ. Dùng client test riêng build ghim **public SPKI khác** bằng quy trình `build-client.ps1` trong [DEPLOY_VPS.md](DEPLOY_VPS.md); credential/profile test riêng. Không đổi pin/cert production, không dùng `--rotate` production, không tắt TLS validation. Mong đợi client báo certificate mismatch/fail closed, không mở bài và không tiêu lượt trên server vì TLS bị từ chối trước login. Client test pin đúng phải hoạt động để phân biệt lỗi pin với server không chạy. Biến thể hostname/hạn cert sai cũng phải bị từ chối dù pin đúng. Không sửa App.config để đổi trust Release.

## E. Đọc receipt/Parked mà không lộ token

Trong File Explorer nhập `%LOCALAPPDATA%\MosWord2019\ResultUploads`. Record đang chờ là `SESSIONID.upload`; ACK ở `Receipts\SESSIONID.receipt`; lỗi vĩnh viễn ở `Parked\SESSIONID.upload` và `SESSIONID.reason.json`. Các `.upload`/`.receipt` là **DPAPI CurrentUser**, chứa cả thông tin bí mật; không mở/raw dump, gửi lên chat hoặc chuyển sang máy/user khác. Không sửa/xóa/di chuyển chúng.

Đọc `Parked\*.reason.json` bằng Notepad: mong đợi chỉ `sessionId`, `reason`, `parkedUtc`. `http_400`, `http_404`, `http_409`, `http_413`, `http_422`, `http_401_expired`, `http_403_expired`, `origin_mismatch`, `invalid_record` cần owner kiểm; không tự phát lại record đã park. Receipt tồn tại chưa thay đối chiếu Admin và hash/session. B4 có thể vẫn `pendingUpload=true`; trạng thái hiệu lực nằm ở queue/receipt.

Nếu cần đọc nội dung ACK, người kỹ thuật chạy PowerShell trên **đúng Windows user/máy**, chỉ xuất trường cho phép như dưới đây. Script chỉ đọc `.receipt`, giải mã record trong bộ nhớ để lấy ACK; tuyệt đối không in object gốc, key, token, payload hoặc `receipt.sessionToken`. Không bật transcript/debug dump khi chạy. Thay `SESSION_ID_32_HEX` bằng tên file receipt đã thấy, không phải mật khẩu.

```powershell
Add-Type -AssemblyName System.Security
$receiptId = 'SESSION_ID_32_HEX'
if ($receiptId -notmatch '\A[0-9a-fA-F]{32}\z') { throw 'SessionId phải có32ký tự hex.' }
$receiptPath = Join-Path $env:LOCALAPPDATA "MosWord2019\ResultUploads\Receipts\$receiptId.receipt"
$receiptBytes = [IO.File]::ReadAllBytes($receiptPath)
$receiptPlain = $null
try {
    $entropy = [Text.Encoding]::UTF8.GetBytes('MosWord2019.ResultUpload.v1')
    $receiptPlain = [Security.Cryptography.ProtectedData]::Unprotect($receiptBytes, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $receiptRecord = [Text.Encoding]::UTF8.GetString($receiptPlain) | ConvertFrom-Json
    [pscustomobject]@{
        SessionId = $receiptRecord.receipt.sessionId
        PayloadHash = $receiptRecord.receipt.payloadHash
        Score = $receiptRecord.receipt.score
        Passed = $receiptRecord.receipt.passed
        ServerTime = $receiptRecord.receipt.serverTime
    }
} finally {
    if ($null -ne $receiptPlain) { [Array]::Clear($receiptPlain, 0, $receiptPlain.Length) }
    Remove-Variable receiptRecord -ErrorAction SilentlyContinue
}
```

Mong đợi SessionId/hash khớp B4 và Admin; lỗi DPAPI thì dừng, giữ nguyên file/profile và nhờ owner xử lý. Không thử dùng tài khoản khác để “sửa” queue.

## F. Admin và kiểm tra VPS chỉ đọc

| Trang | Thao tác | Kết quả cần thấy |
|---|---|---|
| Dashboard `/Admin` | Mở desktop và màn hình hẹp khoảng390px. | Thống kê/link đọc được, không tràn ngang che thao tác; khớp dữ liệu thử. |
| Học viên `/Admin/Accounts` | Tìm username thử, sang trang; tạo học viên, mở Edit, thêm lượt, đặt hạn/khóa, reset password; mở lịch sử phiên của học viên. | Filter/phân trang giữ điều kiện, số Used/MaxUses đúng; không sửa được tài khoản Admin qua chức năng learner. Không giảm MaxUses dưới Used. |
| Kết quả `/Admin/Results` | Lọc tài khoản/ngày VN, sang trang, mở Details từ kết quả VI/EN. | Đúng tài khoản, SessionId, điểm/verdict/flags/gaps/câu sai/instruction; ranh giới ngày hiển thị UTC+7 đúng. Chuỗi nhập không trở thành HTML/script. |
| Phiên `/Admin/Sessions` | Lọc tài khoản, phân trang; đối chiếu với lịch sử phiên của học viên. | Đúng mode, thời gian/boundaries, trạng thái và kết quả; không nhân đôi phiên do resume/retry. |
| Audit `/Admin/Audit` | Lọc tài khoản/action theo form, phân trang; đối chiếu thời gian hiển thị. | Sự kiện login/khóa/reset/resume/result đúng, không chứa mật khẩu/token/key. Username trong quản trị là PII, chỉ chia sẻ đã ẩn. |
| Logout/Login | Logout, dùng Back/URL trực tiếp mở mọi trang Admin; thử learner credential. | Yêu cầu login; cookie hết quyền không mở dữ liệu cũ. Form POST thiếu CSRF phải bị chặn (người kỹ thuật kiểm trên test). |

Owner SSH vào đúng VPS rồi chạy lệnh chỉ đọc sau; không đưa output nguyên bản chứa IP/username/path cá nhân lên nơi công khai. Ghi timestamp đối chiếu theo UTC (`Z`/`+00:00`). `journalctl --utc` xuất UTC; nginx có timezone trong từng dòng, phải quy đổi UTC trước so sánh; Admin hiển thị ngày VN UTC+7. Đối chiếu symlink và service start với stamp release vừa upload/receipt publish, không chỉ nhìn health200.

```bash
date -u +%Y-%m-%dT%H:%M:%SZ
sudo systemctl status mosword.service nginx.service --no-pager
sudo env TZ=UTC systemctl show mosword.service --property=MainPID --property=ExecMainStartTimestamp --property=ExecMainStartTimestampMonotonic
sudo journalctl -u mosword.service --utc --since '30 minutes ago' --no-pager
sudo tail -n 100 /var/log/nginx/access.log
sudo tail -n 50 /var/log/nginx/error.log
readlink -f /opt/mosword/current
curl --fail --silent http://127.0.0.1:5000/health/ready
sudo /usr/local/lib/mosword-deploy/99-verify.sh HOST_NAME VPS_IP 'sha256//PUBLIC_SPKI_BASE64'
sudo systemctl status mosword-backup.timer --no-pager
sudo stat -c '%U:%G %a %n' /etc/mosword/mosword.env /etc/mosword/migrate.env /var/lib/mosword/keys/manifest-signing.key /var/lib/mosword/dataprotection /var/backups/mosword
```

Thay `HOST_NAME`, `VPS_IP`, `PUBLIC_SPKI_BASE64` bằng public values của deployment (host và IPv4 có thể giống nhau); không dùng private key. Mong đợi service/nginx active, `readlink` là `/opt/mosword/releases/STAMP` với `STAMP` đúng bản vừa upload; PID mới và ExecMainStartTimestamp tương ứng lần triển khai, receipt có `MOSWORD_ACTIVE_RELEASE`/`MOSWORD_SERVICE_STARTED_AT` khớp. Nếu publish thiếu receipt hoặc báo mismatch thì chưa xác nhận triển khai thành công, dù health200. Mong đợi readiness thành công (HTTP redirect nếu cấu hình yêu cầu HTTPS thì dùng phép kiểm HTTPS/pin của99verify), `POST /api/results`200 sau reconnect và một kết quả Admin. Env root600; seed root:moswordsvc640; DP service700; backup directory root700/file root600. Không `cat` env/seed/DP keys. Nếu app chưa chạy và owner đang khôi phục server test, dùng `sudo systemctl start mosword.service` rồi kiểm lại; production chỉ start theo runbook owner, không restart để diễn tập.

Kết thúc: ghi từng ca PASS/FAIL/CHƯA CHẠY, giữ bằng chứng tối thiểu đã ẩn PII; xác nhận server test đã chạy lại, mạng bật lại và không còn tiến trình/bài thử do mình tạo. Không đóng tài liệu hoặc tiến trình Word của người khác.
