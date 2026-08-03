# A/B Integration Map

## 1. Provenance

- BASE_COMMIT: `db55788ece6973e321c9cad06904dc997db0cb6a`
- Person B code tip: `686d77eb2ed6ea6fc444c43292b348c01b3a7453`
- Person B handoff tip: `ade9f87ebe889a37900888310a450db81ae12f89`
- Merge commit: `f62ba1742d8b92021424c9015070fd4b17c27eeb`
- Test repair commit: `bea98e8d5aed8eeb1457dec724a8729811e95df6`
- Current branch/head audited: `integration/person-a-plus-b` / `bea98e8d5aed8eeb1457dec724a8729811e95df6`
- Business specification: `D:\MMO\PhanMemNopThuBaiKiemTra\Kế hoạch Người A — Backend, Dữ liệu, PublicCloud và Realtime.docx`

## 2. Person B commit inventory

| Slice | Commit | Nội dung |
|---|---|---|
| B-01 | `da3a381` | Giải thích lỗi mã phòng PublicCloud không rõ ràng |
| B-02 | `d43b2b5` | Dừng countdown khi session đã terminal |
| B-03 | `bd75c07` | Chọn nhiều submission |
| B-04 | `4444b5c` | Download các submission đã chọn |
| B-05 | `ddc741a` | Notification center dạng popup |
| B-06 | `f5018e6` | Route sự kiện học sinh vào notification center |
| B-07 | `8e96935` | UI chấm essay/file |
| B-08 | `8799284` | UI review/chấm quiz |
| B-09 | `686d77e` | Trang kết quả đã trả của học sinh |

Hai commit tài liệu bao quanh chuỗi B là `82cf332` và `ade9f87`; chúng không phải B-01 đến B-09.

## 3. Locked frontend files

28 production files do Người B thêm/sửa được khóa đối với các task backend A. Chỉ một task tích hợp được phê duyệt riêng mới được thay đổi file thuộc nhóm “shared boundary” ở mục 4.

**Models và services**

- `frontend/src/ExamTransfer.Desktop/Models/NotificationItem.cs`
- `frontend/src/ExamTransfer.Desktop/Models/StudentResultPresentationModel.cs`
- `frontend/src/ExamTransfer.Desktop/Services/INotificationCenter.cs`
- `frontend/src/ExamTransfer.Desktop/Services/LocalFileLauncher.cs`
- `frontend/src/ExamTransfer.Desktop/Services/NotificationCenter.cs`
- `frontend/src/ExamTransfer.Desktop/Services/ServiceContracts.cs`
- `frontend/src/ExamTransfer.Desktop/Services/StudentResultsService.cs`

**ViewModels và presentation/coordinator**

- `frontend/src/ExamTransfer.Desktop/ViewModels/DashboardViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/EssayGradingPresentation.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/GradingCenterViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/MainViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/NotificationCenterViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/ProductModules.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/QuizReviewPresentation.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/StudentConnectViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/StudentQuizViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/StudentRealtimeNotificationAdapter.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/StudentResultsViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/SubmissionBatchDownloader.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/SubmissionSelectionRow.cs`

**Views**

- `frontend/src/ExamTransfer.Desktop/Views/DashboardView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/GradingCenterView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/MainWindow.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/NotificationCenterView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/NotificationCenterView.xaml.cs`
- `frontend/src/ExamTransfer.Desktop/Views/StudentResultsView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/StudentResultsView.xaml.cs`
- `frontend/src/ExamTransfer.Desktop/Views/SubmissionCenterView.xaml`

9 frontend test files của B cũng phải được bảo toàn, trừ khi test tương ứng nằm trong WRITE SCOPE của task sau: `DashboardCountdownTests.cs`, `EssayGradingTests.cs`, `NotificationCenterTests.cs`, `PublicCloudRoomJoinTests.cs`, `QuizGradingTests.cs`, `StudentRealtimeNotificationRoutingTests.cs`, `StudentResultsTests.cs`, `SubmissionDownloadTests.cs`, `SubmissionSelectionTests.cs`.

## 4. Shared integration files

Các file sau là biên tích hợp, không phải quyền sửa mặc định của task backend:

- Shared contracts: `backend/src/ExamTransfer.Shared.Contracts/Events.cs`, `Dtos.cs`, `UnifiedGrading.cs`.
- Frontend contract adapter: `frontend/src/ExamTransfer.Desktop/Services/ServiceContracts.cs`.
- OnlyLAN realtime transport: `frontend/src/ExamTransfer.Desktop/Infrastructure/RealtimeService.cs`.
- PublicCloud realtime transport: `frontend/src/ExamTransfer.Desktop/Infrastructure/SupabaseRealtimeService.cs`.
- PublicCloud API client: `frontend/src/ExamTransfer.Desktop/Infrastructure/SupabasePublicCloudClient.cs`.
- Student result integration: `frontend/src/ExamTransfer.Desktop/Services/StudentResultsService.cs`.
- Dashboard data-binding boundary: `backend/src/ExamTransfer.Shared.Contracts/Dtos.cs` → `frontend/src/ExamTransfer.Desktop/ViewModels/DashboardViewModel.cs`.

Contract phải additive/compatible. Không xóa DTO/event cũ, không để frontend gửi `StudentId`, không đưa service key vào frontend, và không sửa View/ViewModel trong task backend.

## 5. A-01 through A-10 matrix

### A-01 — PublicCloud P0003

- **Requirement:** chẩn đoán dữ liệu trùng active room code, xử lý dữ liệu an toàn, rồi áp invariant transaction-safe để một organization không có nhiều PublicCloud/OpenRequest/Waiting session nhận join cùng room code.
- **Current implementation:** `join_open_public_session_by_room_code` đếm candidate và ném `P0003` khi `count > 1`; sau đó mới khóa session. `SessionService` chỉ `AnyAsync` trước insert; `AppDbContext` có index `RoomCode` không unique.
- **Confirmed gap:** không có database invariant/reservation chống race và pgTAP chưa kiểm tra concurrent duplicate. Chưa có bằng chứng live về số duplicate hiện tại.
- **Backend files:** `SessionService.cs`, `AppDbContext.cs`; entry `SessionsController`/session create + join RPC.
- **Runtime/persistence:** controller → `ISessionService`/`SessionService` → `ExamSession` in local EF; student PublicCloud join goes frontend Supabase client → database RPC → `exam_sessions`/`session_participants`.
- **Contract files:** error mapping trong `SupabasePublicCloudClient.cs`; không cần đổi contract ở bước A-01A.
- **Migration/RPC/RLS files:** `20260727122721_session_first_open_request.sql` và migration mới chỉ sau audit; giữ `SECURITY DEFINER`, `search_path=''`, tenant/auth checks.
- **Environment/change control:** A-01A cần Supabase read-only; migration/race pgTAP cần local Supabase/Docker. Data repair cần production backup và rollback được duyệt.
- **Existing tests:** `0009_session_first_open_request.sql`, `OpenSessionDiscoveryTests.cs`, `PublicCloudRoomJoinTests.cs`.
- **Required tests:** read-only duplicate query; uniqueness/race test; same code ở tenant khác; terminal/non-OpenRequest được phép; mapping P0003 không suy giảm.
- **Person B dependency / locked files:** B-01 chỉ hiển thị lỗi; khóa `StudentConnectViewModel.cs`, `PublicCloudRoomJoinTests.cs` ngoài scope.
- **Dependency:** A-01A audit trước cleanup/migration A-01B.
- **Risk / class / capability / reasoning:** PRODUCTION-CRITICAL; T4; C4; R3 cho migration/data repair. A-01A read-only là T2/C2/R2.
- **PASS:** audit định lượng được duplicate, định nghĩa invariant và backup/rollback; migration sau đó chặn race mà không phá tenant hợp lệ.
- **FAIL:** chỉ bắt P0003 ở client, chọn ngẫu nhiên một session, hoặc xóa dữ liệu không có backup.
- **BLOCKED:** thiếu quyền/query live cho A-01A; thiếu quyết định xử lý duplicate hoặc backup/rollback cho migration.

### A-02 — Dashboard active session

- **Requirement:** active là `Waiting/InProgress/Paused/Collecting`; `Finished/Cancelled/Archived` chỉ nằm history. Dashboard phải có active projection riêng.
- **Current implementation:** `SystemService.GetDashboardAsync` tính `ActiveSessionCount` đúng nhưng DTO chỉ có `RecentSessions`; B-02 lấy `RecentSessions.FirstOrDefault()` làm `ActiveSession` và chỉ dừng countdown cho terminal item.
- **Confirmed gap:** một recent terminal session có thể che session active cũ hơn. Requirement và implementation xung đột ở contract/binding; cần quyết định tích hợp cho phép cập nhật file B khóa.
- **Backend files:** `SystemService.cs`, `SystemController.cs`, `AppDbContext.cs`; entity `ExamSession`.
- **Runtime/persistence:** `GET /api/v1/system/dashboard` → `ISystemService`/`SystemService` → local EF `ExamSessions`; frontend backend client → `DashboardViewModel`.
- **Contract files:** `Dtos.cs` (`DashboardSummaryDto`), sau đó adapter/binding B theo task được phê duyệt.
- **Migration/RPC/RLS files:** không cần migration/RPC/RLS.
- **Environment/change control:** không cần Docker/Supabase hay production backup; local build/test đủ cho backend slice, còn binding cần task integration riêng.
- **Existing tests:** `CoreWorkflowPersistenceTests.cs`, `DashboardCountdownTests.cs`.
- **Required tests:** latest terminal + another active; từng active/terminal status; null khi không active; recent history vẫn giữ.
- **Person B dependency / locked files:** B-02; `DashboardViewModel.cs`, `DashboardView.xaml`, `DashboardCountdownTests.cs` khóa trong task backend.
- **Dependency:** contract backend trước integration binding.
- **Risk / class / capability / reasoning:** MEDIUM; T2; C2; R2.
- **PASS:** active card lấy projection active riêng và terminal chỉ ở history; regression countdown PASS.
- **FAIL:** lọc bỏ history hoặc tiếp tục dùng `RecentSessions.First`.
- **BLOCKED:** chưa cấp WRITE SCOPE cho shared contract hoặc integration binding B.

### A-03 — Download submission OnlyLAN

- **Requirement:** teacher/admin đã xác thực chỉ tải official `Completed` submission trong session/organization được sở hữu; path an toàn, stream metadata đúng, không mutation.
- **Current implementation:** route file trong `SubmissionsController` dùng student scope, thiếu `TeacherOrAdmin`; `SubmissionService.GetFileAsync` kiểm tra completed/file nhưng chỉ `StartsWith(root)` và không xác minh teacher organization. `SubmissionPreviewService` có mẫu ownership + `Path.GetRelativePath` an toàn hơn.
- **Confirmed gap:** teacher batch download B-04 không có endpoint authorization đúng; containment prefix có thể sai ở sibling-prefix path.
- **Backend files:** `SubmissionsController.cs`, `SubmissionService.cs`, `SubmissionPreviewService.cs`, `Abstractions.cs`; entities `Submission`, `SubmissionFile`, `ExamSession`, `Exam`.
- **Runtime/persistence:** teacher UI → local HTTP controller → `ISubmissionService` → EF metadata + filesystem dưới `IStoragePaths.RootPath`; không có cloud hop.
- **Contract files:** giữ URL/download response tương thích với `SubmissionBatchDownloader`.
- **Migration/RPC/RLS files:** không cần.
- **Environment/change control:** không cần Docker/Supabase/migration/production backup; cần filesystem integration tests trong temp root.
- **Existing tests:** `LanAndSubmissionPolicyTests.cs`, `OnlyLanWorkflowCharacterizationTests.cs`, frontend `SubmissionDownloadTests.cs`.
- **Required tests:** teacher owner success; cross-org/participant denial; nonofficial/noncompleted denial; traversal/sibling-prefix; correct name/MIME; GET không mutation.
- **Person B dependency / locked files:** B-03/B-04; khóa `SubmissionBatchDownloader.cs`, selection files và `SubmissionCenterView.xaml`.
- **Dependency:** có thể làm trước A-04; nên tách shared download authorization/cache abstraction để A-04 dùng lại.
- **Risk / class / capability / reasoning:** HIGH (authorization + filesystem); T3; C3; R2.
- **PASS:** authorized OnlyLAN file stream đúng và negative security tests PASS.
- **FAIL:** mở teacher policy nhưng bỏ ownership, hoặc fallback đến path tùy ý.
- **BLOCKED:** ownership rule giữa creator/admin/organization chưa được xác nhận.

### A-04 — Download submission PublicCloud

- **Requirement:** cùng endpoint download, dispatch theo `Session.AccessMode`; tải object vào temp, xác minh size/hash, atomic rename, cleanup; tuyệt đối không LAN fallback.
- **Current implementation:** `SubmissionService.GetFileAsync` chỉ local. `ICloudAdapter.DownloadObjectAsync`/`SupabaseCloudAdapter` có temp + move + cleanup nhưng không xác minh expected size/hash và chưa được submission download gọi.
- **Confirmed gap:** chưa có PublicCloud retrieval/cache contract; frontend batch downloader gọi local endpoint nên PublicCloud hiện không hoàn tất.
- **Backend files:** `SubmissionService.cs`, `SubmissionsController.cs`, `ICloudAdapter`/`Abstractions.cs`, `SupabaseCloudAdapter.cs`, storage paths/cache component.
- **Runtime/persistence:** cùng local HTTP endpoint → access-mode dispatcher → Supabase Storage download → verified local cache/temp → HTTP stream; EF metadata chứa object path/hash/size.
- **Contract files:** URL hiện tại nên giữ; không lộ signed/service credential cho frontend.
- **Migration/RPC/RLS files:** thường không cần schema migration; cần kiểm chứng storage RLS/object path trong `202607150005_user_session_storage_policies.sql` và `20260722141147_public_classes_device_control.sql`.
- **Environment/change control:** cần local Supabase/Docker hoặc isolated storage project để kiểm RLS; chỉ cần migration/backup nếu object metadata hoặc policy phải đổi.
- **Existing tests:** `SubmissionDownloadTests.cs`, `PublicCloudHardeningTests.cs`; cloud fake hiện không hỗ trợ download submission.
- **Required tests:** cache hit valid; corrupt/size/hash mismatch refetch; temp cleanup; atomic success; cloud offline/error; no local fallback; cross-org denial; storage policy negative test nếu path đổi.
- **Person B dependency / locked files:** B-04; khóa downloader/UI.
- **Dependency:** dùng authorization/shared boundary từ A-03; có thể song song sau khi boundary chốt.
- **Risk / class / capability / reasoning:** HIGH; T3; C3; R2.
- **PASS:** verified cloud object được stream qua cùng endpoint, không dùng artifact local không xác minh.
- **FAIL:** coi file local tồn tại là authoritative hoặc tải không kiểm hash/size.
- **BLOCKED:** thiếu object-path metadata/credential test environment hoặc storage ownership contract.

### A-05 — Notification/result contracts

- **Requirement:** thêm contract tương thích cho private event và unified returned result; giữ DTO/event cũ.
- **Current implementation:** backend `RealtimeEnvelope<T>` có EventId/SessionId/Sequence/OccurredAtUtc, nhưng frontend `StudentRealtimeNotification` chỉ giữ SessionId/EventName/Revision/TimeExtended/optional ParticipantId/Projection. Generic LAN parser bỏ EventId, OccurredAt và payload. `StudentQuizReviewDto` thiếu grading status/ReturnedAt; chưa có unified list DTO.
- **Confirmed gap:** transport B phải tự tạo EventId và `UtcNow`; không chuyển SubmissionId/ResultId/Reason/Message/Score/Max. Result model B phải suy luận Returned qua `CorrectAnswersVisible`.
- **Backend files:** shared contracts only ở slice này: `Events.cs`, `UnifiedGrading.cs`, `Dtos.cs`.
- **Runtime/persistence:** DTO-only slice; không đổi controller/service/entity/persistence. Consumers được triển khai ở A-06 đến A-10.
- **Contract files:** thêm event DTO gồm EventId, EventType, SessionId, ParticipantId, SubmissionId/AttemptId/ResultId, Message/Reason, Score/MaxScore, OccurredAtUtc, Revision; thêm result list/detail/attachment DTO gồm ReturnedAtUtc.
- **Migration/RPC/RLS files:** không ở A-05; schema/RPC consumers nằm A-07/A-10.
- **Environment/change control:** không cần Docker/Supabase/migration/backup; cần build cả backend và frontend + serialization tests.
- **Existing tests:** `TeacherRealtimeTests.cs`, `StudentRealtimeNotificationRoutingTests.cs`, `StudentResultsTests.cs`, contract compilation tests.
- **Required tests:** serialization backward compatibility, missing optional fields, EventId/Revision preservation, ReturnedAtUtc, no caller StudentId.
- **Person B dependency / locked files:** B-05/B-06/B-09; `ServiceContracts.cs`, adapter và result presentation chỉ sửa trong integration slice.
- **Dependency:** trước A-06/A-07/A-08/A-09/A-10 nếu dùng contract mới.
- **Risk / class / capability / reasoning:** HIGH (shared contract); T3; C3; R2.
- **PASS:** additive contract build được cả backend/frontend và round-trip không mất identity/time/revision.
- **FAIL:** rename/xóa contract cũ hoặc để client tự phát sinh authoritative identity/timestamp.
- **BLOCKED:** chưa chốt ResultId semantics giữa file submission và quiz attempt.

### A-06 — Realtime OnlyLAN

- **Requirement:** publish private participant event sau save/audit/outbox cho approve/reject, submission reject/resubmit, grade returned/reopened; không internet/Supabase.
- **Current implementation:** SignalR publisher tạo full envelope và participant group. Approve, SubmissionRejected, GradeReturned, QuizGradeReturned được publish. `LanSessionParticipantMutationHandler.RejectAsync`, `LanSubmissionMutationHandler.AllowResubmitAsync`, `GradeService.ReopenAsync`, `QuizGradingService.ReopenAsync` không publish; GradeReturned payload không chứa participant identity.
- **Confirmed gap:** bốn luồng thiếu event; generic frontend parser làm mất envelope/payload. Ordering hiện không nhất quán vì một số publish sau DB/audit/outbox, một số thao tác không increment session sequence.
- **Backend files:** LAN mutation handlers, `GradeService.cs`, `QuizGradingService.cs`, `SignalRRealtimePublisher.cs`, session sequence handling.
- **Runtime/persistence:** controller/service mutation → EF transaction/state → audit + outbox → session sequence → `SignalRRealtimePublisher` → participant hub group; entities Participant/Submission/Grade/QuizAttempt.
- **Contract files:** contract A-05 + `Events.cs`; frontend transport integration riêng.
- **Migration/RPC/RLS files:** không cần Supabase/migration.
- **Environment/change control:** không cần Docker/Supabase/production backup; cần in-process SignalR/EF tests. Không được gọi cloud adapter trong OnlyLAN cases.
- **Existing tests:** `OnlyLanCharacterizationHarnessContractTests.cs`, `ResubmitAuthorityContractTests.cs`, `UnifiedGradingTests.cs`, frontend routing tests.
- **Required tests:** đúng participant group; event only after durable state/audit/outbox; monotonic revision; payload identity/reason/time; no publish on failure; no cloud call.
- **Person B dependency / locked files:** B-06/B-07/B-08; adapter/MainViewModel khóa trong backend slice.
- **Dependency:** A-05 trước; grading event portions có thể đi cùng A-08/A-09.
- **Risk / class / capability / reasoning:** HIGH (ordering/privacy); T3; C3; R2.
- **PASS:** mỗi mutation phát đúng một private event có durable revision, rồi client refresh authoritative state.
- **FAIL:** session-wide grade payload, publish trước commit, duplicate path hoặc local fallback qua Supabase.
- **BLOCKED:** chưa chốt transaction/outbox sequencing strategy.

### A-07 — Realtime PublicCloud

- **Requirement:** private per-participant/device event qua Supabase/RPC/RLS, có EventId/Revision/server timestamp, tenant/user scope, không service key và không LAN fallback.
- **Current implementation:** quiz returned/reopened trigger gửi invalidation riêng tới device topic và RPC có auth/org/version/request-id/audit. Payload cố ý chỉ có eventType/attemptId/sessionId; frontend parser yêu cầu đúng bốn key và chuyển thành string event, không tạo notification contract. Các participant/submission/file-grade event chưa có chuẩn tương đương.
- **Confirmed gap:** thiếu EventId/Revision/OccurredAt/ParticipantId cho grade signal và thiếu coverage cho reject/resubmit/file grade. Realtime parser chỉ special-case quiz/time extension.
- **Backend files:** `SupabaseCloudAdapter.cs`; PublicCloud mutation handlers/projection; frontend transport chỉ ở integration slice.
- **Runtime/persistence:** authenticated frontend/teacher mutation → Supabase RPC → authoritative cloud rows/audit → database trigger/realtime private device topic → client invalidation → authoritative refresh; local projection chỉ read cache.
- **Contract files:** A-05; `SupabaseRealtimeService.cs`, `SupabasePublicCloudClient.cs` là shared integration.
- **Migration/RPC/RLS files:** `20260729002024_public_cloud_quiz_grading_privacy.sql`, teacher mutation migrations, realtime policies; migration mới nếu event contract cần persisted revision/id.
- **Environment/change control:** cần Supabase/Docker + pgTAP; migration cần schema authorization. Production rollout cần backup/rollback nếu thêm cột/table/trigger hoặc đổi RLS.
- **Existing tests:** `0013_public_cloud_quiz_grading_privacy.sql`, `0007_public_cloud_time_realtime.sql`, `PublicCloudTimelineTests.cs`, `StudentRealtimeNotificationRoutingTests.cs`.
- **Required tests:** recipient device receives; peer participant/device denied; session topic contains no grade; EventId/revision/timestamp stable; RPC retry idempotent; no service-role grant/client secret; reconnect refresh.
- **Person B dependency / locked files:** B-06/B-08/B-09; notification/router/result UI khóa.
- **Dependency:** A-05; A-07 trước final PublicCloud notification integration; event producers from A-08/A-09.
- **Risk / class / capability / reasoning:** PRODUCTION-CRITICAL; T4; C4; R3.
- **PASS:** pgTAP + client parser chứng minh privacy, identity, monotonic revision và idempotency.
- **FAIL:** broadcast session-wide, payload chứa grade cho peer, service key frontend, hoặc LAN fallback.
- **BLOCKED:** thiếu Supabase/Docker test environment, schema permission, hoặc quyết định revision source.

### A-08 — Essay/file grading

- **Requirement:** teacher queue/detail/save/return/reopen/attachment cho OnlyLAN và PublicCloud; save khác return; Returned mới hiện cho student; audit/notification/authorization đầy đủ.
- **Current implementation:** `GradeService` có local save/return/reopen/attachment/outbox; `GradingController` cung cấp endpoints. Service không dispatch theo `Session.AccessMode`; PublicCloud không dùng transactional RPC. Reopen không publish; attachment student route có prefix containment yếu. UI B-07 đã có workflow contract hiện tại.
- **Confirmed gap:** PublicCloud authority/idempotency/RLS chưa có; teacher ownership trong `GradeService` không hiện rõ ở từng query; reopen event và safe attachment download thiếu.
- **Backend files:** `GradingController.cs`, `GradeService.cs`, `SubmissionPreviewService.cs`, entities `Grade`, `RubricScore`, `GradedAttachment`, DI/abstractions.
- **Runtime/persistence:** B grading UI → local controller → grading service; OnlyLAN authority là EF/filesystem, PublicCloud authority phải là authenticated RPC/storage; audit/event được phát sau durable mutation.
- **Contract files:** `Dtos.cs`, `Events.cs`, A-05 result/event DTO; giữ B grading request/response compatible.
- **Migration/RPC/RLS files:** grades/rubric/attachment schema + storage policies; cần RPC save/return/reopen PublicCloud và pgTAP nếu Plan A triển khai cloud authority.
- **Environment/change control:** OnlyLAN tests local; PublicCloud cần Docker/Supabase/pgTAP. Migration/RPC/RLS và production backup/rollback là bắt buộc trước cloud rollout.
- **Existing tests:** `UnifiedGradingTests.cs`, frontend `EssayGradingTests.cs`; base RLS có returned-owner read nhưng chưa có full mutation privacy suite.
- **Required tests:** score/rubric validation; concurrency/idempotency; state matrix; cross-org denial; return/reopen events; attachment traversal/storage RLS; four mode/action paths; no PublicCloud local mutation fallback.
- **Person B dependency / locked files:** B-07; khóa grading View/ViewModel/presentation/test.
- **Dependency:** A-05; A-06/A-07 event channel; A-03/A-04 for secure file access.
- **Risk / class / capability / reasoning:** PRODUCTION-CRITICAL; T4; C4; R3.
- **PASS:** both modes preserve one authority, audit, distinct save/return/reopen, private notification, safe attachments.
- **FAIL:** outbox eventual push được coi là PublicCloud mutation authority hoặc Returned bị bypass.
- **BLOCKED:** chưa duyệt schema/RPC/storage changes, Supabase env, backup/rollback.

### A-09 — Quiz grading

- **Requirement:** unified teacher queue/detail, authoritative score, per-question outcome, separate save/return/reopen cho hai modes; PublicCloud qua secure RPC/RLS.
- **Current implementation:** `QuizGradingService` đã dispatch theo `SourceMode`; OnlyLAN uses row version + audit/outbox/private return event; PublicCloud uses request-id, cloud version, row lock, org/auth RPC, audit and private device invalidation. `StudentQuizReviewDto` masks correct answers until returned.
- **Confirmed gap:** OnlyLAN reopen không publish; PublicCloud signal thiếu standard EventId/Revision/time; teacher access helper cho phép access khi organization/owner metadata trống; result DTO lacks status/ReturnedAtUtc. Need verify authoritative score/per-question policy against Plan A.
- **Backend files:** `QuizGradingService.cs`, `GradingController.cs`, `QuizController.cs`, `SupabaseCloudAdapter.cs`, `QuizAttempt` entities.
- **Runtime/persistence:** teacher endpoint → `IQuizGradingService`; OnlyLAN → EF/outbox/SignalR, PublicCloud → adapter/RPC/cloud row/trigger; student review → participant-scoped service/RPC.
- **Contract files:** `UnifiedGrading.cs`, `Events.cs`, A-05 contracts.
- **Migration/RPC/RLS files:** `20260728113000_quiz_grading_and_score10.sql`, `20260729002024_public_cloud_quiz_grading_privacy.sql`, `20260729002300_V23__add_sync_rpc_functions.sql`.
- **Environment/change control:** local path can test without Docker; cloud acceptance requires Supabase/Docker + pgTAP. New schema changes require backup/rollback; contract/event-only hardening may not.
- **Existing tests:** backend `UnifiedGradingTests.cs`; pgTAP `0012`, `0013`; frontend `QuizGradingTests.cs`, `PublicCloudTimelineTests.cs`.
- **Required tests:** fail-closed missing org/owner; all state/version/request-id cases; exact score 0..10; per-question result; reopen notification both modes; contract parity.
- **Person B dependency / locked files:** B-08; khóa quiz presentation/VM/View/tests.
- **Dependency:** A-05 and A-06/A-07 normalization; mostly existing foundation, then feeds A-10.
- **Risk / class / capability / reasoning:** HIGH to PRODUCTION-CRITICAL for RLS; T3/T4; C3/C4; R2/R3.
- **PASS:** parity matrix and negative tenant/device tests all PASS without local fallback.
- **FAIL:** permissive organization fallback, duplicate mutation, leaked correct answers before Returned.
- **BLOCKED:** Supabase/pgTAP unavailable for cloud acceptance or ownership semantics unresolved.

### A-10 — Authenticated returned results

- **Requirement:** unified list/detail for authenticated student across OnlyLAN/PublicCloud × file/quiz; server derives user from token, no caller `StudentId`, Returned only, secure attachment download.
- **Current implementation:** local file endpoint requires known submission ID + participant token and filters Returned. Quiz review requires known attempt ID and can expose score before Returned per quiz policy. B `StudentResultsService` only reads current session/attempt/last submission; PublicCloud file throws integration exception; ReturnedAt for quiz is null and Returned inferred from `CorrectAnswersVisible`. No account-wide list.
- **Confirmed gap:** no unified authenticated-account aggregation, no four-quadrant behavior, no PublicCloud essay/file result/download, and current participant-token route is not account-token list authority.
- **Backend files:** `StudentResultsController.cs`, new application/infrastructure result query service, `GradeService.cs`, `QuizGradingService.cs`, secure attachment service; entities Grade/QuizAttempt/Submission/Participant/User.
- **Runtime/persistence:** authenticated account endpoint derives `auth.uid`/local user claim → unified query service → OnlyLAN EF or PublicCloud RPC → Returned-only DTO; attachment route rechecks result/user ownership before filesystem/storage read.
- **Contract files:** A-05 result DTO with ResultId, SessionId, ParticipantId, ExamTitle, DeliveryType, AttemptNumber, GradingStatus, Score/MaxScore, GeneralComment, ReturnedAtUtc, questions, attachments.
- **Migration/RPC/RLS files:** authenticated PublicCloud result RPC/query + returned-owner RLS and storage attachment policy; likely migration required.
- **Environment/change control:** four-quadrant local tests plus Supabase/Docker/pgTAP. Production RPC/RLS/storage rollout requires backup/rollback and two-user acceptance fixtures.
- **Existing tests:** frontend `StudentResultsTests.cs`; backend `UnifiedGradingTests.cs`; partial RLS in quiz/grading pgTAP.
- **Required tests:** four-mode/type matrix; two-user/cross-org negative cases; Draft/Graded/Reopened absent; no StudentId parameter; ordering/paging; attachment ownership/traversal/cloud object; returned timestamp.
- **Person B dependency / locked files:** B-09; `StudentResultsService`, model, ViewModel, View and tests khóa trong backend slice, then integration task consumes new API.
- **Dependency:** A-05, A-08 and A-09; A-03/A-04 for downloads; A-07 for final notifications.
- **Risk / class / capability / reasoning:** PRODUCTION-CRITICAL (identity/data disclosure); T4; C4; R3.
- **PASS:** identity is token-derived, only own Returned records in all four quadrants, attachments fail closed.
- **FAIL:** caller-controlled student ID, non-Returned leakage, or PublicCloud local fallback.
- **BLOCKED:** account↔participant historical identity semantics, RPC/RLS permission, or backup/rollback not approved.

## 6. B_INTEGRATION_REQUIREMENTS mapping

| B requirement | Primary A task | Supporting task | Mapping |
|---|---|---|---|
| P0003 message/root cause | A-01 | — | B-01 displays the error; A-01 fixes data/invariant, not UI text. |
| Correct active dashboard session | A-02 | — | Backend projection plus separately authorized binding integration. |
| Teacher submission download | A-03 | A-04 | OnlyLAN authorization first/shared; PublicCloud verified cache/object second. |
| EventId, Revision, OccurredAtUtc and payload | A-05 | A-06/A-07 | A-05 defines; transports/producers preserve in mode-specific tasks. |
| ParticipantRejected | A-06 | A-05/A-07 | OnlyLAN producer A-06; PublicCloud parity A-07. |
| ResubmitAllowed | A-06 | A-05/A-07 | OnlyLAN currently saves without publish; cloud parity stays A-07. |
| GradeReturned/GradeReopened file | A-08 | A-05/A-06/A-07 | Grade workflow owns event timing; transports own delivery/privacy. |
| QuizGradeReturned/QuizGradeReopened | A-09 | A-05/A-06/A-07 | Existing base hardened further to normalized contract. |
| Essay/file grading and attachments | A-08 | A-03/A-04 | Grading state plus secure artifact read/storage. |
| Quiz grading/per-question review | A-09 | A-05 | Unified quiz contracts and state security. |
| Account-authenticated result list | A-10 | A-05/A-08/A-09 | A-10 owns aggregation and token-derived identity. |
| No caller `StudentId` | A-10 | A-05 | Enforced in endpoint/RPC signature and tests. |
| Returned-only + ReturnedAtUtc | A-10 | A-08/A-09 | Producers own state/timestamp; aggregator filters. |
| PublicCloud RPC/RLS | A-07/A-08/A-09/A-10 | A-01 where room join | Each domain task owns its RPC/RLS; not collapsed into A-05. |

## 7. Dependency graph

```text
A-01A read-only audit -> A-01B data plan/migration (WAITING)

A-03 OnlyLAN download boundary -> A-04 PublicCloud verified download
                                 -> A-08 attachments -> A-10 result downloads

A-05 additive contracts -> A-06 OnlyLAN realtime
                       \-> A-07 PublicCloud realtime
                       \-> A-08 essay/file grading --\
                       \-> A-09 quiz grading -------+-> A-10 result aggregation

A-07 + A-08/A-09 -> final PublicCloud notification integration
```

A-03 và A-04 có thể song song sau khi authorization/cache interface chung được chốt. A-08/A-09 phải tạo trạng thái Returned đúng trước khi A-10 tổng hợp. Các task migration/RLS đều WAITING cho tới khi có audit, môi trường và quyền tương ứng.

## 8. Conflict-risk matrix

| Boundary | Level | Lý do | Mitigation |
|---|---|---|---|
| A-01 duplicate cleanup/unique invariant | PRODUCTION-CRITICAL | Có thể khóa nhầm/xóa dữ liệu thật; race tạo phòng | A-01A read-only, định lượng, backup/rollback, concurrency pgTAP trước migration. |
| A-02 DTO ↔ B DashboardViewModel | MEDIUM | File B khóa nhưng DTO mới cần consumer | Tách backend contract và integration slice có explicit WRITE SCOPE; giữ `RecentSessions`. |
| A-03/A-04 filesystem/cloud path | HIGH | Traversal, cross-org read, corrupt cache | Relative-path containment, expected hash/size, temp + atomic move, negative auth tests. |
| A-05 shared contracts | HIGH | Break cả backend/frontend và serialization | Chỉ additive, compatibility tests, commit riêng trước consumers. |
| A-06 event ordering | HIGH | Thông báo trước durable state hoặc duplicate | Commit/audit/outbox trước publish; monotonic revision; one-path tests. |
| A-07 realtime/RLS | PRODUCTION-CRITICAL | Rò điểm sang peer/session; service credential | Per-device/private topics, authenticated-only RPC/RLS, pgTAP negative matrix, no service key. |
| A-08 PublicCloud grading | PRODUCTION-CRITICAL | Mất/ghi đè điểm, local/cloud split brain | Cloud-authoritative RPC, request id + expected version, audit and rollback. |
| A-09 quiz privacy | HIGH | Lộ đáp án/điểm trước Returned | Fail-closed ownership, returned masking and peer-device pgTAP. |
| A-10 account results | PRODUCTION-CRITICAL | IDOR/cross-student result disclosure | Token-derived identity, no StudentId argument, Returned filter server-side, cross-user tests. |

## 9. Recommended execution order

1. **READY:** A-01A — read-only diagnosis of duplicate active PublicCloud room codes.
2. **WAITING on A-01A + data decision + backup:** A-01B — cleanup/invariant migration.
3. **READY after explicit backend scope:** A-02 backend active-session projection; frontend binding integration remains separate WAITING scope approval.
4. **READY after ownership rule confirmation:** A-03 secure OnlyLAN download.
5. **WAITING on shared download boundary/object metadata:** A-04 PublicCloud download.
6. **READY after ResultId semantics decision:** A-05 additive contracts.
7. **WAITING on A-05:** A-06 OnlyLAN realtime.
8. **WAITING on A-05 + Supabase environment/schema permission:** A-07 PublicCloud realtime.
9. **WAITING on A-03/A-04/A-05 and cloud authorization:** A-08 essay/file grading.
10. **WAITING on A-05/A-07 hardening:** A-09 quiz grading completion.
11. **WAITING on A-05/A-08/A-09:** A-10 authenticated returned results.

Không task nào ở danh sách này được tự động bắt đầu từ ET-A00-R1.

## 10. Next executable task

**A-01A — read-only diagnosis of duplicate active PublicCloud room codes**

- Task class: T2 diagnostic (nâng T4 khi chuyển sang data repair/migration).
- Capability/reasoning: C2, R2 cho audit; C4, R3 cho bước migration production-critical sau này.
- Read scope: session creation/join implementation, current Supabase schema/migrations/pgTAP, read-only duplicate query and environment metadata đã được cấp quyền.
- Write scope: chỉ báo cáo/audit artifact được chỉ định; **không migration, cleanup, RPC/RLS hoặc production mutation**.
- PASS: định lượng duplicate theo organization + normalized room code + active predicate, truy vết producer/race, đề xuất invariant và kế hoạch backup/rollback có thể review.
- FAIL: suy luận từ P0003 mà không kiểm dữ liệu/schema, hoặc thay client fallback.
- BLOCKED: không có read credential/environment hoặc không xác định được authority/tenant predicate.
- Stop: bàn giao audit; không tự chuyển sang A-01B.
