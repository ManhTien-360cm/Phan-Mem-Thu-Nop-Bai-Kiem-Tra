# A-05/A-10 Final Notification and Student Result Contract

## Version and provenance

- Task: `ET-A05-STANDARDIZE-NOTIFICATION-AND-RESULT-CONTRACTS`.
- Result API implementation: `ET-A10-COMPLETE-STUDENT-RESULTS-API`.
- Base commit: `a01664716eaae5ca38c57bedd58b63a8d3011674`.
- Shared assembly: `ExamTransfer.Shared.Contracts`.
- Contract file: `backend/src/ExamTransfer.Shared.Contracts/StudentNotificationAndResultContracts.cs`.
- Serialization: ASP.NET Core web defaults (`camelCase`) plus `JsonStringEnumConverter`, matching LocalServer controllers, SignalR JSON, and the desktop backend client.
- Enum values are JSON strings. No `Unknown` member exists. Unknown strings fail deserialization; undefined numeric values fail validation.
- Scores use `decimal`; timestamps use `DateTimeOffset` and contract validators require UTC offset `00:00`.

A-05 defined the shared contracts. A-10 implements the authoritative OnlyLAN and PublicCloud student-results read paths without changing grading mutations, realtime transports, or Student Results UI/ViewModel code.

## Notification events

All notification payloads use `StudentNotificationEventDto` and must pass `StudentNotificationEventValidator.EnsureValid` before a producer sends them or a consumer accepts them as valid.

| EventType | Purpose | Intended scope | Required event-specific fields | Nullable/optional fields | Data that must not be included |
|---|---|---|---|---|---|
| `ParticipantApproved` | Participant admission approved | Participant | `ParticipantId` | `Message`, `Reason`, score fields | Password, token, device secret |
| `ParticipantAdmissionRejected` | Participant admission rejected | Participant | `ParticipantId` | `Reason` when supplied by the business operation | Password, token, fabricated reason |
| `TeacherMessageReceived` | Teacher message received | Session or participant | non-whitespace `Message` | `ParticipantId` for targeted messages | Broadcast decision, account secrets |
| `SubmissionRejected` | Essay/file submission rejected | Participant | `ParticipantId`, `SubmissionId` | `Reason` when supplied | `AttemptId`, physical/cloud paths |
| `ResubmitAllowed` | Essay/file resubmission allowed | Participant | `ParticipantId`, `SubmissionId` | `Message`, `Reason` | `AttemptId`, authorization tokens |
| `GradeReturned` | Essay/file grade returned | Participant | `ParticipantId`, `SubmissionId` | `Score`, `MaxScore`, `Message` | `AttemptId`, attachment path/key |
| `QuizGradeReturned` | Quiz grade returned | Participant | `ParticipantId`, `AttemptId` | `Score`, `MaxScore`, `Message` | `SubmissionId`, correct-answer content |
| `GradeReopened` | Essay/file grade reopened | Participant | `ParticipantId`, `SubmissionId` | `Reason`; old score is not required | `AttemptId`, attachment path/key |
| `QuizGradeReopened` | Quiz grade reopened | Participant | `ParticipantId`, `AttemptId` | `Reason`; old score is not required | `SubmissionId`, answer key |

Every event also requires non-empty `EventId`, non-empty `SessionId`, a non-default UTC `OccurredAtUtc`, and `Revision >= 0`.

## Notification payload schema

| Field | CLR type | Nullable | JSON name | Validation |
|---|---|---:|---|---|
| `EventId` | `Guid` | No | `eventId` | Non-empty; required JSON property |
| `EventType` | `StudentNotificationEventType` | No | `eventType` | One of the nine named values; required JSON property |
| `SessionId` | `Guid` | No | `sessionId` | Non-empty; required JSON property |
| `ParticipantId` | `Guid?` | Yes | `participantId` | Non-empty when present; required for participant-scoped events |
| `SubmissionId` | `Guid?` | Yes | `submissionId` | Essay/file identity only; non-empty when present |
| `AttemptId` | `Guid?` | Yes | `attemptId` | Quiz identity only; non-empty when present |
| `Message` | `string?` | Yes | `message` | Non-whitespace when present; required for `TeacherMessageReceived` |
| `Reason` | `string?` | Yes | `reason` | Non-whitespace when present; never synthesized by this contract |
| `Score` | `decimal?` | Yes | `score` | `>= 0` when present and `<= MaxScore` when both exist |
| `MaxScore` | `decimal?` | Yes | `maxScore` | `> 0` when present |
| `OccurredAtUtc` | `DateTimeOffset` | No | `occurredAtUtc` | Non-default and UTC offset `00:00`; required JSON property |
| `Revision` | `long` | No | `revision` | `>= 0`; required JSON property |

`SubmissionRejected`, `ResubmitAllowed`, `GradeReturned`, and `GradeReopened` reject `AttemptId`. `QuizGradeReturned` and `QuizGradeReopened` reject `SubmissionId`. This prevents one identity from silently substituting for the other.

## Student result schema

`StudentResultDto` is the shared read contract intended for A-10 and later frontend integration. It must pass `StudentResultValidator.EnsureValid`.

| Field | CLR type | Nullable | JSON name | EssayFile semantics | Quiz semantics |
|---|---|---:|---|---|---|
| `ResultType` | `StudentResultType` | No | `resultType` | `EssayFile` | `Quiz` |
| `ExamId` | `Guid` | No | `examId` | Non-empty | Non-empty |
| `ExamTitle` | `string` | No | `examTitle` | Non-whitespace | Non-whitespace |
| `SessionId` | `Guid` | No | `sessionId` | Non-empty | Non-empty |
| `SubmissionId` | `Guid?` | Yes | `submissionId` | Required and non-empty | Must be null |
| `AttemptId` | `Guid?` | Yes | `attemptId` | Must be null | Required and non-empty |
| `AttemptNumber` | `int` | No | `attemptNumber` | `> 0` | `> 0` |
| `Status` | `StudentResultStatus` | No | `status` | `Graded` or `Returned` | `Graded` or `Returned` |
| `Score` | `decimal?` | Yes | `score` | Non-negative when present | Non-negative when present |
| `MaxScore` | `decimal?` | Yes | `maxScore` | Positive when present | Positive when present |
| `GeneralComment` | `string?` | Yes | `generalComment` | Non-whitespace when present | Non-whitespace when present |
| `ReturnedAtUtc` | `DateTimeOffset?` | Yes | `returnedAtUtc` | Status invariant applies | Status invariant applies |
| `Attachments` | `IReadOnlyList<StudentResultAttachmentDto>` | No | `attachments` | Safe metadata is allowed | Defaults to an empty list when omitted |
| `QuizSummary` | `StudentQuizResultSummaryDto?` | Yes | `quizSummary` | Must be null | Summary is allowed |

`ResultType` is authoritative. Consumers must not infer EssayFile or Quiz from which identity field happens to be null.

## Result status semantics

| Status | Meaning | Student visibility | ReturnedAtUtc |
|---|---|---|---|
| `Graded` | Teacher grading is complete but has not been returned | Must not be shown by default to the student | Must be null |
| `Returned` | The result has been returned to the student | May be returned by A-10 | Required, non-default UTC |

A score does not imply `Returned`. Unknown status values are never mapped to `Returned`.

## Attachment contract

`StudentResultAttachmentDto` contains only:

| Field | Type | JSON name | Validation |
|---|---|---|---|
| `AttachmentId` | `Guid` | `attachmentId` | Non-empty |
| `FileName` | `string` | `fileName` | Non-whitespace filename metadata; path separators and rooted paths are rejected |
| `ContentType` | `string` | `contentType` | Non-whitespace |
| `SizeBytes` | `long` | `sizeBytes` | `>= 0` |

It intentionally has no physical path, `CloudObjectPath`, cache path, service key, signed URL, or download URL. A later download endpoint must authorize by identifier.

## Quiz summary

`StudentQuizResultSummaryDto` contains aggregate values with sources available from the existing grading/review data:

- `TotalQuestions`, `AnsweredQuestions`, `CorrectCount`, `IncorrectCount`, `UnansweredCount`.
- `EarnedPoints`, `MaxPoints` as `decimal`.
- Counts must be non-negative and internally consistent; points must be non-negative, `MaxPoints > 0`, and earned points cannot exceed maximum points.

The summary exposes no question text, selected options, correct options, answer key, or correct-answer content.

## Compatibility

- Existing DTOs and event contracts are retained unchanged: `RealtimeEnvelope<T>`, `RealtimeEvents`, `GradeDto`, `GradeReturnedEvent`, `QuizGradeReturnedEvent`, `StudentQuizReviewDto`, and frontend presentation models.
- The frontend references `ExamTransfer.Shared.Contracts` directly, so no mirror DTO was added to `ServiceContracts.cs`.
- `StudentResultsService` now consumes the A-05 result page for both transports and maps it to the existing presentation model. Notification Center, realtime transports, and Student Results UI/ViewModel remain unchanged.
- A-06 and A-07 should transport `StudentNotificationEventDto` and enforce `StudentNotificationEventValidator` at their contract boundary.
- A-10 returns `StudentResultDto` values inside `StudentResultPageDto`, enforces both result validators, and exposes only `Returned` rows to student callers.

## A-10 student results read API

### Endpoints and response

OnlyLAN uses:

```text
GET /api/v1/student/results
```

The LocalServer response is `ApiResponse<StudentResultPageDto>`. Query parameters are:

- `pageSize`: default `50`, allowed range `1..100`.
- `cursorReturnedAtUtc`, `cursorResultType`, and `cursorResultId`: optional as a group and rejected when incomplete.

PublicCloud uses the typed RPC:

```text
get_student_results(
  p_page_size integer,
  p_cursor_returned_at timestamptz,
  p_cursor_result_type text,
  p_cursor_result_id uuid)
```

It returns `StudentResultPageDto` JSON directly. Both paths sort by `ReturnedAtUtc DESC`, then `ResultType`, then the authoritative result ID. `NextCursor` contains that same tuple, so equal timestamps do not duplicate or skip rows between pages.

### Authentication and identity

- The LocalServer endpoint accepts the authenticated account token under the `Student` policy. It does not accept student, participant, organization, session, or status ownership from query input.
- OnlyLAN resolves the actor from the account `NameIdentifier`, re-checks the persisted user as active `Student`, requires the token organization to match the persisted profile, resolves participants by `SessionParticipant.UserId`, and requires the exam owner to belong to the same organization.
- OnlyLAN reads only `LanOnly` sessions and rejects `PublicCloud` replica rows as a result source. There is no cloud fallback.
- PublicCloud derives the actor from `auth.uid()` through `private.require_active_student()`, then requires matching profile, participant, organization, `PublicCloud` session, exam delivery type, and authoritative result ownership.
- `get_student_results` is `SECURITY DEFINER` because Quiz aggregate verification needs the protected correct-choice graph. It uses `search_path = ''`, schema-qualifies every object, accepts no identity input, is revoked from `PUBLIC`, `anon`, and `service_role`, and is granted only to `authenticated`.
- Existing direct RLS remains defense in depth: students may select only their own Returned grades/attachments and Returned Quiz attempts, and receive no grade/result mutation policy.

### EssayFile mapping

- `ResultType = EssayFile`, `SubmissionId` is the official authoritative submission ID, `AttemptId = null`, and `QuizSummary = null`.
- `AttemptNumber` is the persisted submission attempt number.
- Score, maximum score, comment, and UTC return timestamp come from the authoritative grade whose status is exactly `Returned`.
- Attachments contain only `AttachmentId`, sanitized filename metadata, content type, and size. Physical paths, cache paths, storage bucket, cloud object key, hashes, and signed URLs are not returned.
- Rubric rows remain persisted for grading but are not exposed because A-05 has no rubric-detail field.

### Quiz mapping

- `ResultType = Quiz`, `AttemptId` is the authoritative Quiz attempt ID, `SubmissionId = null`, `Attachments = []`, and `QuizSummary` is required.
- `AttemptNumber` is persisted on `QuizAttempt`/`quiz_attempts`. Existing rows are backfilled to `1` because the pre-A-10 domain already enforced one Quiz attempt per `(session, participant)`; new projection and pull paths carry the persisted field.
- The result must be finalized, have grading status exactly `Returned`, and have a non-null UTC return timestamp.
- OnlyLAN uses the A-09 authoritative scoring service to verify the persisted score and build counts. PublicCloud uses `private.calculate_public_quiz_grade` for the same aggregate verification.
- The summary contains counts and points only. It does not expose question text, selected choice IDs, correct choices, or an answer key. Detailed Quiz review remains a separate endpoint governed by A-09.

### Status and reopen visibility

```text
Graded: never returned by the student-results API
Returned: visible to the owning student
Reopened: disappears immediately and remains absent until returned again
```

The Returned filter is enforced in application mapping/query code and at the PublicCloud RPC/RLS boundary. A score alone never implies Returned. On a later Return, the API reads current authoritative score, comment, timestamp, attachments, and Quiz aggregate.

### Realtime and frontend handoff

- `GradeReturned`, `QuizGradeReturned`, `GradeReopened`, and `QuizGradeReopened` are refresh signals only. The result API remains authoritative.
- Desktop `StudentResultsService.GetReturnedResultsAsync` requests the first bounded page (`50`) from LocalServer for OnlyLAN or from `get_student_results` for PublicCloud, validates A-05, and maps it to the existing presentation model.
- Consumers must use `ResultType`; they must not infer type or Returned state from score or nullable identity fields.
- OnlyLAN attachment download continues through the separately authorized ID route `api/v1/student/submissions/{submissionId}/grade/attachments/{attachmentId}/content`. PublicCloud result payloads provide metadata/IDs only; they do not synthesize a path or long-lived signed URL.
- A-10 does not change Student Results View/ViewModel and does not claim that UI work or a PublicCloud graded-attachment download operation is complete.
- Missing nullable notification fields continue to deserialize as null. Missing `attachments` deserializes to an empty list. Required JSON fields are marked required and missing required fields fail closed.

## Sanitized JSON examples

### TeacherMessageReceived

```json
{
  "eventId": "10000000-0000-0000-0000-000000000001",
  "eventType": "TeacherMessageReceived",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "participantId": null,
  "submissionId": null,
  "attemptId": null,
  "message": "Còn 15 phút để hoàn thành bài.",
  "reason": null,
  "score": null,
  "maxScore": null,
  "occurredAtUtc": "2026-08-02T03:04:05+00:00",
  "revision": 12
}
```

### GradeReturned

```json
{
  "eventId": "10000000-0000-0000-0000-000000000002",
  "eventType": "GradeReturned",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "participantId": "30000000-0000-0000-0000-000000000001",
  "submissionId": "40000000-0000-0000-0000-000000000001",
  "attemptId": null,
  "message": "Kết quả bài tự luận đã được trả.",
  "reason": null,
  "score": 7.5,
  "maxScore": 10,
  "occurredAtUtc": "2026-08-02T03:04:05+00:00",
  "revision": 13
}
```

### QuizGradeReturned

```json
{
  "eventId": "10000000-0000-0000-0000-000000000003",
  "eventType": "QuizGradeReturned",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "participantId": "30000000-0000-0000-0000-000000000001",
  "submissionId": null,
  "attemptId": "50000000-0000-0000-0000-000000000001",
  "message": "Kết quả trắc nghiệm đã được trả.",
  "reason": null,
  "score": 8,
  "maxScore": 10,
  "occurredAtUtc": "2026-08-02T03:04:05+00:00",
  "revision": 14
}
```

### EssayFile Returned result

```json
{
  "resultType": "EssayFile",
  "examId": "60000000-0000-0000-0000-000000000001",
  "examTitle": "Bài tự luận cuối kỳ",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "submissionId": "40000000-0000-0000-0000-000000000001",
  "attemptId": null,
  "attemptNumber": 1,
  "status": "Returned",
  "score": 7.5,
  "maxScore": 10,
  "generalComment": "Lập luận rõ ràng.",
  "returnedAtUtc": "2026-08-02T03:04:05+00:00",
  "attachments": [
    {
      "attachmentId": "70000000-0000-0000-0000-000000000001",
      "fileName": "feedback.pdf",
      "contentType": "application/pdf",
      "sizeBytes": 1234
    }
  ],
  "quizSummary": null
}
```

### Quiz Returned result

```json
{
  "resultType": "Quiz",
  "examId": "60000000-0000-0000-0000-000000000002",
  "examTitle": "Bài trắc nghiệm cuối kỳ",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "submissionId": null,
  "attemptId": "50000000-0000-0000-0000-000000000001",
  "attemptNumber": 2,
  "status": "Returned",
  "score": 8,
  "maxScore": 10,
  "generalComment": "Hoàn thành tốt.",
  "returnedAtUtc": "2026-08-02T03:04:05+00:00",
  "attachments": [],
  "quizSummary": {
    "totalQuestions": 10,
    "answeredQuestions": 9,
    "correctCount": 8,
    "incorrectCount": 1,
    "unansweredCount": 1,
    "earnedPoints": 8,
    "maxPoints": 10
  }
}
```
