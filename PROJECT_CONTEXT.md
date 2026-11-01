# Tài liệu Ngữ cảnh dự án — MOS Word 2019 Trainer

## U1 — Giao diện và đăng nhập offline, 2026-11-01

Login có tài khoản/mật khẩu, radio mode/ngôn ngữ và IAuthenticationService thay thế được ở Phase C; file login-preferences.txt chỉ giữ username/ngôn ngữ. Không có server/xác thực thật. Routing và startup recovery giữ nguyên. Training/Testing có tab số thứ tự, chữ20/18pt cuộn dọc; Testing đồng hồ24pt đỏ giữa thanh trên, Submit xanh, review ở thanh dưới. Panel tối đa35% working area. Result/rules/progress tăng cỡ chữ. Checkbox B5 được thay bằng Ctrl+Shift+F12 chỉ trong Testing Debug khi focus Trainer; Release không có handler/simulator. Không đổi nghiệp vụ, timer hoặc grader.

Debug/Release:844/844 mỗi cấu hình,0warnings/errors; giữ806case cũ, thêm38case. Ảnh EN/VI và ma trận scale mô phỏng ở artifacts/u1. Chưa chạy lại Word thật hoặc DPI native sau U1; không mở rộng bằng chứng B5 sang U1. Dừng U1, Phase C chưa triển khai.

## B5 — Checklist chấp nhận và công tắc offline Debug

`docs/TESTING_MANUAL_TEST.md` giữ nguyên biên bản10bước Word thật do người dùng xác nhận: ngày30/10/2026 bước1–4 PASS lượt đầu; bước5 FAIL lượt đầu rồi PASS sau B4-FIX; bước5–10 PASS sau sửa trên Word16Click-to-Run, DESKTOP-4968L2S, Debug/VI (bước10 mở English). Không coi đây là lượt Codex tự chạy lại. Checklist7bước B4-FIX được giữ thành mục riêng và đối chiếu với bước5–10; có regression set tối thiểu. Full50phút, Font tại timeout, offline giả và resume sau15phút vẫn CHƯA CHẠY native.

Testing Debug có checkbox `DEBUG: Giả lập mất mạng`/`DEBUG: Simulate offline` ở hàng nút ngoài overlay. Monitor giả riêng từng bài đi qua host STA và cơ chế pause/lock/submit có sẵn; không đổi Core, grader hay Training. Release không chứa monitor giả/lệnh/công tắc; vẫn AlwaysOnlineMonitor. Không có heartbeat thật/uploader/chữ ký mới; Phase C chưa triển khai.

## B4-FIX — Đóng/mở lại Word và khôi phục phiên

Bản sửa theo lỗi Word thật ngày30/10/2026: khi học viên đóng Word bằng X, Testing nhả COM của Application cũ, chỉ chờ process sở hữu tối đa10giây mỗi lượt rồi mở instance mới từ checkpoint; ba lần thử có backoff, hết lượt vẫn retry ở tick sau. Application đã retire không còn quyền terminate tự động, bảo vệ tài liệu khác nếu process chưa thoát. Timer và provenance Save As/legacy conversion được giữ.

Warning dùng mã sự kiện; chỉ coordinator.NeedsRecovery mới chuyển UI sang recovery. PID đã thoát/không đọc được identity không làm startup chết và không được terminate. Startup và mở Testing cùng có Retry/Nộp từ dữ liệu đã lưu/Later khi Inspect lỗi; nộp saved dùng checkpoint/autosave, không mở Word, lưu kết quả trước giải phóng phiên. Later không cố Prepare lên active state. Không đổi grader, task/catalog/package. Đây là mốc triển khai B4-FIX trước khi người dùng báo cáo kiểm chứng native; kết quả tiếp theo và các ca còn thiếu được ghi ở mục B5 phía trên.

## B4 — Lưu kết quả và retry ghi atomic

Mỗi bài hoàn tất lưu Results/<SessionId>.json trước cleanup cuối, có seed/definition/hash nội dung, outcome/điểm từng task, tổng điểm, mốc UTC, lịch sử offline theo đồng hồ đơn điệu và pendingUpload=true. Không ghi chẩn đoán grader hay thông tin ownership/COM vào kết quả. Ghi lại cùng dữ liệu là idempotent; dữ liệu xung đột hoặc lỗi ghi giữ nguyên state để khôi phục. Chỉ xóa state sau khi kết quả đã bền vững; archive cũ không tự migrate, thiếu lịch sử được đánh dấu rõ.

State, results, journal và checkpoint/working-copy dùng chung helper atomic: tối đa10 lần, tổng chờ1,98giây cho lỗi32/33/1175/1176/1177; kiểm lại đường dẫn và không ghi đè trái quyền. Hết retry chỉ log một dòng tổng kết. Hộp task sai bật xuống dòng/scroll dọc. Không đổi grader, package, catalog hay hành vi Training ngoài retry I/O đã được yêu cầu. Chưa có uploader/ký kết quả/thời gian server (Phase C); B5 chưa làm và Word thật vẫn cần kiểm chứng riêng.


## B3 — Giao diện Testing EN/VI

Đã bật Testing ở Login, màn hình luật/Start giữ seed khi retry preflight, panel task ở dưới Word, đồng hồ đỏ từ05:00, đánh dấu xem lại, Restart hiện tại, Next một chiều và Submit có xác nhận. Khi offline, ẩn task và hiện overlay/nút nộp; timeout khóa input rồi tự chấm. Result hiển thị tổng/1000, kết luận, điểm từng project và instruction đầy đủ của task sai; không lộ chẩn đoán grader.

Resume mở trực tiếp theo ngôn ngữ đã lưu và tính lại downtime lúc gắn STA. Hộp tiến trình chấm không chặn message pump. Word bận không làm hỏng phiên; deadline retry30giây rồi dùng xác minh PID/creation/image có sẵn và chấm checkpoint chỉ đọc. Lỗi cleanup giữ kết quả bền vững/journal; lỗi không khôi phục được trong phiên có đường quay về startup recovery. Đóng lúc Start/Resume còn chạy cũng áp dụng thoát có giữ dữ liệu. Không đổi Training, grader, catalog hay package. B4 đã bổ sung lưu kết quả như trên; B5 chưa làm, cần kiểm chứng Word thật, không suy PASS từ UI fake.


## B2 — Engine toàn phiên và startup recovery

Đã nối host STA nền có message pump, autosave60giây, heartbeat5giây, Next một chiều, Restart project hiện tại, Submit/timeout/offline và chấm project chưa mở từ starter. Kết quả được lưu trước cleanup; chấm recovery từ checkpoint/context đã lưu. Đồng hồ đơn điệu vẫn chạy khi đóng/mở lại Word; chỉ mất mạng mới pause. Không Save lại output sau Save As.

Trước khi học viên đóng Word, sự kiện owned DocumentBeforeClose ghi checkpoint WordOpenXML/context bền vững; chỉ thay working copy sau khi document đóng để tránh khóa file. Native path/format và baseline được giữ qua reopen/resume. Khi startup, phải xác nhận trước khi dừng Word còn sót đã xác minh PID/creation/image; từ chối giữ phiên pending. Phiên hết hạn được chấm/lưu/archive trước journal recovery. Clock rollback trên2phút được log và lưu cờ audit; thời gian server/chữ ký vẫn thuộc Phase C.

B3 đã bật Testing và gắn trực tiếp host/form khi chấp nhận resume; không chạy bài thi ẩn sau Training. Thời gian chờ này vẫn tính từ heartbeat cũ. P22-T07/P24-T03 giữ nguyên loại trừ, không thay catalog/package. Kiểm chứng Word thật (close/save-as/dialog/crash/orphan/full50phút) sẽ làm sau B3; test fake/OOXML/HWND độc lập không thay thế kiểm chứng đó.


## B1 — Sinh đề và tính điểm thuần Core

Đã thêm ExamComposer/ExamDefinition, PRNG SplitMix64 cố định, ScoreAllocator và ExamScorer/ExamResult; chưa nối UI hoặc session. Composer chọn project trước task, tôn trọng catalog B0, tối đa một legacy.doc/conflictGroup, bảy project và34–40task. Bộ chuẩn bị sao chép metadata; đầu ra giữ thứ tự task và instruction key để B3 tra đúng ngôn ngữ phiên. Điều kiện xung đột ảnh hưởng tần suất biên, nhưng không dùng số tổ hợp task để tăng/giảm trọng số project. Golden test giữ hợp đồng tái lập seed; không tự đổi catalog đang được duyệt.

Điểm theo thứ tự project/task của definition, stream riêng theo seed. Fail/Error/thiếu kết quả nhận0; Error và task thiếu kết quả gửi callback log nội bộ, không có chẩn đoán trong ExamResult. Tổng project cộng thành tổng bài; từ700 là Passed. B3 mới hiển thị Đạt/Pass hoặc Cần Luyện Tập thêm/Needs more practice và instruction đầy đủ từ khóa đã lưu.

| N | Điểm cơ bản | Số task được thêm1 |
|---|---|---|
|34|29|14|
|35|28|20|
|36|27|28|
|37|27|1|
|38|26|12|
|39|25|25|
|40|25|0|


## B0 — Catalog Testing

Catalog riêng `MosWord2019.WinForms/Projects/testing-catalog.json` chứa28 entry; Core có TestingCatalogEntry, Projects có TestingCatalogLoader/Validator. Loader kiểm schema và constraints trước khi trả dữ liệu; không nối vào ProjectLoader Training. Validator dùng tập con theo nhóm và DP kiểm tính khả thi7project/34–40task/conflict/legacy; không sinh đề hoặc chấm điểm. Rationale và các điều kiện B2: docs/TESTING_CATALOG_REVIEW.md. Checklist A4 hiện được tracked ở docs/MANUAL_TEST.md. Training và149 file package gốc giữ nguyên; wildcard copy thêm đúng catalog.


Cập nhật: **29/10/2026** (Asia/Bangkok). Tài liệu dành cho lập trình viên và trợ lý đọc trước khi tiếp tục công việc, tổng hợp từ mã nguồn hiện tại và các quyết định đã được người dùng chấp thuận.

Thứ tự đọc đầu phiên: `AGENTS.md` → `PROJECT_STATUS.md` → `KNOWN_ISSUES.md` → `CODEX_CONTEXT.md` → tài liệu này → các file nguồn liên quan trực tiếp. Yêu cầu hiện tại của người dùng quyết định phạm vi; khi tài liệu mô tả khác mã nguồn, kiểm tra mã nguồn và sửa tài liệu có liên quan. Không tự coi mô tả lịch sử là yêu cầu mới.

Vai trò tài liệu: `AGENTS.md` chứa quy tắc bắt buộc; tài liệu này chứa bức tranh tổng thể; `CODEX_CONTEXT.md` chứa hợp đồng kỹ thuật chi tiết; `PROJECT_STATUS.md` chứa tiến độ; `KNOWN_ISSUES.md` chứa vấn đề/giới hạn đã xác nhận. Cập nhật phần tương ứng khi hệ thống thay đổi, không biến tài liệu này thành nhật ký từng thao tác.

## 1. Tổng quan

### 1.1. Mục đích và phạm vi sản phẩm

Phần mềm Windows Forms luyện thi **MOS Word 2019**, cho người học thực hành trực tiếp trên Microsoft Word desktop thật. Trainer hiển thị câu hỏi, điều hướng task và kiểm tra kết quả trong tài liệu đang làm.

- Hiện triển khai **Training**, hỗ trợ giao diện/câu hỏi **English (`en`) và Tiếng Việt (`vi`)**.
- `LoginForm` hiện chỉ chọn chế độ/ngôn ngữ, không phải đăng nhập tài khoản hay xác thực.
- `AppMode` có giá trị `Testing`, nhưng giao diện chỉ cho chọn Training; `MainForm` từ chối Testing vì chưa triển khai.
- Chưa có thi có tính giờ, tổng điểm, nộp bài, lưu lịch sử vào database hoặc installer/release workflow hoàn chỉnh.
- Có **28 package P01–P28, tổng212 task**, không áp đặt mọi project phải có cùng số task.

Solution là `MosWord2019.slnx`, chương trình khởi động từ `MosWord2019.WinForms/Program.cs`. Repository Word độc lập với repository Excel ở thư mục cha. Origin kỳ vọng: `https://github.com/i-am-vietnam/Mos-Word-2019.git`. Luôn chạy Git/MSBuild với thư mục làm việc là root repository Word; Excel chỉ là tài liệu tham khảo kiến trúc, không sửa hoặc build/clean Excel.

A1 bổ sung `MosWord2019.Tests`: project MSTest classic net472/C#7.3, chạy VSTest không cần Word, nạp task gốc qua `ProjectLoader`. Fixture được lưu trong Git, có Pass/Fail cho77 loại assertion đang dùng và các near miss cho context/output. Constructor nội bộ được duyệt cho phép truyền riêng thư mục Documents của từng test; constructor công khai giữ nguyên. WinForms copy package bằng một wildcard `Projects\**\*` với PreserveNewest. Hướng dẫn chạy: `MosWord2019.Tests/README.md`.

A2 chuyển ngoại lệ chấm bài không nghiêm trọng thành Error có chẩn đoán, kiểm tra metadata TXT, từ chối assertion không hỗ trợ từ bước validation, ghi log package bị loại và hiển thị số lượng bằng EN/VI. Test bổ sung đối chứng đúng/near-miss cho đủ28 task thuộc năm nhóm assertion dùng nhiều, cùng fixture Error và thử nghiệm ngoại lệ. A2 giữ kết quả dữ liệu hợp lệ; A3a bên dưới thay riêng provenance/cleanup đầu ra. A3b đã bổ sung phục hồi bên dưới; A4 bổ sung retry/đóng an toàn bên dưới; kiểm chứng Word thật vẫn tách riêng.

A3a dùng chung `TemplateFolderResolver` (PersonalTemplates theo phiên bản Office, fallback Documents/Custom Office Templates). `OutputOwnershipLedger` chụp baseline trước OpenDocument trên Go/Restart, nhận diện đúng các output khai báo của P06/P07/P09/P20; `TrainingGradeContext.OutputOwnership` truyền snapshot thuần dữ liệu vào Core. A3b chấp nhận đổi hash/identity hoặc LastWriteUtc muộn hơn; thứ tự quan sát dùng sequence tăng đơn điệu nên chỉnh lùi đồng hồ không gây Error. File cũ không đổi không còn PASS; nội dung SavedWordTemplate/PlainTextDocumentExport vẫn phải đúng. Cleanup chỉ sau khi đóng Word, với file vốn chưa tồn tại, được nhận sở hữu từ tài liệu hoặc fallback creation-time được duyệt, vẫn khớp identity/hash đầu tiên trên exclusive delete handle. File cá nhân/cũ/đổi/khóa không bị xóa. P16-T02 chọn thư mục tự do nên không thuộc cleanup. A3b ghi journal atomic tại LocalAppData/MosWord2019/session-journal.json cho asset/output/working; khởi động giữ mutex Local\MosWord2019.Trainer trước khi phục hồi bằng exclusive identity/hash handle. Claim bị khóa được giữ để thử lại. Sự kiện lưu Word truyền đường dẫn thuần dữ liệu, rồi UI tick kiểm tra đường dẫn mới; fallback cleanup chỉ nhận đúng output khai báo, baseline vắng và creation time đủ mới. Program có handler lỗi UI/domain, chỉ gọi Word trên STA sở hữu. Thoát thường xóa tên working dành riêng rồi thư mục rỗng; không xóa file tùy ý. A4 bổ sung retry/đóng an toàn bên dưới; kiểm chứng Word thật vẫn tách riêng.

A4 đăng ký OLE message filter trên STA sở hữu, retry lỗi Word bận tối đa10 giây, khôi phục filter khi dispose; lỗi được đổi thành WordBusyException để hiện đúng hướng dẫn EN/VI. Post-save có ngân sách ngắn/backoff5 giây, chỉ một dòng tổng kết khi thất bại. Đóng từ Task Manager/0x8001010D được hoãn qua BeginInvoke; Windows shutdown không gọi COM. Retry/Đóng ngay cho phép thoát; nhánh Đóng ngay/Dispose lỗi giữ journal và chặn cleanup toàn cục sau đó. Journal hỏng được MOVE atomic sang file invalid-UTC cùng thư mục, không tự phục hồi claims cũ; journal mới hợp lệ cho Go tiếp tục. MissingTranslationException chỉ dành cho thiếu key dịch. RCW của event save được release. Test desktop giả/OLE nằm ở project MSTest net472 riêng để giữ guard Core/Projects-only của test hồi quy gốc. Checklist Word thật: docs/MANUAL_TEST.md.

### 1.2. Luồng người dùng và trạng thái bài làm

```text
Program.Main [STAThread]
  → LoginForm chọn Training + en/vi
  → MainForm tải các package hợp lệ
  → Go: kết thúc phiên cũ an toàn → stage assets → tạo bản làm mới → mở Word
  → chọn task / Previous / Next → người học sửa trong Word
  → Grade: xử lý lưu phù hợp → lấy context cần thiết → CheckTask → Correct/Incorrect hoặc lỗi kỹ thuật
```

Các điểm phải giữ:

| Thao tác | Hành vi đã chốt |
|---|---|
| Go, Go lần nữa, chuyển project, mở lại ứng dụng | Bắt đầu từ starter nguyên vẹn; không khôi phục đáp án cũ. |
| Đổi task, Previous/Next, Grade | Giữ chỉnh sửa trong phiên đang hoạt động. |
| Restart → No | Giữ nguyên bài đang làm. |
| Restart → Yes | Đóng/discard đúng tài liệu sở hữu, phục hồi starter của project hiện tại, chọn task đầu tiên; giữ/re-stage assets cần thiết. |
| Đóng Training bình thường | Đóng/discard tài liệu sở hữu, reset working copy, cleanup assets có sở hữu; không tự Save chỉ vì thoát/chuyển project. |
| Người học tự đóng Word | Phát hiện qua thao tác phụ thuộc tài liệu tiếp theo, kết thúc phiên; Go tiếp theo vẫn lấy starter mới. |
| Đóng/reset lỗi | Hủy chuyển trạng thái; giữ thông tin để có thể thử lại, không âm thầm mở bài cũ hoặc báo thành công. |

Word nằm phía trên, panel Trainer phía dưới vùng làm việc. Không thêm nút Save/Save Close riêng; người học dùng Word và Grade sử dụng luồng lưu hiện có.

## 2. Kiến trúc & Thư viện

### 2.1. Công nghệ và phân chia trách nhiệm

| Thành phần | Vai trò và file cần biết |
|---|---|
| `MosWord2019.WinForms` | Giao diện và điều phối: `Program`, `LoginForm`, `MainForm`, `AppSession`. Namespace mã hiện tại là `MosWord2019`; không đổi namespace hàng loạt theo tên thư mục. |
| `MosWord2019.Core` | Model, interface, `WordGradingService` và các partial assertions, chẩn đoán. Đọc nội dung đã lưu bằng OOXML; không chứa Word COM objects. |
| `MosWord2019.Word` | Adapter Word desktop: `WordController`, `WordSession`, `WinApiProcessHelper`, các partial đọc trạng thái/context và xử lý checkpoint. |
| `MosWord2019.Projects` | `ProjectLoader`, `ProjectValidator`, `TrainingWorkspaceService` và các partial SavedCopy/Assets, `TemplateOutputCleanupService`, `OutputOwnershipLedger`, `SessionRecoveryJournal`, mutex/fatal cleanup helpers. |
| `MosWord2019.Data` | Assembly dự phòng; hiện chỉ có khung project/AssemblyInfo, chưa có database/repository/ORM. |

Nền tảng cố định: **C#7.3, WinForms, .NET Framework4.7.2**, classic `.csproj`, Visual Studio MSBuild. Không chuyển sang .NET mới/WPF/web, nâng dependency hoặc viết lại kiến trúc khi chưa được yêu cầu.

Thư viện chính:

- `Microsoft.Office.Interop.Word` và `Microsoft.Office.Core`: COM references trong project Word, `EmbedInteropTypes=true`; cần Microsoft Word desktop được cài/đăng ký COM trên Windows.
- `Newtonsoft.Json` **13.0.4**: đọc package và giữ metadata mở rộng dạng `JToken`. Assembly version trong reference có thể là13.0.0.0; đó không phải lý do tự đổi package version.
- Thư viện .NET: `System.Windows.Forms`, `System.Drawing`, `System.IO.Compression`/`FileSystem`, `System.Xml.Linq`, `System.Security.Cryptography`.
- P/Invoke Windows và `SafeProcessHandle` phục vụ xác minh sở hữu process/đặt cửa sổ; safe file handles phục vụ cleanup assets. Không thay bằng thao tác theo tên process toàn máy.

### 2.2. Interop Word: sở hữu, STA và giải phóng COM

**`WordController` là chủ sở hữu duy nhất của một Word application và tối đa một tài liệu làm việc đang chỉnh sửa.** `WordSession` chỉ lưu state nội bộ: Application, Document, process handle và DocumentPath. MainForm sở hữu/dispose controller; Core chỉ nhận dữ liệu thường.

1. `Program.Main` có `[STAThread]`; `WordController` ghi nhận thread tạo và `EnsureThread()` bắt buộc mọi lời gọi diễn ra trên chính STA đó. Không đưa COM objects qua `Task.Run`, thread pool hoặc thread khác.
2. `StartWord()` tạo `new Wd.Application()`, **không `GetActiveObject`/attach Word của người dùng**. Có thể tái sử dụng instance đang sở hữu khi còn hợp lệ, không tạo một Word mới cho mỗi lần Grade.
3. Trước activation, chụp các PID Word có sẵn chỉ để loại trừ. Gán caption token duy nhất, xác minh cửa sổ `OpusApp`, image `WINWORD.EXE` và thời gian tạo process; giữ kernel handle trong suốt vòng đời. PID đơn lẻ hoặc tiêu đề cửa sổ không đủ chứng minh quyền đóng/kill.
4. Trên instance sở hữu, đặt `AutomationSecurity=ForceDisable`, `DisplayAlerts=wdAlertsNone`; mở working copy writable, không thêm Recent Files, không tự repair. Không thay global Trust Center, quyền bảo mật hay preference của Word khác.
5. `OpenDocument()` từ chối mở trực tiếp file tên starter; preflight file lock/read-only trước khi mở. Runtime Training hiện mở `.docx` và legacy `.doc`. Validator/workspace nhận thêm `.docm`, nhưng điều đó **không có nghĩa Training UI hỗ trợ `.docm`**.
6. RCW tạm như Documents/Range/View/Window/TOC/Bookmark/Hyperlink phải được giải phóng trong `finally` bằng helper hiện có. Phân biệt RCW tạm với Application/Document đang do session sở hữu: không `FinalReleaseComObject` một alias trỏ đến đối tượng chủ sở hữu còn cần dùng. Tránh COM enumerator/chuỗi truy cập tạo tham chiếu tạm không được quản lý.
7. `CloseDocument()` đóng đúng tài liệu bằng `wdDoNotSaveChanges`; Save là thao tác riêng. Nếu cùng application có thêm tài liệu không thuộc controller, không discard chúng và không cho reset/chuyển project tiếp tục như đã đóng thành công.
8. `Close()` unsubscribe event, đóng tài liệu sở hữu, Quit/release khi quyền sở hữu an toàn. Chỉ fallback chấm dứt process bằng **handle sở hữu đã xác minh**. Nếu không xác minh được các tài liệu còn lại, giữ Word chạy và báo lỗi; không kill theo tên `WINWORD` hoặc theo PID mở lại.

Lỗi COM/session desktop là lỗi hạ tầng, không phải đáp án sai. COM tests cần phiên Windows tương tác; các lỗi80070520/COM disconnected phải ghi bằng chứng riêng, không che bằng PASS hoặc nới grader.

### 2.3. Chấm bài và context chỉ đọc

`MainForm.GradeCurrentTask()` gọi `WordGradingService.CheckTask(savedPath, task, context)`. Dispatch theo **`AssertionType` + `TaskDefinition.Extra`**, không theo ProjectId/TaskId. Hiện có **78 assertion identifiers**; tái sử dụng assertions/opt-in hiện có trước khi thêm route mới. P28 thêm ba route `TableStyleApplied`, `TableCaptionWithSequence`, `CoverPageAssembly`; SymbolInserted thêm Unicode opt-in và SectionOrientationByAnchor thêm nhóm section theo toàn bộ nội dung gốc. Sáu task là table style native Accent2, Registered Sign đúng vị trí, Status contentStatus, caption SEQ bên dưới bảng, cover Banded xóa Address control và Landscape section sau. Bảo toàn thư/list/bảng cho phép các thay đổi độc lập và cả hai thứ tự cover/orientation. P27 và các opt-in cũ giữ nguyên. Hợp đồng ở CODEX_CONTEXT.md; evidence mới ở `artifacts/p28/REPORT.md`.

- Phần saved-content chấm bằng ZIP/OOXML, relationships, styles/theme/effective formatting, logical text. Không mở/attach Word bên trong Core, không chấm theo click history hoặc IDs/XML/runs cố định khi Word có thể tái sinh chúng.
- Grade thông thường gọi owned `Save()` và `PreserveSaveAsWork()` trước khi đọc đáp án. Save lỗi thì báo Error và giữ Word để phục hồi bài.
- Ngoại lệ đã chốt: assertion chỉ kiểm tra tồn tại output không tự Save; khi tài liệu đang là Plain Text, giữ rich working snapshot cho các rich-content checks và dùng owned path/save format cho contract TXT. Không đọc TXT như DOCX hay tự tái tạo định dạng đã mất.
- Các interface phụ là **optional**, không ép mọi fake/controller cũ phải triển khai thêm phương thức và không thay base `IWordController` tùy tiện.

| Context | Khi nào lấy và hợp đồng |
|---|---|
| `IWordViewState` → `WordFormattingMarksSnapshot` | Chỉ task `FormattingMarksVisible` (P22-T07 hiện dùng); đọc bảy cờ view từ owned document/window trên STA, mỗi Grade lấy mới. Không reset preference theo starter; thiếu/không đọc được state là Error. |
| `IWordTocLayout` → `WordTocLayoutSnapshot` | Chỉ `TableOfContentsLevels` có `requireRefreshedEntries=true` (P24-T03 hiện dùng); đọc bookmark target text và adjusted page number từ owned document sau luồng Save. Snapshot immutable, không chứa COM; path phải khớp tài liệu. |
| `IWordDocumentState`, `IWordSaveFormat` | Đường dẫn thực tế, template folder và save format để bảo toàn provenance/Save As/TXT. |
| `IWordExportCheckpoint` | Hợp đồng checkpoint có sẵn; chỉ bật khi package thật sự yêu cầu, không tự áp cho mọi project. |

Không tự Update TOC/fields, Repaginate, chạy Inspector, chèn Introduction hoặc đổi preference trong Grade. OOXML không phải engine phân trang: không suy số trang hiện tại từ `app.xml`, `lastRenderedPageBreak` hoặc danh sách số trang ghi cứng. Người học phải tự cập nhật TOC sau khi thay đổi bố cục/nội dung.

`TaskGradeOutcome` có ba giá trị độc lập: **Pass** = đáp án đúng, **Fail** = đáp án không đạt, **Error** = lỗi kỹ thuật/metadata/context. UI hiển thị Correct/Đúng, Incorrect/Sai hoặc thông báo lỗi kỹ thuật tương ứng.

### 2.4. Working copy, assets và output ngoài tài liệu

- Package gốc nằm ở `MosWord2019.WinForms/Projects/`; runtime đọc `Projects` cạnh executable. `.csproj` dùng wildcard `Projects\**\*` với `PreserveNewest`; thêm file phải bảo đảm inclusion/copy đúng một lần, không sửa trực tiếp bin để coi là sửa nguồn.
- Working root mặc định: `<Documents>/MosWord2019/Working/<ProjectId>/work.docx` hoặc `work.doc` theo starter. `PrepareWorkingCopy()` là entry point tương thích nhưng vẫn gọi fresh reset.
- Reset xác minh containment/reparse/lock, phục hồi starter qua file tạm và atomic replace/move; chỉ xử lý đúng tên reserved `work.doc`, `work.docx`, `work.docm`, `export-checkpoint.docx`. Không xóa đệ quy hoặc đụng arbitrary exports.
- Assets top-level được stage vào Documents qua `TrainingWorkspaceService.Assets.cs`; P12 có thêm bản Map.png trong Pictures qua metadata; P26 thêm alias Coffee Sam.jpg trong Documents, giữ nguyên Coffee_Sam.jpg và Information.docx. Alias đi qua cùng ledger, kiểm tra tên an toàn/collision/containment/no-overwrite/rollback và cleanup identity/hash. Không tự chèn assets vào tài liệu.
- **Quy tắc hiện tại:** mọi bản sao do service tạo trong phiên được cleanup khi kết thúc project/phiên hoặc thoát bình thường, kể cả package không có `assetStaging`. Chỉ xóa bản sao còn đúng hash và Windows file identity, qua exclusive handle. Bản có sẵn cùng hash được tái sử dụng nhưng không nhận sở hữu; khác hash thì chặn, không ghi đè.
- File đã sửa/thay thế/khóa và dữ liệu cá nhân phải được bảo vệ. Crash hoặc cleanup lỗi có thể để lại file; không nhận quyền xóa từ tên/hash của file tồn từ phiên trước. Ghi log, không hứa cleanup tuyệt đối sau force-stop.
- Staged assets giữ ledger riêng hiện có. Output khai báo dùng OutputOwnershipLedger qua TemplateOutputCleanupService; đóng Word thành công trước cleanup/reset. P20 Notes.dotx được đăng ký trực tiếp; file còn từ phiên trước không làm P07 PASS và không bị nhận sở hữu. Không scan/xóa thư mục tự chọn của P16. A3b giữ claim bị khóa trong journal; file bị đổi/thay được giữ và bỏ claim kết thúc. File ngoài đường dẫn khai báo không được scan/xóa.

## 3. Cấu trúc Database/Model

### 3.1. Chưa có database

Hiện **không có SQL schema, connection string, ORM, migration, bảng Users/Results hay lưu lịch sử thi**. Không suy diễn `MosWord2019.Data` đã có chức năng chỉ vì solution chứa project này. Dữ liệu bài tập là các package JSON + Word/assets; trạng thái Training và ledger sở hữu assets nằm trong bộ nhớ. Log chẩn đoán không phải database kết quả.

### 3.2. Package và liên kết dữ liệu

```text
MosWord2019.WinForms/Projects/Word2019_P24/
  meta.json
  tasks.json
  starter.docx
  lang/en.json
  lang/vi.json
  assets/Introduction.docx
```

`meta.json` định danh package và starter; `tasks.json` là mảng task theo thứ tự hiển thị; mỗi task trỏ tới khóa trong dictionary ngôn ngữ. Ví dụ tên khóa `P24_T02_TITLE`/`P24_T02_INST`, task ID `T02`, project ID `Word2019_P24`. Giữ nguyên ID thật của project; P19 cố ý không có T02, không đánh số lại cho liên tục.

`ProjectValidator.Validate()` kiểm tra folder/meta/starter/JSON/task IDs, identifier assertion qua `WordGradingService.IsAssertionTypeSupported()`, language keys và asset options trước khi nạp. Identifier không hỗ trợ có mã `ASSERTION_UNSUPPORTED`; validator chưa xác minh toàn bộ OOXML hoặc mọi metadata riêng của assertion. `ProjectLoader.LoadAll()` ghi log từng package bị loại cùng mã lỗi, cung cấp `LastRejectedCount` cho trạng thái EN/VI và sắp package hợp lệ theo ProjectId; hiện có Project1–28. Không tự nhận một package chắc chắn chấm được chỉ vì loader nạp thành công.

### 3.3. Model đang dùng

| Model / vị trí | Các trường và mục đích chính |
|---|---|
| `ProjectMeta` — Core/Models | `ProjectId`, `OfficeVersion`, `Version`, `Starter`, optional `AssetStaging`. |
| `AssetStagingOptions` / `AssetDestinationCopy` | `CopyAssetsToDocuments`, `AdditionalCopies` (`Source`, `Destination`, optional `DestinationFileName`), `CleanupPolicy`. Cleanup owned default assets hiện không đòi manifest opt-in. |
| `TaskDefinition` — Core/Models | `TaskId`, `TitleKey`, `InstructionKey`, `AssertionType`; `ProjectId` được gán khi load, không serialize. `[JsonExtensionData] IDictionary<string,JToken> Extra` giữ metadata assertion; không mở rộng schema cứng cho từng project. |
| `ProjectPackage` — Core/Models | `Meta`, `List<TaskDefinition> Tasks`, `Dictionary<string,string> Lang`, `ProjectFolderPath`; `DisplayName` suy từ ProjectId. |
| `AppSession` — WinForms | Immutable `Mode`, `Language`; chỉ lựa chọn shell, không phải tài khoản hoặc exam session, chưa có SessionId thi. |
| `TrainingGradeContext` — Core/Models | `DefaultTemplateFolder`, `SavedFromCurrentWorkingDocument`, `OwnedDocumentPath`, nullable `OwnedDocumentSaveFormat`, `FormattingMarks`, `TocLayout`, `OutputOwnership`. Context do owner cung cấp, không tin provenance tự khai trong task JSON. |
| `OutputOwnershipSnapshot` / `DeclaredOutputSnapshot` / `OutputFileState` — Core/Models | Snapshot immutable: session/baseline/current, đường dẫn khai báo, size/hash/UTC/identity/creation-time/monotonic sequence hoặc lỗi quan sát; Core không tự tìm output. |
| `TaskGradeResult` / `TaskGradeOutcome` | Immutable outcome/message/assertion type/task ID; phân biệt Pass/Fail/Error. |
| `ValidationResult` / `ValidationIssue` | Meta/tasks/languages đã đọc, danh sách lỗi/cảnh báo, mã lỗi và số lượng để chặn package lỗi. |
| `WordSession` — Word, internal | COM application/document và verified process handle, không đưa sang Core/UI public models. |

Diagnostics: `Core/Diagnostics/AppLogger.cs` ghi `<LocalApplicationData>/MosWord2019/Logs/training-yyyyMMdd.log`; lỗi ghi log không được làm hỏng thao tác chính. Evidence thử nghiệm lưu dưới `artifacts/` trong repository, thường bị Git ignore; link evidence có thể không tồn tại ở checkout khác, phải nói rõ nếu chưa có.

## 4. Quy tắc code & Đặt tên

### 4.1. Quy tắc bất biến và phạm vi sửa

1. Làm project mới **không sửa project cũ đã hoàn thành**, kể cả package/đáp án accepted. Chỉ sửa lại khi người dùng yêu cầu rõ; sửa shared behavior phải có giải thích và hồi quy phần bị ảnh hưởng.
2. **Không sửa nội dung file câu hỏi `en.json`, `vi.json`/`vn.json` nếu được cung cấp.** Không viết lại, thêm mẹo thao tác hay tự dịch. Ghi các phát hiện workflow vào tài liệu/evidence. Chỉ khôi phục khi được yêu cầu và nêu rõ baseline.
3. Starter và packaged assets bất biến; thử nghiệm trên bản sao. Không dùng file cá nhân làm fixture hoặc ghi đè file trong Documents. Dùng root thử nghiệm độc lập và cơ chế injected workspace hiện có.
4. Không rewrite grader hoặc tạo grader riêng cho mode mới. Dùng `WordGradingService.CheckTask`; opt-in mới phải mặc định bảo toàn hành vi cũ. Không fake PASS, chấm theo click hoặc dùng ProjectId/TaskId để rẽ nhánh.
5. Không tự pull/reset/restore/commit/push hoặc ghi đè dirty worktree. Kiểm tra Git trước khi sửa; có thể có commit mới do người dùng tạo trong lúc làm việc. Chỉ thao tác Git trong repository Word.
6. Không thêm tính năng ngoài phạm vi: database, Testing, timer, installer, migration hoặc dependency upgrade không được suy ra từ việc triển khai thêm bài.

### 4.2. Phong cách mã hiện tại

- PascalCase cho class/interface/public member/method; interface có tiền tố `I`; camelCase cho parameter/local/private field theo file hiện có. Ví dụ `TaskDefinition`, `ReadTocLayout`, `currentProject`, `taskIndex`.
- Namespace thư viện theo `MosWord2019.Core.*`, `MosWord2019.Word`, `MosWord2019.Projects`; UI hiện là `MosWord2019`. Không mass rename, format toàn repo hoặc chuyển namespace không liên quan.
- Giữ partial theo nhóm chức năng: `WordGradingService.*Assertions.cs`, `WordController.*.cs`, `TrainingWorkspaceService.*.cs`; ưu tiên helper hiện có, không thêm lớp trừu tượng/dependency nếu không cần.
- Dùng `Wd`/`Word` alias cho Interop và `Office` cho Office.Core theo file hiện có; dùng type đúng assembly, không nhầm `Word.GroupShapes` với `Office.GroupShapes`.
- JSON dùng các key hiện có như `projectId`, `taskId`, `assertionType`; key câu hỏi theo `Pxx_Txx_TITLE/INST`. Không sửa casing/ID/key để tiện xử lý.
- Comments mô tả lý do, hợp đồng sở hữu hoặc giới hạn; tên điều kiện phải thể hiện điều đang kiểm tra. Với OOXML, resolve relationship/inheritance/effective formatting, không hard-code rId/part filename/page count/shape ID theo một mẫu duy nhất.
- Dùng `using`/`try-finally`, kiểm tra đầu vào và thông báo lỗi rõ. Không biến exception đọc metadata/context/COM thành Fail của người học hoặc nuốt lỗi cleanup rồi báo đã dọn xong.

### 4.3. Quy trình trước và sau thay đổi

Trước thay đổi đáng kể: đọc context, kiểm tra Git và source liên quan; giải thích flow/method hiện tại, file dự kiến sửa, vùng được bảo vệ và kế hoạch kiểm chứng. Khi sửa chấm sai, lấy saved answer nếu có, tái hiện lỗi, viết ca thất bại trước rồi sửa tối thiểu. Ảnh chỉ chứng minh trạng thái nhìn thấy, không thay thế toàn bộ DOCX.

Sau thay đổi mã/package:

- Build Debug và Release **Any CPU** bằng Visual Studio MSBuild cho `MosWord2019.slnx`, báo mọi warning/error. Ví dụ khi `MSBuild.exe` nằm trong môi trường Visual Studio: `MSBuild.exe MosWord2019.slnx /t:Rebuild /p:Configuration=Debug /p:Platform=AnyCPU` và tương tự Release.
- Khi sửa package/routing, chạy `ProjectValidator` EN/VI cho source và output; kiểm tra copy package/asset và source inclusion đúng một lần. Core/Projects/WinForms dùng explicit Compile items; Word/Data hiện dùng wildcard có exclude bin/obj. IDE mở trước khi tạo partial wildcard có thể cần reload solution.
- Kiểm tra baseline, positive, negative, near-miss, Save/close/reopen và các tương tác liên quan. Tách rõ GUI Ribbon, Word API, synthetic OOXML và integration; không gọi API fixture là manual GUI run.
- Kiểm tra cùng owned PID khi Grade, bảo vệ Word chưa lưu độc lập, nguồn bất biến, cleanup/locks; không dọn process lạ để làm đẹp kết quả.
- Xem `git diff --check`, status và toàn bộ scoped diff; giữ thay đổi sẵn có. Cập nhật tài liệu tương ứng. Với việc chỉ sửa tài liệu, kiểm tra nội dung/link/hash nguồn và ghi rõ không chạy lại build/GUI nếu không có lý do.

Testing tương lai bắt buộc dùng working copies riêng theo **SessionId**, không tái sử dụng thư mục Training chung để thi nhiều phiên.

## 5. Tiến độ hiện tại

### 5.1. Tập bài và chức năng đã có

| Project | Số task mỗi project |
|---|---:|
| P01–P09 | 8 |
| P10 | 17 |
| P11–P12 | 8 |
| P13–P15 | 6 |
| P16 | 8 |
| P17 | 6 |
| P18 | **7** |
| P19 | **5**, ID T01/T03/T04/T05/T06 |
| P20 | 11 |
| P21 | 12 |
| P22 | 7 |
| P23 | 6 |
| P24 | 6 |
| P25 | 5 |
| P26 | 5 |
| **Tổng26 project** | **201 task** |

Training đã có package discovery/validation, EN/VI, task tabs, điều hướng, fresh-start/reset, owned Word lifecycle, Save/Save As khi Grade, assets staging/cleanup và73 generic assertions. Trạng thái này không đồng nghĩa mọi task ở mọi project đã được kiểm thử đầy đủ qua GUI tiếng Việt; xem giới hạn riêng trong KNOWN_ISSUES.md.

### 5.2. P26 và các hợp đồng P24/P25 được giữ

P26 hoàn thành đúng năm task: nhập đủ ba đoạn Information giữa note/đường cam và SmartArt; thứ tự logic Accessibility/Speed/Accuracy/Artists với ảnh/layout gốc; đúng Coffee Sam.jpg bên trong Sidebar dưới chữ, bevel Soft Round trên chính ảnh; bullets thật cho đúng ba shift, note ngoài list; watermark chữ MOS Word/Arial trong effective header story. Bốn route chung mới và opt-in preservation giữ defaults cũ, không sửa UI/Interop/lifecycle hoặc package P01–P25.

Kiểm chứng mới P26: matrix72/72, SmartArt43/43, watermark44/44, review13/13, staging/lifecycle172/172, MainForm EN/VI103/103; combined5/5 sau lưu/mở lại. Hồi quy cũ682 ca đúng trên đường dẫn giữ lại;11 ca P20 thiếu template tạm đã chạy lại11/11 bằng context cô lập, P25 thêm123/123. Debug/Release Rebuild0 lỗi/0 cảnh báo; source/hai outputs26 package/201 task EN/VI0/0. Hash giữ52 dictionaries,26 starters,8 assets;139 file khớp mỗi output và7 P26 copy entries không đổi. Native GUI/API/fixture và giới hạn được phân biệt trong artifacts/p26/REPORT.md.

P25 hoàn thành đúng T01–T05: Double480/auto cho toàn bộ main story với effective inheritance; genuine Strong riêng cụm Environmental School Project trong đoạn Introduction đã khai báo; native Banded header chỉ ẩn ở trang đầu tài liệu, vẫn hiện trên References; Expanded2pt (+40) cho toàn đoạn thứ hai dưới Suggestions; Next Page section boundary ngay trước References, chấp nhận native paragraph-end/empty-boundary và omitted default type. Các opt-in giữ defaults cũ; route generic mới là `SectionBreakBeforeHeading`. Không đổi câu hỏi/starter/assets, UI/Interop/lifecycle hay P01–P24.

Đợt P25 trước đó28/10: matrix123/123, MainForm/owned Word API EN/VI91/91, hồi quy dùng fixture lưu lại693/693 (gồm P24 saved-state336 và T02 variants29); Debug/Release Rebuild0 lỗi/0 cảnh báo; source/hai outputs EN/VI0/0,25 package/196 task. Giữ nguyên50 dictionaries,25 starters,6 assets theo hash đầu phiên;132 package files khớp mỗi output. Full manual GUI/F5/VI và Office build khác chưa được khẳng định. Chi tiết: `artifacts/p25/REPORT.md`. Các kết quả26/10 và P24 bên dưới giữ ngày/provenance lịch sử.

P24 hoàn thành sáu task: Narrow margins toàn tài liệu; Banded Quote `MOS, 2021` dưới giữa bìa; cập nhật TOC thật với headings/links/page hiện tại; chèn đủ hai đoạn Introduction trước `Proposal for Tan’s Office`; Page Color Gray/Accent3/Lighter80%; Inspector xóa đúng Headers/Footers/Watermarks.

Các quyết định mới nhất phải giữ:

- Câu hỏi P24 đã khôi phục nguyên byte theo baseline trước triển khai `080ca9d`; toàn bộ48 dictionaries khớp baseline. Không thêm lại hướng dẫn mở rộng vào câu hỏi.
- Cleanup hiện áp dụng cho mọi owned staged copy, không chỉ P12 có manifest. Giữ các hàng rào file cá nhân và kiểm tra hash/identity.
- **T02-P24:** sau phản hồi bằng ảnh, `allowPageRelativePosition` nhận Page/Page Centered/Bottom ngoài Margin/Margin cũ. `allowExpectedTextInBand` nhận `MOS, 2021` trong dải màu nếu vùng dưới còn exact native placeholder khai báo trong `expectedQuotePlaceholderParagraphs`. Mặc định hai opt-in là false; mixed references, sai chữ, fake/mất dải màu/sai anchor hoặc geometry vẫn Fail. Không suy rộng thành nhận mọi textbox hoặc nội dung tùy ý.
- T03 dùng snapshot layout thật, không cố định trang2 hoặc số trang đã quan sát. T06 là category header/footer/watermark, **không phải** privacy category của P23.

Giữ các mốc tương thích: P05 Inspector guards; P06/P07 templates và P07 legacy conversion/reset; P09 Memo; P12 ownership; P16 owned TXT; P19 năm ID ổn định; P20-T04 native omitted-layout/explicit-fixed Pass và explicit-autofit Fail; P21-T07 Name/Name Header Row được người dùng cho phép, T12 chỉ target level3 và peers2/2/2; P22 view snapshot/DC subject/SmartArt; P23 privacy/Clear Formatting/bookmark/Alt Text. Không mở lại giả thuyết nới grader cũ khi chưa có đáp án sai bị chấm nhầm. P17 current-starter T04 Fail/historical Square Pass là khác biệt có bằng chứng, không tự sửa starter.

### 5.3. Bằng chứng kiểm chứng và giới hạn

**Lịch sử kiểm tra khi lập tài liệu26/10:** đối chiếu các project/nguồn, xác nhận191 task và68 routes; chạy lại ProjectValidator cho source P01–P24 EN/VI: **0 lỗi, 0 cảnh báo**, discovery đúng Project1–24. Data hiện không có implementation database. Đây là lượt rà soát/tạo tài liệu, không chạy lại toàn bộ GUI/build.

**Lượt sửa T02 trước đó**, có log tại `artifacts/p24/t02-fix/REPORT.md`: native13/13, T02 cases29/29, production MainForm/Word EN/VI157/157, P24 saved-state336/336, synthetic72/72; hồi quy cũ362/362. Ba ca tồn tại file P06/P07/P09 không chạy lại vì fixture cần ghi vào Documents cá nhân; không gắn nhãn tổng lịch sử365/365 là lượt chạy mới. Debug/Release Any CPU Rebuild khi sửa T02 đều0 lỗi/0 cảnh báo; package output127 file khớp source và24 starter/29 hash bất biến không đổi.

Giới hạn còn phải nhớ:

- Đáp án gốc trong ảnh lỗi T02 đã không còn sau reset; kết quả mới dựa trên mẫu Word native tái tạo + MainForm, không khẳng định đã chấm lại chính file đó.
- Chưa kiểm chứng toàn bộ sáu thao tác P24 bằng GUI Word tiếng Việt; một số project trước còn giới hạn native GUI riêng. Native/API/synthetic không thay thế lẫn nhau.
- Các biến thể Office build, inactive header serialization và page-number format chưa hỗ trợ phải ghi giới hạn; không đoán để trả Pass. Context cần thiết không có là Error.
- Assets khóa/đã sửa/đã thay thế hoặc crash có thể còn lại; ownership ledger hiện không lưu bền vững. Không tự thêm cleanup recovery xóa theo tên.
- Evidence `artifacts/` bị ignore; thiếu evidence ở máy mới phải nói rõ và tái tạo có kiểm soát, không gán lại kết quả lịch sử.

### 5.4. Điểm bắt đầu cho phiên tiếp theo

HEAD đầu và cuối lượt P26: `main` tại **`a7e9673f03d1c3bd8e31fbe9b76486db48b84448`**, “Cap nhat p26”; worktree đầu lượt sạch, P25 đã có implementation. Thay đổi P26/code/docs hiện chưa commit; toàn bộ package P01–P25 và câu hỏi/starter/assets giữ nguyên baseline này. Mô tả dirty P22/P23/HEAD78007df trong handoff cũ là lịch sử. Trợ lý không commit/push/pull/reset.

Phiên sau phải kiểm tra HEAD/status lại, đọc yêu cầu mới và xem diff trước khi chọn việc tiếp theo. Testing/database/project tiếp theo không tự trở thành phạm vi được phép chỉ vì P26 đã xong. Khi được yêu cầu tiếp tục, dùng source hiện tại và các quyết định trên làm nền, giữ nguyên nội dung câu hỏi và thành quả của project đã hoàn tất.
