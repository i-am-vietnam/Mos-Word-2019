# Rà soát catalog Testing — B0, 2026-10-30

## Phạm vi và nguồn bằng chứng

Rà soát toàn bộ `meta.json`, `tasks.json` và instruction EN của28 package, đối chiếu hợp đồng nguồn/KNOWN_ISSUES; không sửa nội dung package. Catalog duy nhất là `MosWord2019.WinForms/Projects/testing-catalog.json`. Mọi project vẫn eligible; đây là pool cho engine tương lai, **chưa bật Testing**.

Lượt chấm headless mới trên starter nguyên bản: **212 task:3 Pass,201 Fail,8 Error**. Ba Pass là P04-T05/P13-T03/P16-T01, phải loại để tránh điểm tự có. Sáu Error P07-T02–T07 do binary.doc chưa chuyển đổi; hai Error P22-T07/P24-T03 do chưa có live snapshot. Không gọi Error là Fail và không suy chứng minh thao tác native từ OOXML. Bằng chứng chi tiết cục bộ `artifacts/b0/starter-results.json`; kiểm thử tracked tái kiểm tra ba starter Pass và các quyết định catalog.

Các nhóm là all-or-none, gồm phụ thuộc bắc cầu khi chồng lấp. required luôn thuộc subset. Validator chỉ chứng minh tồn tại subset4–6/project và pool7project/34–40task, tối đa một conflictGroup và một legacy.doc. Chưa có composer/random/scoring của B1.

## Quyết định từng project

| Project | Tổng / chọn được | Required; nhóm; loại | Bằng chứng và lý do |
|---|---|---|---|
| P01 | 8 / 8 | — | Tám task độc lập; picture wrap kiểm tra nguồn hiện tại, không dùng baseline Square lịch sử. |
| P02 | 8 / 8 | — | TOC saved field; bảng chuyển đổi và style paragraph độc lập. |
| P03 | 8 / 8 | — | Anchor section/table/SmartArt sẵn có; direction và alt text độc lập. |
| P04 | 8 / 7 | —; loại T05 | Loại T05: starter đã có Continuous break đúng vị trí. Asset Glasses.obj riêng. |
| P05 | 8 / 8 | — | Remove header/footer/watermark không xung đột shape trong body. |
| P06 | 8 / 8 | — | Templates/Cert.dotx; cần snapshot provenance Phase A khi B2 rời project. |
| P07 | 8 / 8 | required T01 | Bắt buộc T01 chuyển binary .doc trước OOXML T02–T07; nhóm xung đột template-notes với P20. |
| P08 | 8 / 8 | — | Revision contract bảo vệ nội dung và cho phép các thay đổi task khác. |
| P09 | 8 / 8 | — | Documents/Memo.txt; B2 phải giữ last OOXML/autosave và snapshot trước khi đóng TXT, rồi mới cleanup. |
| P10 | 17 / 17 | — | 17 task vẫn có quyền chọn như project nhỏ; bảng chèn và bảng Office2019 khác nhau. Không thiên vị kích thước. |
| P11 | 8 / 8 | — | TOC saved field; không cần live layout như P24. |
| P12 | 8 / 8 | — | Bốn asset riêng; Map.png còn copy sang Pictures, cùng ownership ledger. |
| P13 | 6 / 5 | —; loại T03 | Loại T03: starter đã đúng bảng fixed-layout 8×2; năm task còn lại độc lập. |
| P14 | 6 / 6 | —; nhóm [['T03', 'T04']] | Nhóm T03+T04: tạo bullets cho năm paragraph rồi căn chính các bullets đó; task revisions cho phép biến thể hợp lệ. |
| P15 | 6 / 6 | — | Sáu task độc lập, không có file ngoài. |
| P16 | 8 / 7 | —; loại T01 | Loại T01: starter đã modern mode15. T02 cần B2 owned Save As snapshot; folder tùy chọn không thuộc scan/cleanup. |
| P17 | 6 / 6 | — | Sáu task độc lập. Không loại T04: starter hiện tại FAIL, bằng chứng Square/PASS cũ không áp dụng. |
| P18 | 7 / 7 | —; nhóm [['T05', 'T07']] | Nhóm T05+T07: tạo bảng rồi style header bảng đó. T01 Replace All không bắt buộc nhờ exact alternatives. |
| P19 | 5 / 5 | — | Giữ năm stable ID T01,T03–T06; không tạo lại T02. |
| P20 | 11 / 11 | — | 11 task; template-notes xung đột P07. T01 Save As template độc lập với nội dung task còn lại; cần snapshot provenance B2. |
| P21 | 12 / 12 | — | 12 task độc lập; giữ accepted T12 list-level contract. |
| P22 | 7 / 6 | —; loại T07 | Tạm loại T07 FormattingMarksVisible đến khi B2 có owned-window snapshot. T01 sort/T02 style độc lập: ignoreDataRowOrder. |
| P23 | 6 / 6 | — | Sáu task độc lập; personal-information contract cho phép instructed changes và bảo vệ body/table. |
| P24 | 6 / 5 | —; loại T03 | Tạm loại T03 TOC freshness/layout đến B2; các task khác dùng source/import alternatives và accepted native assembly variants. Introduction.docx riêng. |
| P25 | 5 / 5 | — | Năm task độc lập; inherited Banded header/Next Page, exact Strong và line/character spacing giữ nguyên. |
| P26 | 5 / 5 | — | Năm task độc lập; import, logical SmartArt, Sidebar bevel, bullets, watermark. Alias Coffee Sam.jpg cùng bytes với Coffee_Sam.jpg. |
| P27 | 5 / 5 | — | Năm task độc lập; finite table variants cho cả hai thứ tự Sentence case/Replace All, không cần nhóm. |
| P28 | 6 / 6 | — | Sáu task độc lập; source-preservation cho symbol/cover/caption/orientation và cả hai thứ tự cover/orientation. |

## Kiểm kê va chạm theo vị trí đích

Đã kiểm tất cả8 asset bằng tên case-insensitive và SHA-256: P04 `Glasses.obj`; P12 `Certificate.jpg`, `Map.png`, `Microsoft Office 2019 Professional Plus.jpg`, `MOS Exam Instructions - Update Mar 2023.png`; P24 `Introduction.docx`; P26 `Information.docx`, `Coffee_Sam.jpg`. Không có tên asset trùng giữa project. P12 còn Map.png ở Pictures; P26 alias Documents/Coffee Sam.jpg cùng bytes với bản gốc. Không có xung đột khác khi tính cả alias.

| Đích khai báo | Project | Quyết định |
|---|---|---|
| Templates/Cert.dotx | P06 | Không trùng |
| Templates/Notes.dotx | P07, P20 | Khác bài/nội dung; cùng conflictGroup `template-notes`, tối đa một project |
| Documents/Memo.txt | P09 | Không trùng |

P16 TXT ở folder người học chọn không được tự scan/claim/cleanup. Tên starter.docx chung không xung đột vì B2 bắt buộc SessionId/project isolation. Test kiểm kê quét asset mặc định, AdditionalCopies/alias và toàn bộ assertion output khai báo; mọi va chạm mới yêu cầu review catalog.

## Điều kiện engine B2 và giới hạn kiểm chứng

P22-T07 và P24-T03 vẫn bị loại cho đến khi B2 có snapshot formatting marks/TOC pagination cùng path và lifecycle đúng; không tự đưa lại chỉ vì grader hỗ trợ route. Các TOC khác dùng saved OOXML field, không bị loại theo suy diễn. P07 T01 là yêu cầu chuyển đổi trước OOXML, không phải permission auto-answer. Save As (P06/P07/P09/P16/P20) cần engine giữ last OOXML, saved format/path và provenance Phase A; B0 chỉ ghi prerequisite, chưa kiểm chứng engine chưa tồn tại.

Loader catalog kiểm schema/ID/constraints và từ chối trước khi trả dữ liệu. Nó độc lập với ProjectLoader Training: catalog lỗi không ngăn Training nạp bài. Wildcard Projects hiện có tự copy catalog; không thêm entry trùng.28 package/212task/77used assertions được giữ; thêm đúng một file root vào149 file ban đầu.

Không có kiểm tra Word native mới trong B0; Word activation của Phase A bị0x80070520 là bằng chứng lịch sử. B0 chạy logic/headless + desktop suite không mở Word. Checklist thao tác Windows/Word của Phase A được chuyển nguyên nội dung sang `docs/MANUAL_TEST.md`.
