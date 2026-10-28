# Tài liệu Ngữ cảnh dự án — MOS Word 2019 Trainer

Cập nhật: **28/10/2026** (Asia/Bangkok). Tài liệu dành cho lập trình viên và trợ lý đọc trước khi tiếp tục công việc, tổng hợp từ mã nguồn hiện tại và các quyết định đã được người dùng chấp thuận.

Thứ tự đọc đầu phiên: `AGENTS.md` → `PROJECT_STATUS.md` → `KNOWN_ISSUES.md` → `CODEX_CONTEXT.md` → tài liệu này → các file nguồn liên quan trực tiếp. Yêu cầu hiện tại của người dùng quyết định phạm vi; khi tài liệu mô tả khác mã nguồn, kiểm tra mã nguồn và sửa tài liệu có liên quan. Không tự coi mô tả lịch sử là yêu cầu mới.

Vai trò tài liệu: `AGENTS.md` chứa quy tắc bắt buộc; tài liệu này chứa bức tranh tổng thể; `CODEX_CONTEXT.md` chứa hợp đồng kỹ thuật chi tiết; `PROJECT_STATUS.md` chứa tiến độ; `KNOWN_ISSUES.md` chứa vấn đề/giới hạn đã xác nhận. Cập nhật phần tương ứng khi hệ thống thay đổi, không biến tài liệu này thành nhật ký từng thao tác.

## 1. Tổng quan

### 1.1. Mục đích và phạm vi sản phẩm

Phần mềm Windows Forms luyện thi **MOS Word 2019**, cho người học thực hành trực tiếp trên Microsoft Word desktop thật. Trainer hiển thị câu hỏi, điều hướng task và kiểm tra kết quả trong tài liệu đang làm.

- Hiện triển khai **Training**, hỗ trợ giao diện/câu hỏi **English (`en`) và Tiếng Việt (`vi`)**.
- `LoginForm` hiện chỉ chọn chế độ/ngôn ngữ, không phải đăng nhập tài khoản hay xác thực.
- `AppMode` có giá trị `Testing`, nhưng giao diện chỉ cho chọn Training; `MainForm` từ chối Testing vì chưa triển khai.
- Chưa có thi có tính giờ, tổng điểm, nộp bài, lưu lịch sử vào database hoặc installer/release workflow hoàn chỉnh.
- Có **25 package P01–P25, tổng196 task**, không áp đặt mọi project phải có cùng số task.

Solution là `MosWord2019.slnx`, chương trình khởi động từ `MosWord2019.WinForms/Program.cs`. Repository Word độc lập với repository Excel ở thư mục cha. Origin kỳ vọng: `https://github.com/i-am-vietnam/Mos-Word-2019.git`. Luôn chạy Git/MSBuild với thư mục làm việc là root repository Word; Excel chỉ là tài liệu tham khảo kiến trúc, không sửa hoặc build/clean Excel.

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
| `MosWord2019.Projects` | `ProjectLoader`, `ProjectValidator`, `TrainingWorkspaceService` và các partial SavedCopy/Assets, `TemplateOutputCleanupService`. |
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

`MainForm.GradeCurrentTask()` gọi `WordGradingService.CheckTask(savedPath, task, context)`. Dispatch theo **`AssertionType` + `TaskDefinition.Extra`**, không theo ProjectId/TaskId. Hiện có **69 assertion identifiers**; tái sử dụng assertions/opt-in hiện có trước khi thêm route mới.

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

- Package gốc nằm ở `MosWord2019.WinForms/Projects/`; runtime đọc `Projects` cạnh executable. `.csproj` copy từng file bằng `PreserveNewest`; thêm file phải bảo đảm inclusion/copy đúng một lần, không sửa trực tiếp bin để coi là sửa nguồn.
- Working root mặc định: `<Documents>/MosWord2019/Working/<ProjectId>/work.docx` hoặc `work.doc` theo starter. `PrepareWorkingCopy()` là entry point tương thích nhưng vẫn gọi fresh reset.
- Reset xác minh containment/reparse/lock, phục hồi starter qua file tạm và atomic replace/move; chỉ xử lý đúng tên reserved `work.doc`, `work.docx`, `work.docm`, `export-checkpoint.docx`. Không xóa đệ quy hoặc đụng arbitrary exports.
- Assets top-level được stage vào Documents qua `TrainingWorkspaceService.Assets.cs`; P12 có thêm bản Map.png trong Pictures qua metadata. Không tự chèn assets vào tài liệu.
- **Quy tắc hiện tại:** mọi bản sao do service tạo trong phiên được cleanup khi kết thúc project/phiên hoặc thoát bình thường, kể cả package không có `assetStaging`. Chỉ xóa bản sao còn đúng hash và Windows file identity, qua exclusive handle. Bản có sẵn cùng hash được tái sử dụng nhưng không nhận sở hữu; khác hash thì chặn, không ghi đè.
- File đã sửa/thay thế/khóa và dữ liệu cá nhân phải được bảo vệ. Crash hoặc cleanup lỗi có thể để lại file; không nhận quyền xóa từ tên/hash của file tồn từ phiên trước. Ghi log, không hứa cleanup tuyệt đối sau force-stop.
- Đừng nhầm staged assets với output bài tập. P09 Memo.txt không có cleanup giống assets. P06/P07 có `TemplateOutputCleanupService` riêng, xóa đúng tên template đã đăng ký sau khi Word đóng an toàn; registry tên được giữ qua chuyển project. Notes.dotx của P20 có thể bị quy tắc P07 tác động nếu đã ghé P07 trong cùng phiên; đây là hành vi đã kiểm chứng, không tự sửa.

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

`ProjectValidator.Validate()` kiểm tra folder/meta/starter/JSON/task IDs, sự hiện diện của `assertionType`, language keys và asset options trước khi package được nạp. Validator hiện chưa tự đối chiếu identifier với tập assertion hỗ trợ; cần kiểm tra riêng bằng `WordGradingService.IsAssertionTypeSupported()` khi kiểm chứng package/routing. Nó cũng chưa xác minh toàn bộ nội dung OOXML hoặc mọi metadata riêng của assertion. `ProjectLoader.LoadAll()` bỏ package có validation errors, sắp theo ProjectId; với tập hiện tại kết quả là Project1–25. Không tự nhận một package chắc chắn chấm được chỉ vì loader nạp thành công.

### 3.3. Model đang dùng

| Model / vị trí | Các trường và mục đích chính |
|---|---|
| `ProjectMeta` — Core/Models | `ProjectId`, `OfficeVersion`, `Version`, `Starter`, optional `AssetStaging`. |
| `AssetStagingOptions` / `AssetDestinationCopy` | `CopyAssetsToDocuments`, `AdditionalCopies` (`Source`, `Destination`), `CleanupPolicy`. Cleanup owned default assets hiện không đòi manifest opt-in. |
| `TaskDefinition` — Core/Models | `TaskId`, `TitleKey`, `InstructionKey`, `AssertionType`; `ProjectId` được gán khi load, không serialize. `[JsonExtensionData] IDictionary<string,JToken> Extra` giữ metadata assertion; không mở rộng schema cứng cho từng project. |
| `ProjectPackage` — Core/Models | `Meta`, `List<TaskDefinition> Tasks`, `Dictionary<string,string> Lang`, `ProjectFolderPath`; `DisplayName` suy từ ProjectId. |
| `AppSession` — WinForms | Immutable `Mode`, `Language`; chỉ lựa chọn shell, không phải tài khoản hoặc exam session, chưa có SessionId thi. |
| `TrainingGradeContext` — Core/Models | `DefaultTemplateFolder`, `SavedFromCurrentWorkingDocument`, `OwnedDocumentPath`, nullable `OwnedDocumentSaveFormat`, `FormattingMarks`, `TocLayout`. Context do owner cung cấp, không tin provenance tự khai trong task JSON. |
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
| **Tổng25 project** | **196 task** |

Training đã có package discovery/validation, EN/VI, task tabs, điều hướng, fresh-start/reset, owned Word lifecycle, Save/Save As khi Grade, assets staging/cleanup và69 generic assertions. Trạng thái này không đồng nghĩa mọi task ở mọi project đã được kiểm thử đầy đủ qua GUI tiếng Việt; xem giới hạn riêng trong KNOWN_ISSUES.md.

### 5.2. P25 và các hợp đồng P24 được giữ

P25 hoàn thành đúng T01–T05: Double480/auto cho toàn bộ main story với effective inheritance; genuine Strong riêng cụm Environmental School Project trong đoạn Introduction đã khai báo; native Banded header chỉ ẩn ở trang đầu tài liệu, vẫn hiện trên References; Expanded2pt (+40) cho toàn đoạn thứ hai dưới Suggestions; Next Page section boundary ngay trước References, chấp nhận native paragraph-end/empty-boundary và omitted default type. Các opt-in giữ defaults cũ; route generic mới là `SectionBreakBeforeHeading`. Không đổi câu hỏi/starter/assets, UI/Interop/lifecycle hay P01–P24.

Kiểm chứng mới28/10: matrix123/123, MainForm/owned Word API EN/VI91/91, hồi quy dùng fixture lưu lại693/693 (gồm P24 saved-state336 và T02 variants29); Debug/Release Rebuild0 lỗi/0 cảnh báo; source/hai outputs EN/VI0/0,25 package/196 task. Giữ nguyên50 dictionaries,25 starters,6 assets theo hash đầu phiên;132 package files khớp mỗi output. Full manual GUI/F5/VI và Office build khác chưa được khẳng định. Chi tiết: `artifacts/p25/REPORT.md`. Các kết quả26/10 và P24 bên dưới giữ ngày/provenance lịch sử.

P24 hoàn thành sáu task: Narrow margins toàn tài liệu; Banded Quote `MOS, 2021` dưới giữa bìa; cập nhật TOC thật với headings/links/page hiện tại; chèn đủ hai đoạn Introduction trước `Proposal for Tan’s Office`; Page Color Gray/Accent3/Lighter80%; Inspector xóa đúng Headers/Footers/Watermarks.

Các quyết định mới nhất phải giữ:

- Câu hỏi P24 đã khôi phục nguyên byte theo baseline trước triển khai `080ca9d`; toàn bộ48 dictionaries khớp baseline. Không thêm lại hướng dẫn mở rộng vào câu hỏi.
- Cleanup hiện áp dụng cho mọi owned staged copy, không chỉ P12 có manifest. Giữ các hàng rào file cá nhân và kiểm tra hash/identity.
- **T02-P24:** sau phản hồi bằng ảnh, `allowPageRelativePosition` nhận Page/Page Centered/Bottom ngoài Margin/Margin cũ. `allowExpectedTextInBand` nhận `MOS, 2021` trong dải màu nếu vùng dưới còn exact native placeholder khai báo trong `expectedQuotePlaceholderParagraphs`. Mặc định hai opt-in là false; mixed references, sai chữ, fake/mất dải màu/sai anchor hoặc geometry vẫn Fail. Không suy rộng thành nhận mọi textbox hoặc nội dung tùy ý.
- T03 dùng snapshot layout thật, không cố định trang2 hoặc số trang đã quan sát. T06 là category header/footer/watermark, **không phải** privacy category của P23.

Giữ các mốc tương thích: P05 Inspector guards; P06/P07 templates và P07 legacy conversion/reset; P09 Memo; P12 ownership; P16 owned TXT; P19 năm ID ổn định; P20-T04 native omitted-layout/explicit-fixed Pass và explicit-autofit Fail; P21-T07 Name/Name Header Row được người dùng cho phép, T12 chỉ target level3 và peers2/2/2; P22 view snapshot/DC subject/SmartArt; P23 privacy/Clear Formatting/bookmark/Alt Text. Không mở lại giả thuyết nới grader cũ khi chưa có đáp án sai bị chấm nhầm. P17 current-starter T04 Fail/historical Square Pass là khác biệt có bằng chứng, không tự sửa starter.

### 5.3. Bằng chứng kiểm chứng và giới hạn

**Kiểm tra mới khi lập tài liệu này:** đối chiếu các project/nguồn, xác nhận191 task và68 routes; chạy lại ProjectValidator cho source P01–P24 EN/VI: **0 lỗi, 0 cảnh báo**, discovery đúng Project1–24. Data hiện không có implementation database. Đây là lượt rà soát/tạo tài liệu, không chạy lại toàn bộ GUI/build.

**Lượt sửa T02 trước đó**, có log tại `artifacts/p24/t02-fix/REPORT.md`: native13/13, T02 cases29/29, production MainForm/Word EN/VI157/157, P24 saved-state336/336, synthetic71/71; hồi quy cũ362/362. Ba ca tồn tại file P06/P07/P09 không chạy lại vì fixture cần ghi vào Documents cá nhân; không gắn nhãn tổng lịch sử365/365 là lượt chạy mới. Debug/Release Any CPU Rebuild khi sửa T02 đều0 lỗi/0 cảnh báo; package output127 file khớp source và24 starter/29 hash bất biến không đổi.

Giới hạn còn phải nhớ:

- Đáp án gốc trong ảnh lỗi T02 đã không còn sau reset; kết quả mới dựa trên mẫu Word native tái tạo + MainForm, không khẳng định đã chấm lại chính file đó.
- Chưa kiểm chứng toàn bộ sáu thao tác P24 bằng GUI Word tiếng Việt; một số project trước còn giới hạn native GUI riêng. Native/API/synthetic không thay thế lẫn nhau.
- Các biến thể Office build, inactive header serialization và page-number format chưa hỗ trợ phải ghi giới hạn; không đoán để trả Pass. Context cần thiết không có là Error.
- Assets khóa/đã sửa/đã thay thế hoặc crash có thể còn lại; ownership ledger hiện không lưu bền vững. Không tự thêm cleanup recovery xóa theo tên.
- Evidence `artifacts/` bị ignore; thiếu evidence ở máy mới phải nói rõ và tái tạo có kiểm soát, không gán lại kết quả lịch sử.

### 5.4. Điểm bắt đầu cho phiên tiếp theo

HEAD đầu lượt P25: `main` tại **`78007dfb97adc6bb30d346f696c23327eb39b7b3`**, “Bo sung Project 25”, parent `8c04b49634db01d64ab0cc2f95cb833e5190d437`. Phần triển khai P25 và cập nhật tài liệu hiện chưa commit. Giữ bốn dictionaries P22/P23 dirty có sẵn, phần bổ sung PROJECT_STATUS có sẵn và CONVERSATION_HANDOFF chưa track; không restore/ghi đè. Trợ lý không commit/push.

Phiên sau phải kiểm tra HEAD/status lại, đọc yêu cầu mới và xem diff trước khi chọn việc tiếp theo. Testing/database/project tiếp theo không tự trở thành phạm vi được phép chỉ vì P25 đã xong. Khi được yêu cầu tiếp tục, dùng source hiện tại và các quyết định trên làm nền, giữ nguyên nội dung câu hỏi và thành quả của project đã hoàn tất.
