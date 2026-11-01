# Kiểm thử chấp nhận Testing — B5

Cập nhật: **30/10/2026, Asia/Bangkok**. Tài liệu ghi lại kết quả Word thật **do người dùng xác nhận trong chat**, đồng thời cung cấp các ca cần chạy tiếp. Đây không phải tuyên bố Codex vừa thực hiện lại kiểm thử native. Test fake/OOXML/desktop tự động được báo cáo riêng trong `PROJECT_STATUS.md`.

## Môi trường và chuẩn bị

- Môi trường đã chạy: **Word 16 Click-to-Run**, `C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE`, máy **DESKTOP-4968L2S**, build **Debug**, giao diện **Tiếng Việt**; bước10 kiểm tra mở bài mới bằng English.
- Thiết lập của checklist gốc: mở ứng dụng, chọn **Testing + Tiếng Việt**, bấm **Continue**. Đọc luật rồi bấm **Bắt đầu bài thi** ở bước1.
- Khi chạy lại, dùng VM/tài khoản thử nghiệm hoặc dữ liệu cách ly. Giữ state/journal/log gốc dùng điều tra, bao gồm `testing-session.json.bak`; không mở Trainer trên bộ chứng cứ đó. Không sửa starter, `tasks.json`, `lang/*.json`, catalog hoặc assets để tạo đáp án.
- Ghi commit/build, phiên bản Word đầy đủ nếu có, SessionId/seed, ngôn ngữ, giờ, PID Word sở hữu và đường dẫn Documents/Pictures/PersonalTemplates thực tế. Chỉ đóng/kill đúng process thử nghiệm đã xác định; không kill WINWORD theo tên hay process tree.
- Chỉ đọc kết quả ở `%LocalAppData%\MosWord2019\Results\<SessionId>.json`; không sửa thời gian/state/điểm để biến một ca chưa chạy thành PASS. Log ở `%LocalAppData%\MosWord2019\Logs`.

## Biên bản 10 bước Word thật gốc

Bảng dưới **giữ nguyên văn và số bước người dùng cung cấp**, kể cả phần mô tả tiếng Anh, để không gán nhầm bằng chứng. Tóm tắt kết quả ngày30/10/2026: bước1–4 PASS ở lượt đầu; bước5 FAIL ở lượt đầu do mở lại vào Word đang tắt, dẫn tới recovery bị kẹt; **sau B4-FIX, bước5–10 PASS trên Word thật**. Đoạn log lỗi21:51–21:56 và fixture phiên `8123ccd108534a2496b95b7b2cf831f3` là bằng chứng lỗi lịch sử; chúng không được đổi nhãn thành log của lượt PASS.

| # | Action | Expected | Result |
|---|---|---|---|
| 1 | Read the rules screen, click "Bắt đầu bài thi" | Shows "50 phút, 7 project, N task" with 34 ≤ N ≤ 40. Word opens project 1. Timer starts at 50:00. Only 4–6 task tabs. No Grade button. | PASS (2026-10-30, first run) |
| 2 | Complete 1–2 tasks correctly, click "Project tiếp theo" | Confirmation "không thể quay lại"; Yes moves to project 2. No Previous button. | PASS (first run) |
| 3 | Open the Font dialog (Ctrl+D) in Word, then click "Project tiếp theo" | After about 10 s: "Hãy đóng hộp thoại đang mở trong Word rồi bấm lại". No hang. Close Font, click again → moves on. | PASS (first run; log 21:52:49 shows WordBusyException handled) |
| 4 | Make edits, click "Làm lại project" → Yes | Document returns to its original state; the timer keeps running. | PASS (first run) |
| 5 | Make an edit, then close the Word window with X | Word reopens the current project automatically, the edit is still present, message "Đã mở lại Word; thời gian vẫn chạy". | FAIL on first run (reopen into a dying Word instance, then a permanent startup-recovery dead end; log 21:53–21:56, state fixture session 8123ccd1…). PASS after B4-FIX. |
| 6 | If the exam contains P06, P07, P09, P16 or P20: do the Save As task, then another task, then Next | On the result screen neither task is listed as incorrect. | PASS after B4-FIX |
| 7 | Note the timer. Task Manager → Details → End task MosWord2019.exe. Wait about 1 minute and restart | Asks to close the old Word → Continue; asks to resume → Yes; opens the exam directly at the same project; the timer has the downtime deducted. | PASS after B4-FIX |
| 8 | Click "Nộp bài" → confirm | "Đang chấm bài…", then a result screen with "xxx / 1000", "Đạt" (≥700) or "Cần Luyện Tập thêm", per-project scores, and "Các task sai:" lines formatted P15-T04: <instruction>. Correctly done tasks are not listed. | PASS after B4-FIX |
| 9 | Close the result, inspect Documents and Pictures | No exam assets/outputs remain; no folder for that exam remains under Documents\MosWord2019\Working\Testing\. | PASS after B4-FIX |
| 10 | Start a new exam in English | A different exam; the whole UI and the instructions are in English. | PASS after B4-FIX |

Giới hạn bằng chứng: bước6 là ca có điều kiện theo project thực sự được chọn, không chứng minh đã chạy riêng đủ cả năm project. Bước9 ghi kết quả của phiên thử; file có trước, đã sửa/thay thế hoặc bị khóa phải được giữ theo ownership ledger. P16 TXT ở thư mục tự chọn không thuộc cleanup declared-output. Không suy từ bước9 rằng ứng dụng được phép xóa mọi file còn lại trong Documents/Pictures. Bước10 ghi quan sát bài mới trong lượt thử, không đổi quy tắc ngẫu nhiên thành cam kết không bao giờ trùng đề.

## Đối chiếu thao tác và văn bản EN/VI

Chạy lại các bước tương ứng với English khi thay đổi UI hoặc nguồn câu hỏi. Bước10 đã PASS cho việc mở bài English; không suy thành việc mọi biến thể và các ca tùy chọn đều đã chạy đầy đủ bằng EN.

| Vị trí | Tiếng Việt | English | Mong đợi |
|---|---|---|---|
| Start | Bắt đầu bài thi | Start | Chỉ chạy timer sau preflight và mở Word thành công. |
| Restart | Làm lại project | Restart Project | No giữ bài; Yes reset project hiện tại, timer tiếp tục. |
| Next | Project tiếp theo | Next Project | Xác nhận một chiều; không có Previous. |
| Xác nhận Next | Bạn sẽ không thể quay lại project này | You will not be able to return to this project. | No giữ project; Yes đi tiếp. |
| Submit | Nộp bài | Submit | Dùng được trước project cuối; project7 có nút Next mang nhãn Submit. |
| Xác nhận Submit | Nộp bài thi này? | Submit this exam? | No giữ bài; Yes chấm toàn bộ bảy project. |
| Đang chấm | Đang chấm bài… | Grading… | UI vẫn có message pump; không hiện chẩn đoán grader. |
| Đạt | Đạt | Pass | Tổng từ700/1000; perfect đúng1000. |
| Chưa đạt | Cần Luyện Tập thêm | Needs more practice | Tổng dưới700/1000. |
| Danh sách sai | Các task sai: | Incorrect tasks: | Một dòng/task: `P15-T04: <instruction đầy đủ theo ngôn ngữ phiên>`; cuộn/copy được, không có diagnostics. |
| Offline | Mất kết nối – đang chờ kết nối lại | Connection lost – waiting to reconnect | Ẩn task, khóa Word sở hữu, giữ nút nộp bài. |
| Phím tắt Debug | Ctrl+Shift+F12 | Ctrl+Shift+F12 | Chỉ có handler trong Testing Debug; không có checkbox ở bất kỳ build nào. |

## Thay đổi thao tác sau U1

Biên bản ngày30/10 bên trên giữ nguyên lời bước gốc. Từ U1: điền tài khoản/mật khẩu bất kỳ không rỗng, chọn Testing và ngôn ngữ rồi bấm Login/Đăng nhập thay cho Continue. Ngôn ngữ đổi nhãn ngay; nhớ tên tài khoản/ngôn ngữ, không nhớ mật khẩu. Tab hiển thị Task1..n theo thứ tự chọn; đồng hồ luôn đỏ. Nút ←Task/Task→ chỉ chuyển task trong project, không quay về project trước. Chữ lớn có cuộn dọc; panel tối đa35% working area. Đây là thay đổi UI, không mở rộng bằng chứng PASS native cũ.

## Các ca bổ sung — CHƯA CHẠY trên Word thật

Các ca sau không được gộp vào PASS của mười bước trên. Điền thời điểm/ảnh/log thực tế khi chạy; giữ trạng thái **CHƯA CHẠY** cho tới khi có bằng chứng.

| Mã | Thao tác | Kết quả mong đợi | Trạng thái native |
|---|---|---|---|
| O1 | Làm một bài đủ50phút, không pause offline; ở project bất kỳ khi hết giờ. | `00:00` khóa thao tác và cửa sổ Word sở hữu, tự nộp không hỏi; chấm project chưa mở từ starter, lưu result trước cleanup. | **CHƯA CHẠY** |
| O2 | Mở Ctrl+D/Font sát thời điểm timeout và giữ hộp thoại mở. | Timer vẫn về0; không hỏi xác nhận nộp. Retry/fallback nộp từ dữ liệu đã lưu trong khoảng30giây khi Word tiếp tục từ chối RPC; không deadlock hay fake score, không terminate process chưa chứng minh sở hữu. Ghi riêng thời gian bắt đầu nộp và thời gian chấm xong. | **CHƯA CHẠY** |
| O3 | Thực hiện toàn bộ chuỗi mô phỏng offline bằng phím tắt Debug bên dưới. | Pause/resume đúng thời gian, task/Word khóa đúng, có nộp sớm khi offline và tự finalize sau30phút mất kết nối liên tục. | **CHƯA CHẠY** |
| O4 | Kill đúng Trainer thử nghiệm, chờ hơn15phút từ heartbeat cuối hoặc để bài hết thời gian, rồi mở lại trên cùng máy. | Không resume bài hết hạn; chấm checkpoint/autosave và project chưa mở, lưu kết quả trước giải phóng phiên; mở được Testing mới. | **CHƯA CHẠY** — bước7 gốc chỉ chờ khoảng1phút. |

Ngân sách retry không ngắt được một lệnh COM/native/filesystem đã được chấp nhận nhưng treo vô hạn. Nếu O2 gặp tình huống này, ghi FAIL/giới hạn kèm log thay vì nhận là đã đạt mốc30giây. Không hạ tiêu chí kiểm thử để che lỗi.

### Cách dùng mô phỏng offline chỉ trong Debug

1. Build `MosWord2019.slnx` cấu hình **Debug** bằng VS MSBuild. Mở một bài Testing thử nghiệm và Start. Đặt focus vào cửa sổ Trainer, nhấn **Ctrl+Shift+F12** để bật/tắt offline; phím tắt không áp dụng khi focus đang ở Word. Phím bị bỏ qua trước Start, khi đang thao tác/nộp hoặc cần recovery.
   **Mong đợi:** không có checkbox Debug trên mọi build. Debug hiển thị ghi chú nhỏ `DEBUG: Đang giả lập mất mạng`/`DEBUG: Simulated offline` hoặc `DEBUG: Đã kết nối`/`DEBUG: Online`. Release không chứa handler, monitor hay lệnh giả; vẫn AlwaysOnlineMonitor tới Phase C. Ngắt cáp mạng thật hiện chưa kích hoạt luồng offline.
2. Ghi timer, SessionId và project hiện tại; nhấn phím tắt để offline, giữ ít nhất60giây.
   **Mong đợi:** monitor giả phát Offline trên STA của host; luồng session có sẵn pause timer, overlay che task, Word sở hữu bị disable. Next/Restart tắt; nút Nộp bài ở overlay vẫn dùng được. Timer không mất60giây đang offline; các Word không thuộc phiên không bị khóa.
3. Đặt focus vào Trainer rồi nhấn Ctrl+Shift+F12 lần nữa để online, tiếp tục sửa bài.
   **Mong đợi:** overlay biến mất, task/Word được bật lại, giữ edits/project/seed và số giây còn lại; timer tiếp tục chạy. Lịch sử offline được lưu khi chốt kết quả.
4. Trong lượt riêng, bật offline rồi bấm Nộp bài ở overlay và xác nhận.
   **Mong đợi:** nộp sớm qua pipeline hiện có, kết quả có per-project/instruction đúng ngôn ngữ, `pendingUpload=true`; không cần chuyển online trước khi nộp.
5. Trong lượt riêng, giữ offline liên tục đủ30phút.
   **Mong đợi:** auto-finalize một lần từ dữ liệu hiện có, không hỏi, không kéo dài thời gian chờ bằng các tick lặp lại. Đây là ngưỡng offline, không phải50phút thi. Không sửa JSON hoặc thêm công tắc nhảy thời gian để giả lập PASS native.
6. Mở bài mới rồi kiểm tra lại bằng Release.
   **Mong đợi:** bài mới không kế thừa trạng thái offline giả của bài trước; Release không có handler phím tắt hoặc công tắc. Training không thay đổi. Kết quả các bước này vẫn **CHƯA CHẠY native** cho tới khi người thử xác nhận.

## B4-FIX targeted retest

Giữ riêng checklist bảy bước B4-FIX để tránh nhầm với số thứ tự của biên bản mười bước. Đối chiếu: targeted5 ↔ bước5/6 gốc; targeted6 ↔ bước8/9 gốc; targeted7(b), resume trong15phút ↔ bước7 gốc; việc mở bài mới sau recovery ↔ bước10 gốc. Các phần tương ứng đã được người dùng xác nhận PASS sau B4-FIX. **Không mở rộng PASS đó sang mọi biến thể dưới đây**: tài liệu cá nhân trong cùng instance, kill riêng Word, timeout trong lúc reopen, O1–O4 và các nhánh lỗi hạ tầng chưa có xác nhận native riêng.

1. Dùng tài khoản/VM thử nghiệm và bản build B4-FIX mới. Giữ bản sao state/log lỗi trước khi chạy; không chạy Trainer trên file gốc đang dùng làm chứng cứ. Mở một Word cá nhân riêng làm sentinel, ghi PID/creation time và nội dung chưa lưu. Ghi build, Word version, ngôn ngữ và giờ bắt đầu.
   **Mong đợi:** không sửa starter, question, assets hoặc tài liệu cá nhân. Sentinel không bị đóng/lưu/terminate bởi Trainer.
2. Chọn Testing EN rồi VI ở hai lượt riêng, Start và ghi SessionId, seed, project/task đã chọn, PID Word sở hữu và timer.
   **Mong đợi:** đúng bảy project, subset task cố định, timer bắt đầu khi Word mở thành công; không có Grade trong bài thi.
3. Sửa một nội dung dễ nhận biết trong project hiện tại; chuyển task, mở/đóng hộp thoại Font rồi thử thao tác khi Word bận và thử lại sau khi đóng Font.
   **Mong đợi:** edits không mất; Word bận không biến thành yêu cầu khôi phục; timer vẫn chạy, nút thao tác dùng lại được.
4. Thử Restart No/Yes và Next project có xác nhận bằng dữ liệu thử nghiệm.
   **Mong đợi:** No giữ bài; Yes chỉ reset project hiện tại; Next một chiều, không đổi seed/phiên/deadline. Sau đó thêm edits mới, gồm cả edits chưa Save, để dùng bước5.
5. **Đóng cửa sổ Word sở hữu bằng X**, không đóng Trainer. Lặp trên project thường và ngay sau Save As sang TXT/template; thử cả trước chu kỳ autosave60giây.
   **Mong đợi:** callback ghi checkpoint trước đóng. Trainer nhả Application cũ, chờ handle process cũ thoát tối đa10giây mỗi lần thử, rồi mở **instance mới/PID mới** và đúng project/checkpoint; edits và provenance Save As giữ nguyên, không tự hoàn thành legacy conversion. Reopen thử tối đa ba lần với backoff trước status warning; nếu chưa được thì tiếp tục retry ở tick sau. Warning tạm thời không ẩn task/khóa vĩnh viễn Next/Restart/Submit; timer tiếp tục. Ghi PID cũ/mới và đoạn log, không suy PASS từ việc chỉ thấy cửa sổ Word xuất hiện.
   **Biến thể bảo vệ:** mở thêm một tài liệu thử nghiệm trong chính Word sở hữu rồi đóng tài liệu project. Nếu tài liệu kia giữ process sống quá10giây, Trainer chỉ chờ/cảnh báo; Submit/timeout/đóng Trainer không được terminate Application đã retire. Lưu/đóng tài liệu thử nghiệm thủ công, kiểm tra reopen retry hoặc nộp từ checkpoint.
6. Nộp bài sau reopen, và thử riêng timeout trong lúc reopen đang retry. Nếu có lỗi hạ tầng thật, thử đường khôi phục.
   **Mong đợi:** warning tạm thời trước đó không che mất sự kiện NeedsRecovery thật; UI có Recover, không mắc kẹt ở progress. Khi Inspect lỗi, cả startup và mở Testing đều có ba lựa chọn song ngữ **Thử lại / Retry**, **Nộp bài từ dữ liệu đã lưu / Submit from saved work**, **Để sau / Later**. Retry kiểm tra lại; Later giữ phiên và không gọi Prepare lên active state; Submit saved không mở Word, chấm checkpoint/autosave đã lưu, lưu `Results/<SessionId>.json` trước release phiên. Lỗi I/O/audit thật vẫn giữ recovery và cho chọn lại, không tạo điểm giả.
7. **Task Manager kill rồi mở lại**, xác định đúng process thử nghiệm; tuyệt đối không kill theo tên WINWORD hoặc process tree. Lượt(a): kill Word sở hữu rồi để Trainer còn chạy; lượt(b): kill Trainer và mở lại trong15phút; lượt(c): mở lại sau15phút hoặc hết thời gian thi. Giữ checkpoints/log trước từng lượt.
   **Mong đợi:** (a) reopen instance mới từ checkpoint/autosave gần nhất, không cam kết giữ edits chưa tới checkpoint khi kill cưỡng bức; (b) offer Resume/Submit/Later, giữ seed/subset và cộng downtime; (c) finalize saved work, lưu result rồi release phiên. PID đã thoát hoặc identity đọc lỗi31/access denied/reused không chặn recovery, không bị terminate, chỉ log một dòng cho lượt kiểm tra lỗi. Orphan còn sống và chứng minh được sở hữu vẫn cần xác nhận song ngữ trước khi dừng. Sau finalize phải mở được Testing mới; không lặp “An active Testing session requires resume/finalization”.

Với bước5/7 của mục targeted này và từng biến thể, ghi **PASS/FAIL/CHƯA THỬ**, giờ EN/VI, SessionId, PID cũ/mới, ảnh, log, checkpoint/result hashes và sentinel còn nguyên. Chỉ đánh dấu PASS khi đã chạy đúng trên Word thật bằng bản sửa mới.

## Regression set tối thiểu sau thay đổi Testing hoặc WordController

Chạy lại các bước gốc **1–10** trong một phiên VI, gồm Word-busy ở3, Restart ở4, close-X ở5, Save As ở6 nếu chọn được project phù hợp, crash/resume ở7, nộp sớm/result/cleanup ở8–9 và mở English ở10. Nếu đề không có project Save As, bổ sung một phiên hợp lệ có project phù hợp; không sửa catalog hoặc tasks để ép đề. Kiểm tra thêm Restart/Submit chọn **No** giữ nguyên bài.

Sau thay đổi timer/locking/finalization, thêm **O1/O2**; sau thay đổi connectivity/overlay, thêm **O3** ở Debug và xác nhận không có công tắc ở Release; sau thay đổi persistence/startup, thêm **O4**. Dùng một Word riêng với nội dung chưa lưu làm sentinel cho ownership. Mỗi lượt ghi kết quả mới, không sao chép nhãn PASS lịch sử.

Trước khi chốt thay đổi: build Debug/Release0warnings/0errors, chạy cả hai suites, kiểm nguồn/output package hashes. Đây là bằng chứng tự động bổ sung, không thay thế các bước Word thật.

## Mẫu ghi kết quả tiếp theo

| Ngày/giờ | Commit + build | Word + máy | SessionId + ngôn ngữ | Bước/biến thể | PASS/FAIL/CHƯA CHẠY | Ảnh/log/hash và ghi chú |
|---|---|---|---|---|---|---|
| Chưa ghi nhận lượt mới | | | | O1/O2/O3/O4 hoặc regression | CHƯA CHẠY | Không điền PASS từ test fake. |
