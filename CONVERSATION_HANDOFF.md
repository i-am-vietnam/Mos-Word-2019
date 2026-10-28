# Bàn giao toàn bộ cuộc trò chuyện — MOS Word 2019 Trainer

Ngày tổng hợp: **28/10/2026 — Asia/Bangkok**. Mục đích: mở cuộc trò chuyện mới mà vẫn giữ đúng phạm vi, kiến trúc, các quyết định và tiến độ. Đây là bản tóm tắt có chọn lọc, không phải transcript nguyên văn hay yêu cầu tự chạy lại tất cả công việc cũ.

## 1. Đọc ngay: trạng thái mới nhất và cách tiếp tục

**Repository Word:** `C:\Users\HUYNH HAU\source\repos\MosWord2019`. Solution: `MosWord2019.slnx`. Luôn đặt working directory Git/build tại đúng repository này vì thư mục cha là repository Excel độc lập.

Snapshot đã kiểm tra ngày28/10:

| Hạng mục | Trạng thái |
|---|---|
| Nhánh | `main` |
| HEAD | `78007dfb97adc6bb30d346f696c23327eb39b7b3` — `Bo sung Project 25` |
| Parent | `8c04b49634db01d64ab0cc2f95cb833e5190d437` — `Sua loi p24` |
| Origin | `https://github.com/i-am-vietnam/Mos-Word-2019.git` |
| Đã hoàn thành theo checkpoint của cuộc trò chuyện | **P01–P24:191 task,68 assertion identifiers** |
| P25 | **Mới có scaffold**, chưa triển khai/chưa được chấp nhận là project hoàn thành. |
| Thay đổi có sẵn trước khi viết bản bàn giao | Bốn file câu hỏi P22/P23 EN/VI chưa commit, liệt kê bên dưới. |

Các thay đổi có sẵn phải bảo toàn:

```text
MosWord2019.WinForms/Projects/Word2019_P22/lang/en.json
MosWord2019.WinForms/Projects/Word2019_P22/lang/vi.json
MosWord2019.WinForms/Projects/Word2019_P23/lang/en.json
MosWord2019.WinForms/Projects/Word2019_P23/lang/vi.json
```

Không có bằng chứng trong yêu cầu hiện tại cho phép hoàn tác bốn file này. **Không tự restore vì thấy chúng khác Git hoặc vì có quy tắc không sửa câu hỏi.** Chỉ ghi nhận, giữ nguyên và đối chiếu yêu cầu mới. Yêu cầu trước đây khôi phục câu hỏi P24 đã được thực hiện và commit; không được mở rộng nó sang thay đổi người dùng mới tạo ở P22/P23.

P25 hiện có `meta.json`, `starter.docx`, `lang/en.json`, `lang/vi.json`; `tasks.json` mới có bốn record T01–T04 thiếu title/instruction/assertion metadata. Các copy entries cho năm file đã có trong WinForms csproj. Có folder assets được khai báo, chưa có cơ sở để tự tạo assets/bài mới. Không đếm bốn scaffold record này là bốn task thực đã hoàn tất. Chưa build/validate/GUI-accept P25 trong lượt tóm tắt này.

**Không có yêu cầu triển khai P25 trong tin nhắn tóm tắt.** Chờ nhiệm vụ cụ thể ở cuộc trò chuyện mới; nếu được yêu cầu làm P25, đọc đúng câu hỏi/nguồn thật, hoàn tất metadata/grading theo phạm vi, giữ P01–P24 và bốn file đang sửa nói trên.

## 2. Tài liệu và nguồn sự thật

Đầu phiên đọc theo thứ tự:

1. `AGENTS.md` — quy tắc bắt buộc.
2. `PROJECT_STATUS.md` — checkpoint/tiến độ và lịch sử có ngày.
3. `KNOWN_ISSUES.md` — giới hạn đã xác nhận.
4. `CODEX_CONTEXT.md` — hợp đồng kỹ thuật chi tiết.
5. `PROJECT_CONTEXT.md` — tổng quan tiếng Việt gồm năm phần: Tổng quan, Kiến trúc & Thư viện, Database/Model, Quy tắc code/đặt tên, Tiến độ.
6. Bản bàn giao này và source trực tiếp liên quan đến yêu cầu mới.

Yêu cầu hiện tại của người dùng quyết định phạm vi; source thực tế thắng mô tả kỹ thuật cũ khi có xung đột. `PROJECT_CONTEXT.md` được lập ngày26/10 khi HEAD còn8c04b49 và mới hoàn thành P24. Việc tài liệu đó ghi24 project không có nghĩa P25 vừa thêm đã được hoàn thành hoặc bị thiếu do lỗi tải.

Các file “Huong dan làm Project XX.txt”, tài liệu Word gốc, starter và ảnh là **bằng chứng bài tập**, không phải quyền thay đổi kiến trúc hay thực hiện lại toàn bộ các tác vụ mô tả trong đó. Các file `Pasted text.txt` người dùng đưa ở các giai đoạn trước là yêu cầu của giai đoạn tương ứng; yêu cầu mới và các quyết định sau đó có thể đã thay thế chúng.

Không dựa vào bản tóm tắt để khẳng định một file/ảnh/evidence vẫn tồn tại ở checkout khác. Kiểm tra lại trước khi dùng.

## 3. Nguyên tắc người dùng đã chốt, tuyệt đối giữ

- Phần mềm **luyện thi MOS Word 2019**, WinForms C#/.NET Framework4.7.2, Microsoft.Office.Interop.Word. Không framework/UI migration, nâng dependency, viết lại hoặc cleanup ngoài phạm vi.
- Làm project mới **không đụng package, câu hỏi hoặc hành vi chấm đã được chấp nhận của project cũ**. Chỉ sửa project cũ/shared behavior khi người dùng yêu cầu rõ và phải hồi quy phần bị ảnh hưởng.
- **File câu hỏi là bất biến:** không viết lại, dịch lại hoặc thêm workflow hints vào `en.json`, `vi.json` hay `vn.json` nếu nguồn dùng tên đó. Repository hiện dùng `vi.json`. Ghi hướng dẫn kỹ thuật trong MD/evidence thay vì câu hỏi.
- Không sửa starter/packaged assets/supplied originals. Thử nghiệm trên bản sao dưới repository Word, thường là ignored `artifacts/`; không dùng file cá nhân làm fixture.
- Assets được chép vào Documents trước khi mở bài. Khi kết thúc project/phiên hoặc thoát bình thường, xóa **chỉ bản sao do service tạo trong phiên và còn đúng hash/file identity**. Bảo vệ file có sẵn, đã sửa/thay thế, đang khóa và tài liệu không thuộc phiên. Không xóa theo tên/hash suy đoán.
- Không tự chèn đáp án, cập nhật TOC, chạy Inspector hoặc đổi Word preferences trong Grade. Không fake PASS hoặc chấm theo click history.
- Giữ phân biệt **Pass / Fail / Error**; lỗi lưu/COM/context/metadata không được giả thành “người học làm sai”.
- Không kill Word người dùng hoặc Word có tài liệu chưa xác minh sở hữu. Không đóng process chỉ dựa trên PID/tên/tiêu đề.
- Excel chỉ là tham khảo kiến trúc: không sửa package/source/Git/installer và không build/clean Excel.
- Không tự pull/reset/restore/commit/push, không ghi đè dirty worktree. Các commit mới trong lịch sử đã xuất hiện do người dùng/bên ngoài phiên trợ lý; trợ lý không tự tạo chúng.
- No Testing/timer/scoring/database/submission/installer work nếu chưa có yêu cầu mới. Testing tương lai phải dùng working copies tách biệt theo **SessionId**.

Người dùng không muốn bị hỏi xác nhận lặp lại khi đã cho phép đúng phạm vi. Tiếp tục làm các bước an toàn/đã được ủy quyền; chỉ hỏi khi thật sự thiếu thông tin hoặc cần vượt ranh giới quyền. Nếu skill/automatic approval review chặn, nêu đúng hành động và lý do, tiếp tục phần độc lập hoặc cách an toàn hơn; không vòng qua chặn.

## 4. Kiến trúc, Interop và luồng phải nhớ

### 4.1. Các project và model

| Project | Trách nhiệm |
|---|---|
| `MosWord2019.WinForms` | `Program.Main [STAThread]` → `LoginForm` → `MainForm`; chọn Training/ngôn ngữ, mở bài, task tabs, Grade/Restart/điều hướng. Namespace UI hiện là `MosWord2019`. |
| `MosWord2019.Core` | Models/interfaces, `WordGradingService` và các partial assertions, diagnostics. Saved-content grading thuần OOXML. |
| `MosWord2019.Word` | `WordController`, internal `WordSession`, `WinApiProcessHelper`, partial Save/format/view/TOC context. |
| `MosWord2019.Projects` | `ProjectLoader`, `ProjectValidator`, `TrainingWorkspaceService` + Assets/SavedCopy, template output cleanup. |
| `MosWord2019.Data` | Mới là khung; **chưa có database, SQL schema, connection string, ORM hay persistence kết quả**. |

Thư viện chính: C#7.3, .NET Framework4.7.2, WinForms, Office/Word COM references, Newtonsoft.Json13.0.4, System.IO.Compression/XML.Linq và Windows safe handles/PInvoke. Không suy package version từ assembly version13.0.0.0 rồi tự nâng dependency.

Package nguồn: `MosWord2019.WinForms/Projects/Word2019_Pxx/{meta.json,tasks.json,starter.*,lang/en.json,lang/vi.json,assets/}`. Runtime nạp `Projects` cạnh executable; copy entries `PreserveNewest` phải đủ và không trùng.

Models chính: `ProjectMeta`, `ProjectPackage`, `TaskDefinition` với `[JsonExtensionData] Extra: IDictionary<string,JToken>`, `TaskGradeResult/TaskGradeOutcome`, `ValidationResult/ValidationIssue`, `TrainingGradeContext`. `AppSession` chỉ giữ Mode/Language; không phải authentication/exam session.

`ProjectValidator` kiểm tra cấu trúc/keys/required assertionType/asset options, **chưa tự xác nhận mọi identifier được Core hỗ trợ hoặc mọi metadata/OOXML semantics**. Khi kiểm chứng phải đối chiếu thêm `IsAssertionTypeSupported`. Validator/workspace nhận `.docm` không đồng nghĩa Training UI hỗ trợ nó; runtime hiện `.docx` và legacy `.doc`.

### 4.2. Word ownership và COM

- Controller tạo dedicated Word bằng `new Application()`, không GetActiveObject/attach Word cá nhân; một instance sở hữu, tối đa một document đang chỉnh sửa.
- Mọi COM call trên đúng STA thread đã tạo controller; không đưa COM sang Task.Run/thread pool/Core.
- Sở hữu process được chứng minh bằng caption token, loại trừ PID cũ, cửa sổ OpusApp, image/creation time và kernel handle giữ suốt phiên. Fallback terminate chỉ dùng handle đã xác minh.
- RCW tạm giải phóng trong finally; không FinalRelease một alias của Application/Document chủ sở hữu còn cần dùng. Từng có lỗi fixture do release nhầm Application; đây là lỗi harness đã sửa, không phải lý do sửa ownership production.
- Không mở starter để sửa; kiểm tra writable/lock. `CloseDocument` discard đúng tài liệu sở hữu; thêm document lạ trong cùng application làm chặn reset/chuyển an toàn.
- `Close`/Dispose bảo vệ tài liệu lạ hoặc trạng thái không xác minh được; không force-kill để làm sạch kết quả test.
- Word trên, panel Trainer dưới; không thêm Save/Save Close controls. Không thay global security/privacy/display preferences.

### 4.3. Training, Save/Grade và optional context

Go/repeated Go/switch/relaunch luôn fresh starter. Điều hướng và Grade giữ active-session edits. Restart No giữ bài; Yes discard/reset đúng project hiện tại/chọn task đầu. Manual Word close được phát hiện ở thao tác tiếp theo. Close/reset lỗi phải hủy transition, giữ state để retry, không mở bài stale.

MainForm gọi owned Save/Save As flow rồi `WordGradingService.CheckTask(savedPath, task, context)`. Dispatch theo assertionType/metadata, không ProjectId/TaskId. Core không tạo/attach Word hoặc mang COM object.

- Ordinary Grade giữ cùng owned PID; không tạo grading instance mới.
- File-existence tasks có ngoại lệ không tự Save.
- P16 TXT chấm bằng observed owned path/save format; rich checks dùng retained DOCX, không parse TXT như ZIP hoặc phục dựng formatting đã mất.
- `IWordViewState`/`WordFormattingMarksSnapshot` đọc bảy cờ view fresh cho P22-T07; không reset preference từ starter; thiếu state là Error.
- `IWordTocLayout`/`WordTocLayoutSnapshot` đọc bookmark text và adjusted page number từ already-owned document trên STA, chỉ khi `TableOfContentsLevels/requireRefreshedEntries=true`. Core so plain data với saved OOXML; thiếu/wrong-document/incomplete context là Error.
- Không suy trang hiện tại từ app.xml Pages, lastRenderedPageBreak hoặc số trang cố định. Không tự Update/Repaginate TOC khi Grade.

Working root: `<Documents>/MosWord2019/Working/<ProjectId>/work.docx` hoặc `work.doc`. Reset xử lý đúng các reserved work/checkpoint names qua containment/reparse/lock checks và atomic replacement; không xóa arbitrary exports.

Assets ownership ledger theo service/session: CreateNew, SHA-256, Windows file identity và exclusive deletion handle. Default cleanup hiện áp dụng cả khi không có assetStaging manifest. P12 còn additional Pictures copy. Modified/locked/replaced/pre-existing files được bảo vệ; crash cleanup không được hứa tự phục hồi.

P06/P07 template output cleanup là contract riêng, không phải owned-assets ledger. P09 Memo.txt là external output giữ nguyên qua reset/exit. P20-only Notes.dotx được giữ, nhưng phiên đã ghé P07 giữ registry cleanup exact Notes.dotx; overlap này đã kiểm chứng, không tự sửa.

## 5. Tóm tắt tiến trình toàn bộ cuộc trò chuyện

P01–P10 là nền tảng đã có trước chuỗi công việc chính; các thay đổi P10 đã được phê duyệt phải giữ. Những mô tả cũ về resume bài qua switch/relaunch đã bị quy tắc fresh-session từ P15 thay thế.

| Giai đoạn | Kết quả/điểm quyết định phải giữ |
|---|---|
| **P11** | Triển khai bài sponge-cake mới8 task, không phục hồi bản Skill Review19 task lỗi thời. Tracked changes phải theo đúng source; chấm TOC/style/symbol/spacing bằng assertion generic. Người dùng yêu cầu cập nhật AGENTS/PROJECT_STATUS/CODEX_CONTEXT/KNOWN_ISSUES để bàn giao. |
| **P12** | 8 task và safe asset lifecycle; stage bốn ảnh vào Documents và Map.png thêm vào Pictures; ownership/hash/file-identity/rollback/conflict/reparse safeguards. Người dùng bổ sung guide để tham khảo. Default cleanup opt-in của thời điểm này về sau đã được sửa theo yêu cầu toàn phần mềm, xem mục6. |
| **P13** | 6 task thực, reused assertions/metadata, giữ nguồn và lifecycle. Guide chỉ là evidence. |
| **P14** | 6 task: style set, merge table, bullets/indent, footnote và phân loại Accept text/Reject formatting revisions theo source. Không Accept All thay yêu cầu; giữ nguyên phần còn lại của tài liệu. |
| **P15** | 6 task và sửa fresh-session logic Go/switch/relaunch/Restart. Người dùng yêu cầu Computer Use kiểm chứng và khởi tạo lại. Lần đầu helper lỗi; lần sau chạy được native F5, sáu EN tasks đúng, VI representative đúng/sai; một số More Symbols/native VI coverage vẫn chưa đủ. |
| **P16** | 8 task; thêm owned TXT provenance cho Outline for Proposal.txt. Không áp đặt thư mục output, không xóa TXT khi reset/exit. Starter đã là modern format nên T01 baseline Pass là đúng, không ép Convert. |
| **P17** | 6 task, kiểm chứng layout/định dạng và backward compatibility. Windows từng bị khóa; người dùng mở khóa để tiếp tục GUI. T04 current-starter Fail và historical Square Pass là khác biệt đã xác minh, không sửa starter/đổi contract để baseline qua. |
| **P18** | **7 task**, không nhầm thành6; triển khai theo actual source, giữ approved behavior của P01–P17. |
| **P19 ban đầu** | Từng có6 task và native/API/GUI evidence. Người dùng tự Enable Editing cho các bản sao Protected View; không coi đó là quyền tự vượt Protected View cho file khác. |
| **P20 + sửa inventory P19** | P20 có11 task. Theo yêu cầu rõ, **loại P19-T02**, còn đúng T01/T03/T04/T05/T06; không đánh lại ID/khôi phục ba cột từ guide cũ. Notes.dotx overlap P07 được giữ. |
| **P20-T04 false negative** | Ảnh người dùng Convert Text to Table4x2, Tabs, Fixed width Auto là đúng. Native GUI bỏ `tblLayout`, API mẫu cũ ghi fixed; thêm opt-in `allowOmittedTableLayout` chỉ cho P20-T04. Omitted/fixed Pass, explicit-autofit Fail; không bỏ các kiểm tra dữ liệu/vị trí/source. |
| **P21** | 12 task. Accessibility Checker máy này không có table recommendation; người dùng chọn rõ **Header Row cho bảng Name/Name**, ghi giới hạn checker. Giữ Blue/Accent1=4F81BD. T12 chỉ Velociraptor level3, peers2/2/2; không tái hiện correct-state false negative nên không nới grader phỏng đoán. |
| **P22** | 7 task, Subject dùng dc:subject, real SmartArt, conversion/sort/footnote và fresh nullable view context cho chỉ Tabs. Reader chỉ đọc owned Word; thiếu state là Error. P21-T12 native3/2/2/2 tiếp tục Pass, changed peers Fail. |
| **P23** | 6 task; privacy category document-local, native Clear Formatting Arial11, bookmark toàn heading, table Alt Text chính xác. Người dùng xác nhận Remove All **Document Properties and Personal Information** trên bản sao; native Reinspect/save/reopen giữ đúng, không xóa Custom XML tùy tiện. |
| **P24** | 6 task thực, Introduction asset immutable, grouped quote, refreshed TOC có owned layout, inserted exact2 paragraphs, real page background và Inspector đúng category. Thêm3 assertion generic, tổng68, giữ defaults cũ. Chi tiết mục6. |
| **Rà soát nguyên tắc** | Người dùng cấm sửa câu hỏi/project cũ và yêu cầu assets cleanup khi thoát. Hoàn tác câu hỏi P24 theo baseline080ca9d, sửa default-owned cleanup, cập nhật4 MD. Không sửa P01–P23 trong lượt này. |
| **P24-T02 false negative** | Thêm opt-in Page-relative và chữ trên band theo ảnh, giữ chính xác các guards/native assembly. Chi tiết mục6. |
| **Tài liệu ngữ cảnh** | Đã tạo `PROJECT_CONTEXT.md` đủ5 phần, liên kết từ startup reading order; kiểm tra source/package không đổi. Người dùng commit thành quả cùng scaffold P25 ở HEAD hiện tại. |
| **Yêu cầu hiện tại** | Tạo chính file `CONVERSATION_HANDOFF.md` để mở chat mới; không tự triển khai P25 hay chỉnh bốn file câu hỏi đang dirty. |

## 6. Những quyết định mới nhất cần hiểu chính xác

### 6.1. Sáu task P24

1. **T01:** Narrow, bốn lề720twips trên tất cả relevant sections. Không đóng băng section count/page count; mirror/bookfold hoặc chỉ đúng một section không hợp lệ.
2. **T02:** real Banded Quote group trong cover body, exact `MOS, 2021`, cả nhóm Centered/Bottom với wrapping. Hai biến thể người dùng đã cho phép ở mục6.3.
3. **T03:** cập nhật existing live TOC toàn bảng, giữ scope/location, đúng effective headings/order/bookmark/link/page. Source cache18 tiêu đề cũ nhưng actual current scope chỉ có một heading Proposal; không coi generated TOC paragraphs/empty heading section-break là source headings. Thay bố cục/import có thể cần người học Update entire table lại.
4. **T04:** exact hai đoạn Introduction trước chữ P của heading **Proposal for Tan’s Office**, giữ curly apostrophe, paragraph boundary và meaningful leading tab của đoạn2. Đọc từ metadata lấy theo asset immutable, không phụ thuộc staged file còn tồn tại sau import. Không tự chèn hoặc sửa asset để pass.
5. **T05:** document page background Gray/Accent3/Lighter80%, native `EDEDED`/accent3/tint33 với theme accent3=A5A5A5; không thay bằng paragraph shading/rectangle/theme recoloring. Chỉ chấp nhận RGB equivalence đã có evidence, sai explicit theme vẫn Fail.
6. **T06:** Remove All đúng **Headers, Footers, and Watermarks**; khác category privacy của P23. Resolve active/inherited default/even/first stories, cho empty/unreferenced leftovers hợp lệ. Giữ cover-body quote, body/table/graphics/Custom XML/core properties và các task độc lập. Không xóa body office.com theo string search.

Các additions: `TextBoxAssemblyWithPosition`, `BodyContentBeforeHeading`, `DocumentBackgroundColor`; opt-ins trên `TableOfContentsLevels` và `HeadersFootersWatermarksRemovedByState`. P05 legacy Inspector comment/source/creator/hash guards không được loại toàn cục.

P24 reference identities lịch sử đã xác nhận, cần kiểm tra lại nếu sử dụng:

| File | SHA-256 | Git blob |
|---|---|---|
| P24 starter / supplied Project24 | `04E8F22808094B4FDCF337899676ED3C905C0F101436E335F9CDBDFF31256B58` | `d9aae972c57977e8c54719bbdd4c5c2ac2e07d4f` |
| Introduction.docx packaged/supplied | `6957474248A1193C2A34B6CBB41B798546F66C80AB94F9537D46FBDB0D990EC1` | `0859a7f8333dea6a0850bf4527fee4662cf368c7` |

Nguồn ngoài repo đã được cung cấp: guide `Huong dan làm Project 11.txt` đến `Huong dan làm Project 24.txt` trên Desktop; riêng P24 original/Introduction nằm trong bộ Word2019 Associate/Practice3_word/Gmetrix Templates trên D-drive. Production không được phụ thuộc đường dẫn D-drive. Nếu thiếu independent reference, dùng tracked package nhưng ghi rõ khoảng trống đối chiếu.

### 6.2. Khôi phục câu hỏi và đổi cleanup mặc định

Người dùng chỉ rõ phần mềm luyện thi không được sửa câu hỏi hoặc bài cũ khi triển khai project mới. Lượt P24 trước đó đã thêm wording/hints; **P24 EN/VI đã khôi phục nguyên byte từ commit080ca9d** theo yêu cầu. Chỉ giữ notes kỹ thuật trong MD. Các mô tả lịch sử “đã clarify EN/VI” không phải lệnh thêm lại các thay đổi đó.

Cleanup cũ gắn `Created && Cleanup` với Cleanup phụ thuộc có assetStaging manifest, nên default Introduction còn sau thoát. Đã bỏ điều kiện opt-in; `CleanupProjectAssets()` chạy với mọi Created entry. Dùng lại đúng hash/identity/exclusive-handle safeguards, không đổi manifests/câu hỏi/source assets. Old notes “default staging không cleanup” đã bị supersede.

Commit8c04b49 đã chứa phần khôi phục và cleanup này. Trạng thái bốn file câu hỏi P22/P23 dirty ngày28/10 là phát sinh sau đó/ngoài phạm vi lượt tóm tắt; **không tự phục hồi chúng**.

### 6.3. P24-T02 theo ảnh người dùng

Ảnh cho thấy horizontal Centered relative to Page, vertical Bottom relative to Page, `MOS, 2021` trong colored band và native placeholder còn ở dưới. Bộ chấm trước đó buộc Margin/Margin, band trống và text ở vùng dưới, gây false negatives trên mẫu Word tái tạo.

Chỉ thay `CheckTextBoxAssembly` và metadata T02:

- `allowPageRelativePosition=true`: nhận Page/Page ngoài Margin/Margin cũ, vẫn đúng Centered/Bottom. Không nhận mixed refs hoặc arbitrary offsets.
- `allowExpectedTextInBand=true` + `expectedQuotePlaceholderParagraphs`: nhận exact requested text một lần trong band nếu lower region vẫn exact native placeholder; arbitrary/duplicated text vẫn Fail.
- Legacy lower-text/empty-band vẫn Pass. Genuine two-shape group, cover anchor, wrapSquare, coherent geometry, màu/native transforms và source-preservation guards vẫn giữ.
- Cả hai flag mặc định false cho các package không opt-in. Không sửa câu hỏi/other5 P24 tasks/UI/Word ownership.

Đáp án gốc trong ảnh không còn ở working copy sau normal reset; mẫu kiểm thử được tái tạo bằng Word API rồi Save/reopen và grade qua production MainForm. Không khẳng định đã chấm lại file gốc hoặc lặp toàn bộ thao tác Ribbon của người dùng.

## 7. Bằng chứng, kiểm thử và giới hạn còn lại

**Các con số sau là kết quả lịch sử của những lượt tương ứng, không phải chạy mới ngày28/10:**

| Lượt | Kết quả có log |
|---|---|
| P24 implementation | Combined native6/6; GUI category Inspector Reinspect trước/sau reopen; F5 EN T03 và VI T01,6 tabs/T01/same PID. |
| Sửa nguyên tắc/assets | Default lifecycle13/13 (tái hiện5 failure trước sửa), P12/asset17/17, real MainForm EN/VI149/149. |
| Sửa T02-P24 cuối | Native13/13, focused29/29, real MainForm EN/VI157/157; P24 saved-state336/336 và synthetic71/71; older regressions362/362. |
| Lượt build T02 | VS MSBuild Any CPU Debug/Release Rebuild0 errors/0 warnings; source+outputs EN/VI0/0;127 package files match outputs,24 starters/29 initial hashes unchanged; cuối lượt0 Word/Trainer/test processes/locks. |
| Lập PROJECT_CONTEXT26/10 | Fresh source validation/support-check P01–P24 EN/VI0/0;191 task,68 routes;188 source/package files không bị thay đổi. Không chạy mới build/GUI. |

Ba ca external-existence P06/P07/P09 không rerun trong lượt T02 cuối: harness cũ cần tạo file trong Documents cá nhân, bị permission/automatic review chặn. Bản an toàn loại trừ đúng3 ca, chạy362; **không đổi production grader để né test** và không gắn nhãn365/365 lịch sử là kết quả mới.

Computer Use từng lỗi helper/runtime, Windows khóa, COM80070520 hoặc IDE chưa discover partial wildcard. Đã có các lượt GUI/F5 thành công sau reinitialize/unlock/reload/interactive execution context. Không coi mọi lần thất bại là bug production hoặc gọi API integration là manual GUI. Không đổi security để vượt chặn.

Có lần fixture release nhầm RCW để lại Word trống; automatic review từ chối đóng chỉ bằng PID/title. Sau quan sát UI xác minh không có document, Save/edit disabled, cửa sổ đó đã được đóng bình thường bằng Computer Use; không force-kill Word không rõ sở hữu.

Các xác nhận Enable Editing/Remove All trước đây áp dụng cho đúng bản sao/category/thời điểm; không mặc nhiên cho phép bỏ Protected View hoặc xóa dữ liệu trong original/personal file khác.

Giới hạn tiếp tục ghi rõ:

- Full native GUI/VI mọi task chưa hoàn tất; P24 có full-six native GUI English nhưng không đủ all-six native VI. Các project cũ có gaps riêng trong KNOWN_ISSUES.
- Unhandled TOC numbering/layout context → Error; không fake Pass theo saved cached pages.
- T06 cho original/full-Introduction variants để hoạt động trước T04 nên không phát hiện click history undo của task độc lập; combined T02/T04 vẫn bắt missing quote/import.
- Owned-assets ledger không persisted qua crash; modified/locked/replaced files có thể còn lại. Không xóa leftover theo tên để làm “cleanup tuyệt đối”.
- P21-T12 original failing attempt không có bytes; không nới strict target/peer rules theo phỏng đoán.
- P25 hiện chưa hợp lệ như project hoàn chỉnh, bốn câu hỏi P22/P23 đang dirty chưa được lượt này validate lại. Không nói toàn bộ source hiện có25 project đã pass.

## 8. File và công cụ để truy vết

Tài liệu/evidence chính, dùng đường dẫn relative từ repository Word:

- `PROJECT_CONTEXT.md`: toàn cảnh241 dòng, model/architecture/Interop/standards và checkpoint26/10.
- `PROJECT_STATUS.md`, `CODEX_CONTEXT.md`, `KNOWN_ISSUES.md`, `AGENTS.md`: tiến độ/hợp đồng/quy tắc.
- `artifacts/p24/REPORT.md`: implementation P24 trước các policy corrections; đọc banner superseded.
- `artifacts/p24/principles-audit/REPORT.md`: khôi phục câu hỏi và cleanup.
- `artifacts/p24/t02-fix/REPORT.md`: T02 final fix, positives/negatives, full logs/limits/diff/integrity.
- `artifacts/p20-t04-fix/REPORT.md`, `artifacts/p21/REPORT.md`, `artifacts/p21-t12-fix/REPORT.md`, `artifacts/p22/REPORT.md`, `artifacts/p23/REPORT.md`.
- Evidence các phase trước trong `artifacts/p11-new`, `p12`, `p13`, `p14`, `p15`, `p16`, `p17`, `p18`, `p19`, `p20`. Phải kiểm tra existence trước khi khẳng định dùng được.

MSBuild đã dùng trên máy: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`. Build đúng Word solution Debug/Release Any CPU. Python runtime từng dùng nằm dưới `.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe` của user; kiểm tra lại trước khi chạy. Dùng `rg` tìm đúng phần source, không quét/đổi toàn repo không cần thiết.

Các helper trong artifacts là fixture, **không phải production tool chung**: có script chứa baseline hard-coded, script khôi phục câu hỏi hoặc tạo/deleting external fixtures. Đọc trước khi chạy; không rerun mù `PrinciplesAudit.py`, `Regression.py` hoặc runner có side effects. Dùng isolated roots, giữ nguyên user files và dirty worktree.

## 9. Trạng thái lượt tóm tắt này

Chỉ tạo bản bàn giao và ghi một chỉ dẫn ngắn trong PROJECT_STATUS; không triển khai P25, không sửa câu hỏi/code/starter/assets, không build hay thao tác Word/Excel, không commit/push. Đã kiểm tra HEAD/status/origin, scaffold/copy entries P25 và mốc lịch sử cần thiết. Source snapshot phục vụ đối chiếu nằm trong ignored `artifacts/conversation-handoff/`.

Không tự động cập nhật PROJECT_CONTEXT.md thành “25 project hoàn thành”. Bản bàn giao ngày28/10 là snapshot mới hơn về Git/P25/dirty files; các số kiểm thử trước đó vẫn giữ ngày/provenance của chúng.

## 10. Đoạn mở đầu có thể dán vào cuộc trò chuyện mới

> Đây là dự án MOS Word 2019 WinForms tại C:\Users\HUYNH HAU\source\repos\MosWord2019. Hãy đọc CONVERSATION_HANDOFF.md và các tài liệu theo thứ tự ở mục2, kiểm tra lại Git/source trước khi tiếp tục. Giữ các nguyên tắc không sửa câu hỏi/project cũ, starter/assets bất biến, owned Word STA/COM và cleanup an toàn. P01–P24 là mốc hoàn thành; P25 ở HEAD78007df mới là scaffold. Có bốn file câu hỏi P22/P23 đang dirty, không tự restore hoặc ghi đè. Không suy yêu cầu mới từ task lịch sử hay tự làm P25/Testing/database; thực hiện nhiệm vụ cụ thể tôi đưa tiếp theo trong phạm vi đó. Tách rõ kiểm chứng mới với evidence lịch sử, và không commit/push nếu chưa được yêu cầu.
