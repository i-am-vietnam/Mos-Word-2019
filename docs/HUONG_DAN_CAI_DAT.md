# Cài đặt MOS Word 2019

## Máy cần có gì?

- Windows 10 hoặc Windows 11, .NET Framework 4.7.2 trở lên.
- Microsoft Word 2019 desktop hoặc Microsoft 365 desktop đã cài và sử dụng được. Word trên trình duyệt không thay thế được Word desktop.
- Kết nối Internet và tài khoản do người quản lý cấp.

## Cài đặt

1. Nhận `MosWord2019-Setup-1.0.0.exe` từ người quản lý. Đóng MOS Word 2019 nếu đang chạy.
2. Nhấp đúp bộ cài. Nếu SmartScreen hiện cảnh báo vì ứng dụng chưa có chữ ký số, chỉ với tệp từ người quản lý mà bạn tin cậy, chọn **Thông tin thêm → Vẫn chạy**.
3. Chọn **Tiếp theo → Cài đặt**. Bộ cài dùng tài khoản Windows hiện tại, không yêu cầu quyền quản trị. Có thể bỏ chọn tạo lối tắt Desktop; lối tắt Start Menu luôn được tạo.
4. Giữ chọn **Chạy MOS Word 2019**, chọn **Hoàn tất**. Có thể mở lại từ Start Menu hoặc Desktop.

Bộ cài dùng tiếng Việt. Không cần chép thư mục Projects hoặc tự cài chứng thư.

## Đăng nhập lần đầu

Nhập tài khoản, mật khẩu được cấp và chọn ngôn ngữ. Đăng nhập thành công dùng một lượt; xem số lượt còn lại rồi chọn Training hoặc Testing. Một lượt đã chọn chế độ thì không đổi sang chế độ khác. Training có đồng hồ 4 giờ; Testing có thời gian thi riêng. Nếu hết lượt hoặc tài khoản bị khóa, liên hệ người quản lý.

Phiên bản ứng dụng hiện ở cuối màn hình đăng nhập. Cài bản cập nhật bằng bộ cài mới trên cùng tài khoản Windows; dữ liệu cục bộ và kết quả chờ gửi được giữ lại. **Không gỡ bản cũ trước khi cập nhật.**

## Gỡ cài đặt: đọc kỹ trước khi xác nhận

Đóng MOS Word 2019, mở **Cài đặt Windows → Ứng dụng → MOS Word 2019 → Gỡ cài đặt**. Xác nhận tiếng Việt sẽ cảnh báo: **kết quả chưa gửi và dữ liệu khôi phục bài đang làm sẽ bị mất**.

Gỡ cài đặt xóa dữ liệu ứng dụng trong `%LocalAppData%\MosWord2019` và cây thư mục làm bài `Documents\MosWord2019\Working`. Hãy mở ứng dụng khi có Internet, đợi gửi kết quả và nhờ người quản lý xác nhận trước khi gỡ nếu còn bài quan trọng. Bộ gỡ không đi theo junction/symlink; dữ liệu bị khóa hoặc ở đường dẫn không an toàn có thể được giữ lại kèm thông báo. Không tự xóa cả thư mục Documents.

## Lỗi thường gặp

| Thông báo/tình huống | Cách xử lý |
|---|---|
| Cần .NET Framework 4.7.2 | Cài từ liên kết Microsoft mà bộ cài cung cấp, khởi động lại nếu được yêu cầu rồi chạy lại bộ cài. Có thể cần quản trị viên để cài .NET. |
| Không tìm thấy Word desktop | Mở Word từ Start Menu; nếu chưa có, cài/kích hoạt Word 2019 hoặc Microsoft 365 desktop rồi thử lại. |
| Ứng dụng đang chạy | Thoát ứng dụng bình thường trước khi cài/gỡ. Không buộc đóng các cửa sổ Word cá nhân. |
| Không kết nối được máy chủ | Kiểm tra Internet, thử lại; nếu tiếp tục lỗi, liên hệ người quản lý. |
| Chứng thư không khớp | Dừng đăng nhập và liên hệ người quản lý. Không tắt kiểm tra bảo mật hoặc cài chứng thư lạ để bỏ qua lỗi. |
| Mật khẩu sai/hết lượt/khóa/hết hạn | Kiểm tra cách gõ, sau đó liên hệ người quản lý; không thử mật khẩu liên tục. |
| SmartScreen/antivirus chặn | Xác minh nguồn tệp với người quản lý. Không tắt antivirus toàn máy. |
