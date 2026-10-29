# Saved-package fixtures

## A2 bổ sung

`a2-nearmiss.json` khai báo cặp đúng/near-miss cho đủ28 task thuộc BodyTextReplaceAll, TextReplaceAll, TextRangeFormattingEquals, Footnote và SymbolInserted. Cả hai dùng nguyên task được ProjectLoader nạp. Có13 near-miss đã lưu từ kiểm chứng trước và15 biến thể OOXML tập trung; provenance phân biệt rõ hai nguồn, thay đổi archive entry và hash. Riêng fixture P11 nén ảnh PNG không mất dữ liệu để dưới2 MB; hash pixel RGBA giải mã bằng nhau. Chỉ bản fixture thay đổi, không sửa tài nguyên package. Hai fixture P20 chuyển content type từ template sang document và ghi rõ trong provenance.

`a2-errors.json` giữ sáu DOCX có byte giống fixture đúng: một dùng cho nhánh ngoại lệ mới và năm dùng cho RequiredString/Int/Array/StringMatrix/Bool. Nhãn Error mô tả exception/context hoặc metadata task sai, không phải đáp án học viên sai. Test clone metadata của task đã nạp và chỉ thay một trường; kiểm tra đối chứng task gốc vẫn Pass và không bị sửa. Callback Documents nội bộ tiêm11 loại ngoại lệ; OOM/StackOverflow chỉ được ném bằng object thử nghiệm, không làm cạn bộ nhớ hoặc đệ quy thật. Các manifest A1 bên dưới giữ nguyên.

`fixtures.json` lưu 158 ca kiểm thử cho đủ 77 `assertionType` đang được 28 package sử dụng: 146 ca cho 73 loại chấm nội dung OOXML, cộng 12 ca context/output cho bốn loại còn lại. Mỗi loại OOXML có một Pass và một Fail trên cùng `projectId`/`taskId` gốc; mỗi loại context/output có Pass, Fail và near miss. Mỗi file DOCX nhỏ hơn 2.000.000 byte. Dữ liệu được đưa vào Git để checkout mới có thể chạy kiểm thử mà không cần Word, Office Interop, GUI hay thư mục `artifacts/`.

Test phải nạp task thật bằng `ProjectLoader` từ package được copy vào output, rồi gọi public `WordGradingService.CheckTask` trên fixture đã lưu. Core chỉ đọc file; output/template cần tạo được copy vào thư mục riêng và dọn sau mỗi ca. Không lấy task từ manifest, không thay metadata kỳ vọng và không sửa package/câu hỏi/starter. `expected` chỉ là kết quả kiểm thử, không tham gia chấm đáp án.

## Nguồn và thay đổi có kiểm soát

`provenance.json` ghi đường dẫn nguồn tương đối repository, SHA-256 nguồn và fixture cuối, matrix/case lịch sử, hash file metadata task gốc, và mọi biến đổi. Các đường dẫn `artifacts/` trong provenance là dấu vết nguồn gốc; runtime không đọc chúng. Các ca giữ lại gồm tài liệu Word đã lưu và các biến thể OOXML bổ sung; không gắn nhãn toàn bộ là thao tác GUI thủ công.

- Hầu hết fixture giữ nguyên byte từ file nguồn đã lưu.
- `InlineModel3D_Pass` nén lại ZIP bằng Deflate để file còn 1.969.512 byte. SHA-256 nội dung từng archive entry, gồm model GLB, giữ nguyên; không thay model để vượt kiểm tra.
- `SmartArtLogicalOrder_Fail` xuất thành saved package từ đáp án native P26 T02. Chỉ `word/diagrams/data1.xml` thay đổi: hoán đổi thứ tự logic của hai cạnh cha đầu tiên, giữ nhãn/ảnh và drawing cache. Ca này dựa trên biến thể “wrong content order and correct cache” trong kiểm chứng P26, sau đó được chạy qua public `CheckTask` với task gốc.
- SmartArt direction dùng cặp P09 T04 đã lưu. Ca P03 lịch sử tên `t05-wrong-direction` hiện trả Pass với metadata gốc nên không được đưa vào tập Fail.

## Context của saved package

- `SavedTemplate`: fixture dùng đuôi `.docx` thống nhất để lưu trữ, nhưng bên trong là template hoặc template near miss. Test copy nguyên byte thành tên `Notes.dotx` trong thư mục riêng rồi cung cấp `DefaultTemplateFolder` và `SavedFromCurrentWorkingDocument=true`. Nhờ vậy negative kiểm tra nội dung macro template, không chỉ sai tên/đường dẫn.
- `Toc`: `tocLayout` trỏ tới snapshot JSON đi kèm trong Fixtures. Snapshot giữ bookmark/text/page đọc từ owned Word trước đây; test phải gắn `DocumentPath` với fixture hiện đang chấm trước khi gọi grader. Snapshot là dữ liệu context được cấp cho Core; kiểm thử headless không tự đo lại phân trang Word.

## Context và output ngoài OOXML

Các hàng sau trỏ tới tài liệu working DOCX đã lưu hợp lệ tại `Fixtures/<ProjectId>/<TaskId>/pass-context.docx`, `fail-context.docx` hoặc `nearmiss-context.docx`. Các bản copy giữ nguyên byte; kết quả được quyết định bởi đúng hợp đồng view/context/output của assertion. Không gán kết quả này thành bằng chứng kiểm tra nội dung OOXML.

- `FormattingMarksVisible`, P22 T07: Pass khớp đủ bảy cờ; Fail tắt Tabs; near miss bật Tabs đúng nhưng bật sai Show All. Các cờ được đưa vào `TrainingGradeContext.FormattingMarks`.
- `PlainTextAtOwnedSavedLocation`, P16 T02: Pass copy nguyên byte TXT native đã lưu vào đúng `Outline for Proposal.txt` và cung cấp SaveFormat 2. Fail giữ đúng TXT/tên nhưng cung cấp sai SaveFormat 12. Near miss giữ đúng tên/SaveFormat 2 nhưng dùng byte DOCX để xác nhận không nhận file OOXML chỉ đổi đuôi. `outputSource` trỏ tới companion TXT trong Fixtures, kèm SHA-256 nguồn và bản copy trong provenance.
- `FileExistsInDocuments`, P09 T08, và `FileExistsInCustomOfficeTemplates`, P06 T08: Pass tạo đúng tên `Memo.txt`/`Cert.dotx`; Fail không tạo file; near miss chỉ tạo tên `Wrong-...`. Test dùng documents-root seam nội bộ để tất cả output nằm trong thư mục riêng. Không ghi vào Documents cá nhân hoặc thay đổi resolver mặc định. Hợp đồng tồn tại file không kiểm tra nội dung output; các ca này không giả lập kiểm tra định dạng template/TXT.

## Kiểm chứng khi đưa vào repository

146/146 expectation OOXML được chạy mới qua public `ProjectLoader.Load` + `WordGradingService.CheckTask`, không gọi private assertion helper, không khởi động Word. Core đọc fixture; output được cô lập và dọn sau mỗi ca. Tất cả cặp Pass/Fail OOXML dùng cùng task metadata gốc và có byte khác nhau. Các ca context/output cố ý giữ cùng byte working DOCX và thay đổi context/output được khai báo rõ trong manifest. Kết quả chi tiết của lượt chuẩn bị nằm ở evidence bị ignore; test trong repository tự kiểm chứng lại từ các file ở đây.
