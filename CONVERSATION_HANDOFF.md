# Bàn giao toàn bộ cuộc trò chuyện — MOS Word 2019 Trainer

Cập nhật: **30/10/2026 — Asia/Bangkok**. Đây là bản bàn giao theo các quyết định mới nhất, không phải transcript nguyên văn hoặc lệnh thực hiện lại task lịch sử. Bản này thay thế snapshot ngày28/10 vốn còn ghi P25 scaffold và bốn file câu hỏi P22/P23 dirty.

## 1. Trạng thái hiện tại — đọc trước

- Repository Word: `C:\Users\HUYNH HAU\source\repos\MosWord2019`.
- Origin: `https://github.com/i-am-vietnam/Mos-Word-2019.git`; nhánh `main`.
- HEAD đã kiểm tra trong lượt bàn giao: **`7cf99c0` — `Persist auditable exam results and retry atomic commits`**.
- **P01–P28 đã hoàn thành; Phase A và B0–B4 đã triển khai. B5 chưa được thực hiện.**
- Inventory source vừa đếm lại: **28 package,212 task,77 assertionType được sử dụng**. Checkpoint test trước đó có78 route được hỗ trợ; kiểm tra source/test guards nếu cần dùng con số route.
- Kiểm chứng cuối B4: Debug và Release mỗi cấu hình **770/770 PASS**,0 skipped;694 headless +76 desktop. Mỗi build VS MSBuild0 warning/0 error, stderr kiểm thử rỗng.
- B4 giữ đủ749 test B3, bao gồm chuỗi hồi quy528 test Phase A. Source/Debug/Release150 tệp package/catalog khớp hash;149 tài nguyên gốc, catalog và fixtures không thay đổi trong B4.
- Trước lượt tóm tắt này working tree sạch. Lượt này chỉ cập nhật `CONVERSATION_HANDOFF.md`, không build/test lại, không thao tác Word, không commit/push.
- **Yêu cầu hiện tại chỉ là tạo bàn giao. Không tự bắt đầu B5, Phase C, project mới hoặc sửa grading.** Chờ nhiệm vụ cụ thể ở ô chat mới.

## 2. Cách bắt đầu ở ô chat mới

1. Đọc `AGENTS.md` trong repository Word.
2. Đọc checkpoint trên cùng của `PROJECT_STATUS.md`.
3. Đọc `KNOWN_ISSUES.md`.
4. Đọc `CODEX_CONTEXT.md` và `PROJECT_CONTEXT.md`.
5. Kiểm tra HEAD/status rồi chỉ đọc source liên quan trực tiếp đến nhiệm vụ được giao.

Source thực tế thắng tài liệu cũ. Các đoạn checkpoint có ngày ở phía dưới là lịch sử, không được dùng để kết luận Testing còn disabled hoặc P25–P28 chưa hoàn thành. File hướng dẫn bài tập/original DOCX là đặc tả bài tập, không phải quyền ghi đè quy tắc repository.

Người dùng muốn: **mọi trả lời/báo cáo bằng tiếng Việt; code/comments tiếng Anh; triển khai và kiểm chứng trong phạm vi được giao, không dừng ở kế hoạch; báo cáo ngắn gọn**. Trước sửa code phải mô tả luồng, danh sách file và kế hoạch nhỏ. Không hỏi lại quyền đã được cấp; không mở rộng phạm vi từ task lịch sử.

## 3. Quy tắc bảo vệ xuyên suốt

- Giữ WinForms, .NET Framework4.7.2, C#7.3, Microsoft.Office.Interop.Word và ranh giới layer; không nâng dependency, migration hoặc refactor rộng.
- **Không sửa starter, supplied originals, packaged assets, `lang/*.json` hoặc `tasks.json` trong Phase A/B.** Metadata Testing chỉ ở `Projects/testing-catalog.json`; thay catalog phải có nhiệm vụ/quyền rõ.
- P01–P28 và hợp đồng PASS/FAIL đã nhận phải được giữ. Không tự answer trong Go/Grade, không click-history grading, không COM trong Core grading; giữ Pass/Fail/Error khác nhau.
- Excel và repository cha là read-only. Mọi Git/build/generated evidence dùng đúng repository Word, không build/clean Excel.
- Chỉ điều khiển Word/document đã chứng minh sở hữu trên đúng STA. Không attach Word cá nhân, kill theo tên WINWORD.EXE hoặc chỉ tin PID/title; giải phóng temporary RCW an toàn.
- Không thay global Word preferences/security để vượt hạn chế môi trường.
- Training: Go/new session/switch/relaunch luôn fresh starter; navigation/Grade giữ edits hiện tại; Restart No giữ bài, Yes reset đúng project; giữ Save/Save As/TXT.
- Asset/output cleanup phải theo baseline/claim/file identity/hash và exclusive handle; bảo vệ file có sẵn, sửa/thay thế, khóa hoặc không liên quan. Không suy sở hữu bằng tên/hash, không quét Documents cá nhân để xóa.
- Verification dùng isolated workspace/Documents/session IDs. Không commit `artifacts/` hoặc fixtures quá2MB.
- Git: không push/pull/reset/rebase/force/restore đè dữ liệu. Quyền commit cục bộ ở cuối từng bước A/B đã được cấp trong yêu cầu từng bước, chỉ sau build/test đạt. Yêu cầu tóm tắt hiện tại **không yêu cầu commit**; không mặc nhiên áp quyền đó vào lượt mới.
- Cảnh báo bốn file P22/P23 EN/VI dirty là snapshot28/10; hiện tại đã có baseline sạch. Nếu về sau có dirty mới, giữ nguyên, không tự restore để ép về bất kỳ baseline nào.

## 4. Lịch sử project và hợp đồng không được làm hồi quy

| Mốc | Nội dung cần nhớ |
|---|---|
| P01–P24 | Checkpoint ban đầu191 task/68 assertion identifiers. P19 giữ năm stable IDs T01,T03–T06. P21-T12 chấm đúng list-level/peer behavior; không nới theo click history. |
| P24 | T02 nhận coherent Page/Page hoặc Margin/Margin; nhận các placeholder/text-band variants đã khai báo. Không quay lại giả định cũ buộc Margin/Margin hoặc cấm expected text trong band. Câu hỏi đã từng được khôi phục nguyên byte theo yêu cầu; không thêm lại hints. |
| P25 | Đã hoàn thành5 task: Double main-story effective480; actual Strong trên đúng phrase trong Introduction; genuine inherited Banded header ẩn trang đầu; expanded2pt đúng paragraph Suggestions; Next Page section trước References. Header phải còn đúng trên References. |
| P26 | Đã hoàn thành5 task: import Information đúng interval; reorder logical SmartArt Accessibility,Speed,Accuracy,Artists; đúng coffee image bên trong Sidebar với Soft Round Bevel; real bullets đúng ba shift paragraphs; text watermark MOS Word/Arial. Không grade diagram theo serialized ptLst hoặc chỉ nhìn tên ảnh. |
| P27 | Đã hoàn thành5 task: native Sentence case toàn bảng; horizontalScroll bottom-center wrapping; cyan text highlight toàn Prince row; genuine Accent4/gold/soft-bevel title WordArt; Replace All Football→Soccer. Dùng finite native-verified table variants và hỗ trợ hai thứ tự T01/T05; không bắt title plain paragraph còn ngay trước bảng sau WordArt. |
| P28 | Đã hoàn thành6 task: Grid Table5 Dark Accent2; Unicode ® đúng vị trí sau Michael’s Art Shop; cp:contentStatus=New update!; real SEQ Table caption bên dưới bảng; genuine Banded cover bỏ Company address control; Landscape đúng original section, trước đó Portrait, hỗ trợ cover-order interactions. |

P26 alias ảnh là điểm quan trọng: package giữ `Coffee_Sam.jpg`, câu hỏi yêu cầu `Documents\Coffee Sam.jpg`. Generic optional destination filename alias đi qua staging/ownership ledger; giữ nguyên cả originals và dictionaries, không ad-hoc copy trong UI/grader. P12 Pictures vẫn được bảo vệ.

Các yêu cầu P25/P26/P27/P28 liên tiếp đã thay thế nhau theo scaffold/reference mới; **không thực hiện lại chúng chỉ vì prompt cũ còn trong lịch sử**. Chứng cứ native/API/synthetic cụ thể phải đọc checkpoint/evidence tương ứng, không gắn kết quả native lịch sử thành test mới.

Nguồn bài tập lịch sử: `D:\Việt Âu\MOS WORD\Word 2019 Associate-20260923T073405Z-1-001\Phần Mềm\Practice 3_word`, các file `Project XX.docx`, `Gmetrix Templates`, và hướng dẫn `Huong dan làm Project XX.txt` trên Desktop. Kiểm tra tồn tại và hash trước khi dùng; originals chỉ đọc.

## 5. Phase A — đã hoàn thành

### A1: hygiene/test foundation

- Commit P28 completion trước mọi thay đổi khác; line-ending policy và normalization tách commit.
- `.gitattributes`: các source/project/config/json/md dùng CRLF; Office/images/binaries là binary.
- WinForms dùng wildcard `Projects\**\*`, PreserveNewest thay danh sách copy thủ công; hiện gồm catalog.
- `MosWord2019.Tests`: classic csproj/net472/MSTest packages.config; fixtures Pass/Fail/near-miss/Error và inventory guards. Không cần Word để chạy headless.
- Có internal isolated Documents test seam được người dùng cho phép; không đổi production PASS/FAIL qua seam đó.

### A2: grader/package robustness

- `WordGradingService.CheckTask` chuyển lỗi kỹ thuật thành Error/diagnostic, trừ OOM/StackOverflow; metadata access được kiểm shape, không unsafe cast.
- Validator có ASSERTION_UNSUPPORTED; loader log issue codes của từng package bị từ chối và expose rejected count; UI EN/VI dùng strings trong code, không sửa dictionaries.
- Exception-injection, metadata helper malformed cases và near-miss cho các assertion dùng nhiều được kiểm thử.

### A3a/A3b: provenance, ownership, recovery

- Một TemplateFolderResolver đọc Word PersonalTemplates registry và fallback Documents/Custom Office Templates, được dùng thống nhất.
- OutputOwnershipLedger chụp baseline **trước mở document trên Go và Restart**: existence,size,SHA-256,last-write UTC,identity.
- Freshness: current exists AND baseline absent **hoặc hash khác, identity khác, LastWriteUtc mới hơn**; export lại cùng bytes vẫn hợp lệ nếu có provenance mới. Untouched stale file không PASS.
- P06 Cert.dotx, P07 Notes.dotx, P09 Memo.txt và P20 SavedWordTemplate được theo dõi; P07 không PASS từ Notes.dotx cũ của P20.
- P09 Memo.txt có thể trở thành live document sau Save As: cleanup chỉ sau đóng owned document; file khóa giữ journal để retry.
- P16 TXT ở folder tùy học viên: chỉ grade từ explicit owned-path context; **không scan/delete arbitrary folder**, nằm ngoài cleanup declared-output.
- Claim sau owned save events/next UI tick; cleanup có thể claim đúng declared path absent-at-baseline và creation time phù hợp. Mỗi claim journal ngay, ordering dùng monotonic sequence.
- Journal: `%LocalAppData%\MosWord2019\session-journal.json`, atomic writes; identity/hash-verified recovery, modified/replaced/locked được giữ/log. Cleanup thất bại giữ claim cho startup sau.
- Working cleanup chỉ tên reserved và thư mục rỗng đã biết; không sweep file lạ.
- Named mutex `Local\MosWord2019.Trainer`, bilingual second-instance notice; fatal handlers giữ dữ liệu và chạy owned cleanup/recovery an toàn.

### A4: COM/shutdown safety

- Owner-STA IOleMessageFilter: retry rejected/retry-later tối đa10s; WordBusyException có thông báo thân thiện EN/VI. Restore filter khi dispose.
- Post-save observation backoff250ms trong5s, một summary cuối; không log hàng chục stack traces.
- TaskManagerClosing hoặc RPC_E_CANTCALLOUT_ININPUTSYNCCALL0x8001010D: deferred close bằng BeginInvoke. Windows shutdown không COM/block; journal giữ lại.
- Cleanup failure có Retry/Close Anyway; Dispose không throw. Event document RCW được release đúng.
- Corrupt journal được quarantine atomically thành `session-journal.invalid-<UTC>[-suffix].json`; không xóa/recover claims trong file corrupt; mở journal mới sau quarantine thành công.
- MissingTranslationException chỉ cho thiếu translations; journal/metadata/ownership errors không còn bị gán nhầm thông báo thiếu câu hỏi.
- Checklist A có ở `docs/MANUAL_TEST.md`. Không nhầm với B5 checklist chưa có.

## 6. Quyết định sản phẩm Testing — giữ nguyên

- 50 phút; đúng7 project ngẫu nhiên; mỗi project4–6 task nhưng không vượt số task có; tổng N34–40.
- 1000 điểm: b=floor(1000/N), r=1000-b*N; r task do seed chọn nhận b+1, còn lại b. Mỗi task25–30; perfect=1000.
- Error hoặc thiếu grade tính incorrect/0 và log nội bộ; không hiện diagnostics cho học viên.
- Từ700: Đạt/Pass; dưới700: Cần Luyện Tập thêm/Needs more practice. Kết quả có điểm từng project và `P15-T04: <instruction đầy đủ theo ngôn ngữ phiên>` cho từng task sai.
- Không Grade/per-task feedback trong khi thi; không Previous Project. Next có xác nhận không quay lại; project cuối là Submit; Submit bất kỳ lúc nào có xác nhận.
- Restart chỉ current project; timer vẫn chạy. Mark for review là local.
- Timeout khóa UI/owned Word, tự nộp không hỏi. Word bận được retry30s, sau đó verified owned-process recovery và chấm last autosave/checkpoint; lỗi I/O thật giữ recovery chứ không fake result.
- Mất mạng: pause timer, ẩn task, disable owned Word và hiện overlay/nút nộp; tối đa30 phút rồi finalize. Hiện dùng AlwaysOnlineMonitor/fakes; real heartbeat là Phase C.
- **Quyết định mới nhất: downtime do crash/kill/power loss CÓ TÍNH vào50 phút**: elapsed khi resume = stored elapsed + thời gian từ lastHeartbeatUtc, cap duration. Chỉ network loss pause.
- Chỉ offer resume trong15 phút, cùng máy. Hết thời gian hoặc không còn remaining: finalize từ autosaves. Heartbeat lưu mỗi5s; autosave60s.
- Preparing/Prepared chưa start phải giữ cùng seed/definition, cho thử lại Start/preflight; không được reroll bằng kill app.
- Preflight chỉ chặn asset khác bytes tại staging destination. **Declared output đã tồn tại không chặn exam**; freshness/preservation Phase A xử lý.
- Orphan Word: lưu PID/creation time/image; chỉ terminate bằng verified handle sau xác nhận bilingual ở startup. Từ chối giữ session pending và hỏi lại lần sau, không recover/finalize lén.
- Clock rollback >2 phút ở recovery được log/lưu flag; downtime giữ0, không giả định máy có thời gian đáng tin cậy. Server time/signing thuộc Phase C.

## 7. B0–B4 — kiến trúc và trạng thái triển khai

### B0: catalog riêng

`MosWord2019.WinForms/Projects/testing-catalog.json` có28 eligible projects; không nhét metadata Testing vào tasks.json. Loader/validator kiểm schema/IDs/group closure/khả thi7 project và34–40 task.

| Quy tắc catalog hiện hành | Giá trị |
|---|---|
| Required | P07-T01 legacy conversion |
| Chọn cùng nhóm | P14-T03/T04; P18-T05/T07 |
| Conflict | P07/P20, group template-notes vì Notes.dotx |
| Starter-satisfied exclusions | P04-T05; P13-T03; P16-T01 |
| Live-snapshot exclusions chưa được bật lại | P22-T07 FormattingMarksVisible; P24-T03 TOC layout |

`docs/TESTING_CATALOG_REVIEW.md` giải thích từng project. P22/P24 có isolated fake/retained-fixture Pass/Fail/Error pipeline evidence nhưng **không được tự enable lại khi chưa có người dùng chấp thuận**.

### B1: composition/scoring thuần Core

ExamComposer/ExamDefinition, deterministic SplitMix64 PRNG, ScoreAllocator, ExamScorer/ExamResult. Chọn identity project trước subset; không bias bằng số tổ hợp task. Tôn trọng conflict/legacy tối đa1/grouped/required/excluded và task order gốc.100k-seed constraints/frequency/reproducibility tests, N34–40, totals1000 và699/700/1000 boundaries đã có.

### B2: engine, workspace, STA, startup

- TestingSession dùng monotonic clock; project state NotStarted/Active/Locked, một chiều, chỉ reset current.
- TestingWorkspaceService: `Documents\MosWord2019\Working\Testing\<SessionId>\<ProjectId>\work.<ext>`, đi qua journal; stage/register baseline khi enter, cleanup khi leave.
- TestingProjectRunner dùng lại controller/ledger/staging/checkpoint/grader; save→capture live state→close→grade selected tasks→persist→cleanup. Unvisited grade từ untouched starter.
- FullName khác working copy sau Save As: **không Save output**; xuất WordOpenXML formatted checkpoint atomically, reject macros; non-output tasks grade checkpoint, output tasks grade file/context của chúng.
- Owned DocumentBeforeClose checkpoint trước native close; reopen từ checkpoint, cảnh báo và timer tiếp tục. Không tự làm legacy conversion task bằng internal XML checkpoint.
- TestingStaHost: background owning STA, message pump/serialized commands,250ms ticks,60s autosave,5s heartbeat; không Task.Run COM.
- Resume state: `%LocalAppData%\MosWord2019\testing-session.json`; crypto seed8 bytes; lưu definition, language, audit hashes catalog+7(tasks/starter)=15, results/context/owned PID, elapsed và thời gian heartbeat.
- Startup kiểm state trước generic journal recovery, bảo vệ claims theo Testing SessionId. Resumable giữ inputs; expired finalize trước recover. Same-process identity recheck và quyền dừng orphan được giữ.

### B3: UI và sửa coordinator

- Login bật Testing EN/VI; TestingFlow/TestingForm/ResultForm/GradingProgress nối engine hiện có. Word ở trên/panel dưới; chỉ selected task tabs; rules/Start, countdown đỏ từ05:00, review marks, Restart/Next/Submit và offline overlay.
- Busy/RPC errors không fault permanently; Next/Submit và next-project entry retry được. Deadline/offline-limit retry30s rồi force recovery bằng verified identity và saved-only grade, không phụ thuộc live Word hoặc thay working file đang khóa.
- Tick bị chặn trước Start/Resume attachment để không reset nhầm working copy. Restore tính downtime lúc attach; accepted resume mở trực tiếp trước Login, dùng language đã lưu.
- Lỗi thật có đường quay startup recovery; Completed cleanup thất bại vẫn giữ durable result/journal.
- UI marshal cả host events lẫn post-await continuations; bỏ update sau dispose. In-flight Start/Resume close phải dùng deferred recovery exit.
- Progress responsive; Task Manager/Windows shutdown không bị progress dialog chặn. No task-grade diagnostics trong result UI.

### B4: persistence và shared atomic retry — mốc mới nhất

`TestingSessionStateStore.PersistCompletedResult()` lưu **trước final cleanup** tại `%LocalAppData%\MosWord2019\Results\<SessionId>.json`:

- version/sessionId/seed/machine/language/definition/contentHashes;
- startedUtc/completedUtc/lastHeartbeatUtc/lastSaveUtc/elapsedTicks;
- offlinePeriods gồm StartedUtc/EndedUtc/DurationTicks, historyIncomplete, clockRollbackDetected, finalizeReason;
- tasks gồm projectId/taskId/instructionKey/outcome/points/awardedPoints, total/passed/perProject;
- **pendingUpload=true**, chưa có uploader.

Không đưa grader messages, working paths hoặc ownership/COM details vào result. Thiếu grade thành Error/0. Recompute score/weights và đối chiếu tổng lưu. Identical result được dùng lại; khác dữ liệu thì bảo vệ cả result và state. Metadata save/heartbeat lúc chốt được giữ nguyên dù cleanup quan sát save sau đó. Archive chỉ xóa state sau khi result durable; state đang khóa còn retry được.

TestingSession/snapshots ghi immutable offline history theo đồng hồ đơn điệu và giữ qua resume. UTC mô tả thời gian máy, có clamp ending UTC khi rollback. State cũ thiếu audit được đánh dấu historyIncomplete, timestamp không có giữ null; **không migrate lén archive B2/B3**.

`Core/IO/AtomicFile.cs` là helper I/O không thuộc grader. Tất cả production File.Move/Replace cho state/results/journal/quarantine/checkpoint/working promotion/reset đi qua nó:

- chỉ retry Win32 errors32/33/1175/1176/1177;
- tối đa10 attempts,9 lần chờ220ms =1,98s; không phải watchdog cho filesystem operation đã treo;
- recheck reparse paths và caller guards mỗi attempt, giữ no-overwrite/metadata/ownership behavior;
- source bị Windows consume thì không replay;
- hết budget log một summary, không log storm mỗi attempt.

ResultForm bật WordWrap/scroll dọc. Test journal-reader cũ được đổi cách release độc lập với retry log để khớp hợp đồng chỉ-log-cuối; không bỏ test/hạ assertion cốt lõi.

## 8. Commit và kiểm chứng để truy vết

Các commit đã đọc lại từ Git trong lượt bàn giao, newest ở trên:

| Commit | Nội dung |
|---|---|
|7cf99c0|B4: auditable results + atomic retry |
|ee9a32a|B3: Testing UI + resilient submission |
|d3e0590|B2: full coordinator/STA/startup |
|2a4122f|B2: per-project pipeline/protected recovery |
|d2dfa0e|B2: session/audited workspace foundations |
|e2467e0|B1: deterministic composition/scoring |
|358828d|B0: catalog/eligibility review |
|1ef59e3|A4: OLE rejection/deferred shutdown |
|c3dd7d6|A3b: journal/output rewrite observation |
|0c012f4|A3a: output provenance/owned cleanup |
|b96a2df|A2: grader/package-loading robustness |
|967578b|A1: headless fixtures/wildcard copying |
|4cd5f01|Normalize line endings |
|25652de|Line-ending policy |
|79b4c8e|P28 completion |

Các commit bước A/B là **local, không push bởi trợ lý**. Không suy trạng thái GitHub hiện tại từ lịch sử hội thoại.

### Bằng chứng cuối đã chạy ở B4 — không phải chạy lại trong lượt viết MD

- `artifacts/b4/verified-build-Debug.log`, `verified-build-Release.log`:0 warnings/0 errors.
- `artifacts/b4/verified-Debug.trx`, `verified-Release.trx`:770/770 mỗi cấu hình,694 headless +76 desktop,0 skipped; stderr logs rỗng.
- `artifacts/b4/regression-audit.txt`: đủ749 tên/case B3 được giữ.
- `artifacts/b4/immutable-verification.txt`:150/150 source/output matches;149 original files gồm56 dictionaries,28 starters,8 assets không đổi; fixtures/catalog cũng không có diff.
- `artifacts/b4/ui/`: ảnh WinForms EN/VI, gồm task sai wrap/scroll dọc. Đây không phải native Word run.
- `artifacts/b4/red.trx`, `green2.trx`, `late-observation-red.trx`, `late-observation-green.trx`: red→green cho filesystem/result và late-cleanup metadata; không che failed intermediate runs bằng nhãn PASS.

`artifacts/` gitignored, có thể không tồn tại khi clone sang máy khác. Kiểm tra trước khi viện dẫn; không commit nó. Không chạy mù harness lịch sử có baseline/đường dẫn/side effects cứng.

Build/test tools trên máy hiện tại (kiểm tra lại nếu đổi máy):

```text
C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\Extensions\TestPlatform\vstest.console.exe
C:\Users\HUYNH HAU\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe
```

Build đúng `MosWord2019.slnx` ở Word root, Debug và Release. Chạy cả `MosWord2019.Tests` và `MosWord2019.Desktop.Tests`; suites headless/fake desktop không cần kích hoạt Word. Retained immutable checker: `artifacts/b0/verify.py`. Git từng báo quyền đọc global ignore; các lượt kiểm tra dùng `git -c core.excludesFile=artifacts/a1/empty-git-excludes ...` khi file workaround còn tồn tại.

## 9. Giới hạn đã xác nhận — không biến thành PASS

- Chưa chạy mới native Word/full50-minute exam trong B3/B4. Native Font dialog, actual HWND/modal behavior, Save As→close/checkpoint timing, Task Manager/shutdown, orphan termination, crash/power-loss và personal documents còn chờ manual acceptance.
- Các lần COM activation0x80070520 thuộc evidence lịch sử, không phải attempt mới của lượt bàn giao/B4. Không đổi ownership/security để né môi trường.
- Fake controller + real OOXML fixtures, standalone HWND/OLE tests và ảnh DrawToBitmap không tương đương thao tác Word thật.
- Actual antivirus/Windows Search lock contention chưa được tái hiện; retry dùng injected filesystem và isolated real file locks. Lock kéo dài >budget vẫn lỗi an toàn.
- B3 có một supplemental VSTest render run treo chưa rõ nguyên nhân; đã ghi Known Issues, diagnostic rerun đạt. Một late-UI continuation race tìm được sau đó đã sửa; các lần chốt B3/B4 stderr rỗng. Không tự suy hai hiện tượng có cùng nguyên nhân.
- JSON state/results user-writable; elapsed/locked grades/audit có thể bị sửa. Server-authoritative time/signing/upload là Phase C, không có cam kết chống gian lận local.
- AlwaysOnlineMonitor chưa phát hiện network loss thật. Debug fake toggle của B5 chưa triển khai.
- OLE/file retry không ngắt được một native/COM/filesystem call đã được chấp nhận nhưng treo vô hạn.
- P22-T07/P24-T03 giữ excluded. Chỉ đề xuất bật lại với kiểm chứng và user approval, không tự sửa catalog.
- Không có database/SQL/ORM/authentication/server API mới; kết quả hiện là JSON local.

## 10. Công việc kế tiếp — chỉ khi được giao

**B5 dự kiến**, chưa được ủy quyền bởi yêu cầu tóm tắt này:

- Viết `docs/TESTING_MANUAL_TEST.md` tiếng Việt, expected results rõ cho full50-minute exam, timeout, early submit, Restart, one-way Next, learner closes Word, Task Manager kill rồi resume trong/sau15 phút, offline fake Debug-only toggle, kết quả EN/VI, ownership/cleanup.
- Nếu làm Debug fake toggle phải chỉ compile trong Debug, không đưa vào Release/production heartbeat; cần test tương ứng.
- Thực hiện/ghi rõ manual Word acceptance khi có môi trường thật; không gắn nhãn PASS cho checklist chưa chạy.
- Tiếp tục giữ build Debug/Release0 warnings/0 errors, cả hai suites, immutable hashes và local focused commit khi yêu cầu bước mới cho phép.

Phase C chỉ được thảo luận như phần tương lai: real connectivity heartbeat, upload pending results, server time/signing. Chưa có đặc tả/ủy quyền để tự triển khai.

## 11. Gợi ý tin nhắn mở ô chat mới

> Đây là dự án MOS Word 2019 Trainer tại C:\Users\HUYNH HAU\source\repos\MosWord2019. Hãy đọc CONVERSATION_HANDOFF.md và tài liệu theo thứ tự trong đó, kiểm tra lại Git/source. Checkpoint mới nhất là7cf99c0: P01–P28, Phase A và B0–B4 đã hoàn thành;770 test mỗi cấu hình. B5/Phase C chưa làm. Giữ starters/assets/questions/tasks/catalog và hợp đồng grading/owned Word/cleanup; không suy việc mới từ yêu cầu lịch sử. Thực hiện đúng nhiệm vụ cụ thể tôi đưa tiếp theo, phân biệt evidence lịch sử với kiểm chứng mới; không push/pull/reset/ghi đè dirty worktree.
