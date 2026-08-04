# ET-A00 Integrated Baseline

BASE_COMMIT: `db55788ece6973e321c9cad06904dc997db0cb6a`

PERSON_B_CODE_TIP: `686d77eb2ed6ea6fc444c43292b348c01b3a7453`

PERSON_B_HANDOFF_TIP: `ade9f87ebe889a37900888310a450db81ae12f89`

MERGE_COMMIT: `f62ba1742d8b92021424c9015070fd4b17c27eeb`

TEST_REPAIR_COMMIT: `bea98e8d5aed8eeb1457dec724a8729811e95df6`

CURRENT_HEAD: `bea98e8d5aed8eeb1457dec724a8729811e95df6` (HEAD được audit và chạy gate trước commit tài liệu baseline)

BRANCH: `integration/person-a-plus-b`

WORKTREE: Sạch tại đầu task, sau commit sửa test và trước khi tạo hai tài liệu baseline.

RESTORE: PASS. Restore được thực hiện bởi `dotnet build`; không có lỗi package restore.

DEBUG_BUILD: PASS — `dotnet build ExamTransfer.slnx -c Debug`; 0 warnings, 0 errors.

RELEASE_BUILD: PASS — `dotnet build ExamTransfer.slnx -c Release`; 0 warnings, 0 errors.

BACKEND_TESTS: PASS — 253 passed, 0 failed, 1 skipped, 254 total. Test skip là fixture DOCX thực `QuizDocumentImportTests.RealUserDocx_ParsesAllFiftyQuestionsAndPreservesKnownAnswers`, không phải skip mới do R1.

FRONTEND_TESTS: PASS — 301 passed, 0 failed, 0 skipped ở Release, gồm cả final recheck.

ORIGINAL_PERSON_B_FAILURES: Hai failure được giao đều là lỗi portability của test: chuỗi điểm `8.5` không theo `CurrentCulture`, và assertion phụ thuộc câu tiếng Anh cũ trong tài liệu đã Việt hóa. Không có bằng chứng đây là production defect.

NEWLY_DISCOVERED_FAILURES: NONE trong targeted test, full frontend test và Release build đã chạy trước commit test.

REPAIRS_APPLIED:

- `EssayGradingTests`: tạo chuỗi điểm từ `8.5m` bằng `CultureInfo.CurrentCulture`, giữ nguyên kiểm tra command enable và giá trị score gửi xuống service.
- `StudentResultsTests`: kiểm tra marker `B-09` và ba invariant ổn định: danh tính từ tài khoản xác thực, không nhận/gửi `StudentId`, chỉ trả kết quả `Returned`.

PRODUCTION_FILES_CHANGED_BY_R1: NONE

PLAN_A_SOURCE_PATH: `D:\MMO\PhanMemNopThuBaiKiemTra\Kế hoạch Người A — Backend, Dữ liệu, PublicCloud và Realtime.docx`

PLAN_A_TASKS_FOUND: A-01 through A-10

KNOWN_LIMITATIONS:

- Đây là baseline tĩnh/local; chưa truy vấn dữ liệu Supabase thật, chưa chạy migration, Docker/pgTAP hoặc kiểm thử production.
- A-01 mới xác nhận đường phát sinh `P0003` và thiếu invariant chống trùng; số bản ghi trùng hiện có chưa được đo trên môi trường thật.
- A-02 cần contract active-session riêng. `DashboardViewModel` của Người B hiện dùng `RecentSessions.FirstOrDefault()` nhưng là file khóa trong các task backend; thay đổi binding phải được phối hợp ở task tích hợp riêng.
- Download PublicCloud, realtime chuẩn hóa, file/essay grading PublicCloud và unified authenticated result list chưa hoàn chỉnh; chi tiết nằm trong `A_B_INTEGRATION_MAP.md`.
- Mọi kết luận PASS của R1 chỉ là source/build/test local, không phải production readiness.

RESULT: PASS — local source/build/test baseline; không phải production readiness.

Ngày thực hiện: 2026-08-01
