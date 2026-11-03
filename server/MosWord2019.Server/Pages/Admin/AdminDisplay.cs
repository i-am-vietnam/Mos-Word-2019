using System.Globalization;
using System.Text.Json;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin;

public static class AdminDisplay
{
    public static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    public static string Time(DateTimeOffset? utc) => utc.HasValue
        ? TimeZoneInfo.ConvertTime(utc.Value, Vietnam).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "—";
    public static DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Vietnam).DateTime;
    public static DateTimeOffset Utc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Vietnam));
    public static DateTimeOffset DayStart(DateOnly day) => Utc(day.ToDateTime(TimeOnly.MinValue));
    public static string Status(Account account, DateTimeOffset now) => account.Locked ? "Khóa"
        : account.LockoutUntil > now ? "Khóa tạm" : account.ExpiresAt <= now ? "Hết hạn"
        : account.Used >= account.MaxUses ? "Hết lượt" : "Hoạt động";
    public static string Mode(string? mode) => mode switch { "Training" => "Luyện tập", "Testing" => "Thi thử", _ => "Chưa chọn" };
    public static string Reason(string? reason) => reason switch
    { "Expired" => "Hết hạn hoặc bị thu hồi", "TimeLimit" => "Hết thời gian", "Abandoned" => "Không còn liên hệ", "Completed" => "Hoàn tất", "UserEnded" or "UserExit" => "Người dùng thoát", null => "—", _ => "Đã kết thúc" };
    public static string Flag(string code) => code switch
    { "TimingInconsistent" => "Thời gian bất thường", "ClientClockRollback" or "ClockRollbackDetected" => "Chỉnh lùi giờ", "Network" => "Mất mạng", "Unobserved" => "Không quan sát được", _ => "Cần xem xét" };
    public static string FlagDescription(string code) => code switch
    { "TimingInconsistent" => "Thời gian không nhất quán", "ClientClockRollback" or "ClockRollbackDetected" => "Phát hiện đồng hồ máy học viên lùi", "Network" => "Có khoảng mất mạng", "Unobserved" => "Có khoảng không quan sát được", _ => "Cần xem xét" };
    public static string[] Flags(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    public static string Action(string code) => code switch
    { "AdminBootstrapped" => "Tạo quản trị viên đầu tiên", "LearnerCreated" => "Tạo học viên", "LearnerUpdated" => "Cập nhật học viên", "SessionEnded" => "Kết thúc phiên", "LearnerLogin" => "Học viên đăng nhập", "SessionModeSelected" => "Chọn chế độ phiên", "SessionResumed" => "Tiếp tục phiên", "PackageDownloaded" => "Tải gói bài tập", "PackagesImported" => "Nhập gói bài tập", "ExamDefinitionConfirmed" => "Xác nhận đề thi", "LoginSucceeded" => "Đăng nhập thành công", "LoginFailed" => "Đăng nhập thất bại", "AccountTemporarilyLocked" => "Tạm khóa do đăng nhập sai", "ResultAccepted" => "Nhận kết quả", "ResultUploadConflict" or "ResultConflict" => "Xung đột kết quả", "ResultClockRollback" => "Đồng hồ máy học viên lùi", "TestingTimingFlagged" or "TimingInconsistent" => "Thời gian không nhất quán", _ => "Sự kiện hệ thống" };
    public static string Error(string? error) => error switch
    {
        "Username is already in use." => "Tên tài khoản đã được sử dụng.",
        "Maximum uses cannot be negative." => "Số lượt tối đa không được âm.",
        "Additional uses cannot be negative." => "Số lượt cấp thêm không được âm.",
        "Maximum uses must be at least the used count and cannot exceed 2147483647." => "Số lượt tối đa phải từ số lượt đã dùng đến 2147483647.",
        "Learner account was not found." => "Không tìm thấy tài khoản học viên.",
        "Password must contain 12-128 characters and cannot be blank." => "Mật khẩu phải có 12–128 ký tự và không được để trống.",
        _ => "Tên tài khoản phải có 3–64 ký tự, chỉ gồm chữ A–Z, a–z, chữ số, dấu chấm, gạch dưới hoặc gạch ngang."
    };
}
