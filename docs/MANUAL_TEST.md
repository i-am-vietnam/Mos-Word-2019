# Kiểm thử thủ công A3a / A3b / A4

Ngày soạn: 2026-10-30. Đây là checklist chưa được xác minh trên Word thật trong lượt A4: môi trường Codex bị COM `0x80070520` trước khi mở tài liệu. Không đánh dấu PASS nếu chưa thực hiện và ghi bằng chứng.

## Chuẩn bị

1. Dùng máy ảo hoặc tài khoản Windows thử nghiệm có Word 2019 và .NET Framework 4.7.2. Build Debug/Release bằng Visual Studio MSBuild, chạy `MosWord2019.WinForms\bin\Release\MosWord2019.exe`. Không sửa starter, câu hỏi hoặc assets trong Projects. Ghi phiên bản Word và cấu hình build.
   **Mong đợi:** ứng dụng mở bình thường; EN/VI đọc được; mọi Go mở starter mới.
2. Xác định Documents thực tế và PersonalTemplates (`HKCU\Software\Microsoft\Office\16.0\Word\Options\PersonalTemplates`; nếu không đặt, dùng Documents\Custom Office Templates). Chỉ thao tác trên dữ liệu thử nghiệm. Nếu Cert.dotx, Notes.dotx hoặc Memo.txt đã là tài liệu cá nhân, dừng và đổi sang tài khoản/VM thử nghiệm; không ghi đè chúng. Ghi hash trước/sau bằng `Get-FileHash -Algorithm SHA256`. Journal: `%LocalAppData%\MosWord2019\session-journal.json`; log: `%LocalAppData%\MosWord2019\Logs`.
   **Mong đợi:** không có file cá nhân nào bị thay đổi để chuẩn bị test. Không kết thúc WINWORD theo tên; không đóng/kết thúc tài liệu Word không thuộc phiên trainer.

## Output và provenance

3. Khi chưa có Memo.txt trong Documents thử nghiệm, mở P09, Go, chuyển T08 và Grade. Sau đó Save As tài liệu Word thuộc trainer thành Documents\Memo.txt, chọn Plain Text và chấp nhận hộp thoại chuyển đổi mặc định; quay lại Grade.
   **Mong đợi:** trước export Incorrect/Sai; sau export Correct/Đúng. Grade không tự trả lời hoặc xuất file. File TXT đang mở không bị xóa.
4. Với Memo.txt đang là tài liệu thuộc trainer, chuyển project hoặc đóng trainer bình thường. Kiểm tra sau khi Word đã đóng.
   **Mong đợi:** Memo.txt mới của phiên, còn nguyên identity/hash đã nhận, được xóa; không xóa khi Word vẫn giữ file. Nếu đang khóa, file còn lại và claim vẫn ở journal, log ghi lý do.
5. Tạo trước một Memo.txt thử nghiệm; Go P09 lại rồi Grade T08 mà không export.
   **Mong đợi:** Incorrect/Sai. Export đúng đè lên cùng nội dung rồi Grade lại: Correct/Đúng nếu identity đổi hoặc LastWriteUtc mới hơn. File có trước baseline vẫn được giữ khi đóng, kể cả sau ghi đè. Dọn riêng file thử nghiệm sau khi ghi kết quả.
6. Trong template folder thử nghiệm, tạo Notes.dotx làm sentinel cá nhân và ghi SHA-256. Mở P07, Go và Grade T08, rồi đóng.
   **Mong đợi:** file cũ không đổi không cho PASS; sentinel vẫn tồn tại và giữ hash. Không được nhận sở hữu để xóa. Có thể lặp tương tự P06/Cert.dotx bằng dữ liệu thử nghiệm.
7. Khi Notes.dotx không có trước Go: mở P07, Convert/Save nếu cần, Save As Word Template Notes.dotx trong template folder được phân giải, Grade T08, rồi Save As thêm một file ở thư mục thử nghiệm khác và đóng trainer.
   **Mong đợi:** T08 PASS sau export mới; Notes.dotx vẫn được nhận diện/journal và cleanup sau close dù FullName đã chuyển nơi khác. File Save As ngoài đường dẫn khai báo không bị cleanup.
8. P20-T01: thử file Notes.dotx có trước Go, sau đó làm đúng Save As bằng tài liệu thuộc P20 vào template folder.
   **Mong đợi:** bản cũ không đổi FAIL; bản mới chỉ PASS khi cả provenance lẫn nội dung/template đúng. Bản mới được cleanup sau close. Để kiểm tra stale giữa project, giữ một Notes.dotx bằng khóa thử nghiệm khi đóng P20, giải phóng khóa rồi mở P07 và Grade mà không export: phải FAIL; P07 không xóa file có trước baseline. Không dùng file cá nhân thật cho ca này.
9. P16-T02: Save As TXT vào một thư mục thử nghiệm do người học chọn; đóng/chuyển project.
   **Mong đợi:** kiểm tra nội dung/đường dẫn sở hữu vẫn như cũ; TXT ngoài đường dẫn khai báo được giữ, trainer không scan/xóa thư mục đó.

## Crash, journal và một instance

10. Thử riêng hai lượt: mở P26 để stage assets; hoặc mở P09/P20 để tạo output mới hợp lệ như Memo.txt/Notes.dotx. Kiểm tra journal có claim. Chuyển project có cleanup bình thường nên không dùng thao tác chuyển để gộp hai lượt crash. Trong Task Manager **Details**, xác định đúng PID MosWord2019.exe vừa mở, dùng End task để kết thúc cưỡng bức đúng process trainer (không End process tree, không kết thúc WINWORD cá nhân). Nếu chỉ đóng mềm, ứng dụng có thể cleanup đầy đủ; đó không phải crash test.
    **Mong đợi:** sau kill thật, journal còn. Chỉ đóng cửa sổ Word được trainer mở nếu nó còn giữ file, rồi khởi động trainer lại. Các file claim còn khớp identity/hash được xóa trước Go; file bị khóa vẫn được giữ để thử lại sau. Không dựa riêng tên/hash để nhận một file không có claim.
11. Lặp crash test với từng bản copy thử nghiệm: (a) sửa nội dung asset/output sau khi claim; (b) thay file bằng file khác cùng tên, kể cả cùng bytes; (c) giữ khóa file rồi mở trainer lại, sau đó giải phóng và khởi động lại lần nữa.
    **Mong đợi:** (a)/(b) được giữ; claim đã kết thúc bị bỏ. (c) được giữ và vẫn có claim khi khóa; lần mở sau xóa được nếu identity/hash còn khớp. File không liên quan luôn nguyên vẹn.
12. Khi trainer đã tắt, lưu bản copy journal thử nghiệm rồi tạo journal JSON hỏng có một đường dẫn sentinel thử nghiệm. Mở trainer.
    **Mong đợi:** journal hỏng được chuyển cùng thư mục thành `session-journal.invalid-<UTC yyyyMMddHHmmss>[-suffix].json`, bytes không bị mất; sentinel không bị xóa. Journal mới trống hợp lệ; Go và tạo claim mới hoạt động. Không tự phục hồi claims từ file đã cách ly. Nếu file nguồn bị khóa/reparse, phải fail an toàn, ghi log, không ghi đè.
13. Khi trainer đầu tiên đang mở, chạy executable lần nữa trong cùng phiên Windows.
    **Mong đợi:** instance thứ hai báo song ngữ “already running / đang chạy” rồi thoát; không chạy recovery trên journal của instance đầu. Không thử chạy đồng thời cùng tài khoản qua hai phiên RDP: mutex Local không hỗ trợ kịch bản đó.

## COM, save và đóng ứng dụng

14. Mở một project/tài liệu thuộc trainer, mở hộp thoại Font trong Word. Khi hộp thoại còn mở, bấm Grade bằng cửa sổ trainer (nếu Windows không cho chuyển focus, ghi rõ chưa kiểm thử được; không dùng thao tác đóng Font trước Grade thay cho ca này).
    **Mong đợi:** không crash, không coi Word đã đóng, không reset bài. Sau retry giới hạn khoảng 10 giây, hiển thị “Please close any open dialog in Word and try again.” / “Hãy đóng hộp thoại đang mở trong Word rồi thử lại.” Đóng Font rồi Grade lại: hoạt động bình thường, nội dung vẫn còn.
15. Save hoặc Save As trong Word, theo dõi log trong ít nhất 6 giây; có thể lặp với hộp thoại Word đang bận.
    **Mong đợi:** observation thử lại có backoff, giới hạn 5 giây cho cửa sổ retry; không có khoảng 20 stack trace/giây. Nếu hết hạn vẫn lỗi: tối đa một dòng tổng kết cho lượt đó; journal vẫn còn. Thời hạn này giới hạn các RPC bị từ chối, không phải watchdog cho COM treo vô hạn.
16. Để một hộp thoại Word mở rồi đóng trainer; nếu cleanup thất bại, thử **Retry/Thử lại**, đóng hộp thoại Word và thử tiếp. Lặp lại với lựa chọn **Close Anyway/Đóng ngay**.
    **Mong đợi:** thông báo có lý do; Retry thực sự thử cleanup lại. Close Anyway cho ứng dụng thoát, không hỏi lặp vô tận, không chạy COM lần nữa trong Dispose; journal còn cho lần khởi động sau. Không được xóa file còn bị Word giữ. Nếu Word thuộc trainer còn mở sau Close Anyway, đóng riêng cửa sổ đó thủ công trước khi kiểm tra recovery.
17. Mở trainer với một project, dùng Task Manager tab Processes gửi đóng mềm đúng trainer (khác kill cưỡng bức ở bước10).
    **Mong đợi:** WM_CLOSE đầu không gọi COM đồng bộ; cleanup được hoãn sang UI tick rồi thoát. Không còn vòng “refuse to close” vì `0x8001010D`. Nếu lỗi vẫn xảy ra ở lượt hoãn, người dùng có lựa chọn Retry/Close Anyway; journal không mất.
18. Chỉ trên VM/tài khoản thử nghiệm, mở project rồi thử Windows logoff/shutdown.
    **Mong đợi:** trainer không chặn shutdown, không gọi COM từ luồng đóng đồng bộ, giữ journal để phục hồi lần đăng nhập sau. Không yêu cầu Windows chờ hộp thoại Retry.
19. Lặp các nhánh đại diện với EN và VI: Go, navigation, Grade, Restart No rồi Yes, chuyển project, đóng. Mở riêng một tài liệu Word không thuộc trainer với nội dung chưa lưu làm sentinel.
    **Mong đợi:** navigation/Grade giữ edits; Restart No giữ bài, Yes/Go mới trả starter; sentinel Word không bị đóng, lưu hoặc kill. Lỗi dịch chỉ xuất hiện khi thực sự thiếu key dịch; lỗi journal/ownership khác hiển thị lỗi kỹ thuật, không báo nhầm thiếu bản dịch.

## Ghi kết quả

20. Với mỗi bước, ghi PASS/FAIL/CHƯA THỬ, thời điểm, ngôn ngữ, phiên bản Word, PID trainer/Word sở hữu, hash trước/sau, ảnh chụp và đoạn log liên quan. Không ghi PASS từ test headless thay cho Word thật. Sau kiểm thử, chỉ dọn dữ liệu đã chứng minh do test tạo; giữ journal/quarantine/log khi cần điều tra.
