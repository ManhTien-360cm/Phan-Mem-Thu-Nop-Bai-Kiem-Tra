# A-01A2 Authoritative Session Decision Dossier

## 1. Provenance

- Task: `ET-A01A2-AUTHORITATIVE-SESSION-DECISION-DOSSIER`.
- Git root: `D:/MMO/PhanMemNopThuBaiKiemTra`.
- Branch: `integration/person-a-plus-b`.
- Start HEAD: `81dda934d858467a0734ee9f3282bd5c82af930b`.
- Supabase project ref: `uythsrpriegwwdwnbisi`; existing link metadata matched the expected ref.
- Remote query window: `2026-08-01T18:32:16Z` through `2026-08-01T18:36:51Z` (`2026-08-02 01:32:16` through `01:36:51`, Asia/Saigon).
- Remote safety: every catalog/data query ran inside `BEGIN TRANSACTION READ ONLY` and ended with `ROLLBACK`; `transaction_read_only=on` was returned by the duplicate, detailed-profile, room-history, and later-use queries.
- Local database: `C:/ProgramData/ExamTransfer/database/exam-transfer.db`, resolved from the source's runtime-configuration path and the current runtime settings for organization `560f402b-5db4-413f-aa1c-391bbc78fbe0`.
- Local safety: SQLite opened with URI `mode=ro` and `PRAGMA query_only=ON`; no application process was started. The database file and WAL retained their size and modification time. SQLite updated only the modification time of the existing WAL shared-memory file (`exam-transfer.db-shm`) while establishing the read-only reader; no row, schema, DB page, or WAL content was written by the audit.
- Privacy: no student name, email, raw student code, raw device ID, address, token, key, password, connection string, submission content, or uploaded file was read into this dossier. Participant IDs are shortened and device/host values are one-way fingerprints.
- Model/capability/reasoning: `gpt-5.6-sol`, C4, `high` reasoning; the user explicitly approved continuing with the current configuration after the original R3 request.

## 2. Current duplicate state

The exact join predicate still returns one duplicate group for organization `560f402b-5db4-413f-aa1c-391bbc78fbe0`, room code `222222`:

- active predicate sessions: **3**;
- exact duplicate groups: **1**;
- affected active sessions: **3**;
- sessions with participants: **1**;
- submissions / submission files / receipts: **0 / 0 / 0**;
- grades / rubric scores / graded attachments: **0 / 0 / 0**;
- quiz attempts / quiz answers: **0 / 0**;
- returned results: **0**;
- non-canonical active room codes: **0**;
- active fourth session: **none**.

Compared with `A_01A_P0003_DIAGNOSIS.md`, the active duplicate snapshot did **not drift**: the same three IDs remain `PublicCloud + OpenRequest + Waiting + accepting_participants=true`; the same one participant remains on `56666af9-...`; all submission/grading/quiz counts remain zero.

The broader, dependency-scoped room history contains two later **Finished** sessions with the same organization/code. They do not match the active join predicate and are not cleanup candidates:

- `77cb9b9f-...`, exam `lan`: participant joined and was approved; one official one-file submission received a receipt; one grade was returned; session finished normally.
- `66cc9c26-...`, exam `lan`: no participant or submission; session finished normally and is the only room-`222222` session present in the current local database.

This distinction matters: there is no active fourth candidate, but there are five historical rows for the organization/code. Any future repair scoped only by room code could damage valid finished history.

## 3. Side-by-side session comparison

| Session | Exam | Exam title | Exam type | Session created, Asia/Saigon | Status | Participants | Submissions | Grades | Quiz attempts | Local presence | Audit summary |
|---|---|---|---|---|---|---:|---:|---:|---:|---|---|
| `56666af9` | `8bf26ff2` | `test lan` | EssayFile | 2026-07-30 22:17:41 | Waiting / accepting | 1 | 0 | 0 | 0 | No | Created as Draft, immediately opened via `CreateAndOpen`; participant row joined later; no participant audit event |
| `a5c5c4e5` | `2deba7a3` | `jv` | EssayFile | 2026-07-30 23:46:53 | Waiting / accepting | 0 | 0 | 0 | 0 | No | Created as Draft, immediately opened via `CreateAndOpen`; no later event |
| `33367b33` | `bf3c3481` | `lan` | EssayFile | 2026-08-01 07:36:45 | Waiting / accepting | 0 | 0 | 0 | 0 | No | Created as Draft, immediately opened via `CreateAndOpen`; no later event |

All three are classless OpenRequest sessions, have no assignment relationship, use the 10-point application grading contract, and reference Published version-1 exams with duration 60 minutes. The remote `exams` table has no per-exam `max_score` column; score 10 is therefore contract evidence, not an independently stored exam attribute.

## 4. Session 1 detailed profile

### Session and exam

- Session: `56666af9-b930-444f-83cb-dd072a3bdf6e`.
- Exam: `8bf26ff2-e449-458d-99f3-a336c089d997`, title `test lan`, subject `jv`, EssayFile/FileSubmission, Published, version 1, 60 minutes.
- Exam created/updated: `2026-07-30 22:13:55` / `22:14:10` Asia/Saigon.
- Session created/updated: `2026-07-30 22:17:41` / `22:17:41` Asia/Saigon.
- Planned start: `2026-07-30 22:22:41` Asia/Saigon; actual start/end: null/null.
- Access/admission: PublicCloud/OpenRequest; status Waiting; accepting true; auto-approve false; capacity 36; sequence 1.
- Cloud version: `639210214614740900`; host fingerprint: `b65c3e73bc29`.
- Class/assignment/creator: no class, no assignment table relationship, exam `created_by` is null.

### Participant and activity

- Participant count: 1; participant `720eaf21`; device fingerprint `f8a796a1626e`.
- Joined: `2026-07-30 22:18:01` Asia/Saigon.
- Status: PendingApproval; approved/rejected timestamp: none; last seen equals joined time.
- Download status: NotStarted; submission status: NotStarted; resubmit allowed: false.
- There is no reconnect counter and no timestamp evidence of a later reconnect.
- No `ParticipantJoined`, approve, or reject audit row exists for this participant. The join is confirmed by the participant row and its timestamps, not by an audit event.
- Submission/file/receipt/grade/rubric/attachment/quiz-attempt/quiz-answer/returned-result counts are all zero.

### Audit

- `22:17:41.473818`: `SessionCreated`, status Draft, trace fingerprint `4b9d66094b2a`.
- `22:17:41.474226`: `SessionStateChanged`, numeric state `0 -> 1` (Draft -> Waiting), reason `CreateAndOpen`, same trace.
- No later lifecycle audit exists.

## 5. Session 2 detailed profile

### Session and exam

- Session: `a5c5c4e5-6631-4d9d-a868-055f897a6b57`.
- Exam: `2deba7a3-a469-4da0-a7f8-28855ecb28e5`, title/subject `jv`, EssayFile/FileSubmission, Published, version 1, 60 minutes.
- Exam created/updated: `2026-07-30 23:43:04` / `23:43:13` Asia/Saigon.
- Session created/updated: `2026-07-30 23:46:53` / `23:46:53` Asia/Saigon.
- Planned start: `2026-07-30 23:51:53` Asia/Saigon; actual start/end: null/null.
- Access/admission: PublicCloud/OpenRequest; status Waiting; accepting true; auto-approve false; capacity 36; sequence 1.
- Cloud version: `639210268131751600`; host fingerprint: `b65c3e73bc29`.
- Class/assignment/creator: no class, no assignment relationship, exam `created_by` is null.

### Dependencies and audit

- Participant/submission/file/receipt/grade/rubric/attachment/quiz/returned-result counts are all zero.
- `23:46:53.174516`: `SessionCreated`, status Draft, trace fingerprint `4fa7f0fd4adb`.
- `23:46:53.175347`: `SessionStateChanged`, Draft -> Waiting, reason `CreateAndOpen`, same trace.
- No later activity or lifecycle event exists.

## 6. Session 3 detailed profile

### Session and exam

- Session: `33367b33-927b-4235-bad6-bf8dcec8ddc0`.
- Exam: `bf3c3481-b7aa-4123-839d-03f13e34b4d1`, title `lan`, subject `python`, EssayFile/FileSubmission, Published, version 1, 60 minutes.
- Exam created/updated: `2026-08-01 07:34:10` / `07:34:36` Asia/Saigon.
- Session created/updated: `2026-08-01 07:36:45` / `07:36:45` Asia/Saigon.
- Planned start: `2026-08-01 07:41:45` Asia/Saigon; actual start/end: null/null.
- Access/admission: PublicCloud/OpenRequest; status Waiting; accepting true; auto-approve false; capacity 6; sequence 1.
- Cloud version: `639211414054550700`; host fingerprint: `b65c3e73bc29`.
- Class/assignment/creator: no class, no assignment relationship, exam `created_by` is null.

### Dependencies and audit

- Participant/submission/file/receipt/grade/rubric/attachment/quiz/returned-result counts are all zero.
- `07:36:45.454094`: `SessionCreated`, status Draft, trace fingerprint `56efc5913e9a`.
- `07:36:45.455640`: `SessionStateChanged`, Draft -> Waiting, reason `CreateAndOpen`, same trace.
- No later activity or lifecycle event exists.

## 7. Participant-bearing session analysis

The participant on `56666af9-...` proves that a student resolved room `222222` to that session at `22:18:01` on July 30. It does **not** prove that this exam was the room the user ultimately intended to keep:

- the participant remained PendingApproval;
- there is no approval, rejection, download, submission, receipt, grade, quiz answer, or reconnect evidence;
- two later active rows were created for different exams;
- a later finished room `77cb9b9f-...` contains a complete real-use chain: join, approval, start, official submission, receipt, returned grade, and finish.

If `56666af9-...` is not selected, participant `720eaf21` must remain attached to it as historical evidence. No automatic reassignment is permitted. Cancelling/archiving the session must not delete or rewrite the participant row.

## 8. Timeline comparison

All times below are Asia/Saigon (UTC+07:00).

| Time | Session | Confirmed event |
|---|---|---|
| 2026-07-30 22:13:55 | `56666af9` exam | Exam `test lan` created |
| 22:17:41 | `56666af9` | Session created Draft and immediately opened Waiting |
| 22:18:01 | `56666af9` | Participant `720eaf21` joined; remained PendingApproval |
| 23:43:04 | `a5c5c4e5` exam | Exam `jv` created |
| 23:46:53 | `a5c5c4e5` | Session created Draft and immediately opened Waiting |
| 2026-08-01 07:34:10 | `33367b33` exam | Exam `lan` created |
| 07:36:45 | `33367b33` | Session created Draft and immediately opened Waiting |
| 07:51:08 | `77cb9b9f` | Later non-candidate `lan` session created/opened |
| 07:58:45–08:03:16 | `77cb9b9f` | Join, approval, start, official submission with receipt, returned grade, finish |
| 08:13:36–08:31:28 | `66cc9c26` | Later non-candidate `lan` session created, started, and finished without participant |

The first-to-second target gap is about 89 minutes. The second-to-third gap is about 31 hours 50 minutes. The third target was followed about 14 minutes later by the finished session with the complete use chain.

## 9. Evidence of intended use

### Confirmed

- The three target rows are separate create-and-open operations, not retry clones: they have distinct session/exam IDs and trace fingerprints.
- The same sanitized host fingerprint produced all five room-history rows.
- `56666af9-...` received only a pending join.
- `77cb9b9f-...` is the only room-history row with a complete user workflow and returned result. It is Finished and is not one of the three duplicate candidates.
- The current local database contains none of the three target IDs. It contains only later Finished session `66cc9c26-...` for room `222222`, with four Synced `exam_sessions` outbox records and zero participants.
- Remote and current local data agree on `66cc9c26-...` identity, exam, timestamps, status, and absence of participants.

### Inferred

- The three Waiting targets are likely abandoned test/open attempts that were left eligible because no close/expiry invariant exists.
- `33367b33-...` may be an abandoned precursor to the later successful `lan` run because it has the same title and precedes `77cb9b9f-...` by roughly 14 minutes. Title alone is insufficient because the exam IDs differ.
- Absence of the older four remote session IDs from the current SQLite database is consistent with local data reinitialization/path replacement before the current DB snapshot, but the cause is not proven by available data.

### Unknown

- Which exam title/time the user intended when asking to keep an authoritative active session.
- Why the later local create prechecks did not see the earlier remote Waiting rows.
- Whether the pending participant on `56666af9-...` was a deliberate acceptance test or an accidental probe.
- Whether same room codes across organizations are an approved business rule, even though the current join design is organization-scoped.

## 10. Decision options

### Option A — keep `56666af9-...` / exam `test lan`

- Authoritative session/exam: `56666af9-...` / `8bf26ff2-...` (`test lan`).
- Disposition: cancel `a5c5c4e5-...` and `33367b33-...`; archive only after the valid Cancelled -> Archived lifecycle step if history policy requires it.
- Participant handling: keep `720eaf21` on `56666af9-...`; do not auto-approve or move it.
- Advantage: preserves the only participant relationship among the three.
- Risk: that relationship is only a PendingApproval join with no subsequent activity; it predates the confirmed successful run by more than a day and may be a stale probe.
- Backup scope: complete graph for all three targets, plus guard/exclusion manifests for finished `77cb9b9f-...` and `66cc9c26-...`.
- Rollback complexity: medium; two terminal-state repairs plus audit/outbox consistency.

### Option B — keep `a5c5c4e5-...` / exam `jv`

- Authoritative session/exam: `a5c5c4e5-...` / `2deba7a3-...` (`jv`).
- Disposition: cancel/archive the other two through approved lifecycle handling.
- Participant handling: preserve `720eaf21` on cancelled/archived `56666af9-...`; do not transfer it to `a5c5c4e5-...`.
- Advantage: retains the only target whose exam title and subject both identify `jv`.
- Risk: no participant, start, submission, or later activity supports actual use.
- Backup scope: same complete three-target graph and finished-history guard manifest.
- Rollback complexity: medium; participant-bearing history must remain untouched while two target statuses change.

### Option C — keep `33367b33-...` / exam `lan`

- Authoritative session/exam: `33367b33-...` / `bf3c3481-...` (`lan`).
- Disposition: cancel/archive `56666af9-...` and `a5c5c4e5-...` through approved lifecycle handling.
- Participant handling: preserve `720eaf21` on `56666af9-...`; no transfer.
- Advantage: title and time are closest among the targets to the later confirmed `lan` workflow.
- Risk: it still has no participant or start event and uses a different exam ID from the successful `77cb9b9f-...` run; proximity/title do not prove intent.
- Backup scope: same complete three-target graph and finished-history guard manifest.
- Rollback complexity: medium.

### Option D — keep none of the three

- Authoritative session/exam: none among the active duplicate targets.
- Disposition: cancel all three, preserve their history, then archive through the valid lifecycle if approved. Alternatively leave all unchanged and use a different code until a reviewed cleanup exists.
- Participant handling: preserve `720eaf21` on `56666af9-...` as historical PendingApproval evidence; do not move/delete it.
- Advantage: best matches the evidence if the user's real intended run was the completed `77cb9b9f-...` workflow rather than any abandoned Waiting attempt.
- Risk: wrong if the user intentionally wants to reopen one of the three specific exam titles. Creating another room before the uniqueness fix can repeat the producer defect.
- Backup scope: complete three-target graph; include both Finished same-code sessions in the backup or at minimum in a strict exclusion/restore manifest because a room-code-wide repair would otherwise capture them.
- Rollback complexity: medium to high because three lifecycle repairs are required, but there is no submission/grade graph on the targets.

### Supported-action classification

| Proposed action | Current support | Decision implication |
|---|---|---|
| Waiting -> Cancelled | Supported by `POST /api/v1/sessions/{id}/cancel` and the state machine | The current local API cannot execute it for the three remote-only IDs because they are absent from current SQLite; a separately authorized, audited repair path is needed |
| Cancelled/Finished -> Archived | Supported by state machine and bulk archive | Must not skip Cancelled; same remote-only limitation applies |
| `accepting_participants=false` only | Not exposed by `UpdateSessionRequest` | Direct remote data repair or a new audited RPC would be required |
| Change room code | Not exposed by the current update API | Direct repair/new RPC required; changes historical meaning and is not the default |
| Move participant | No approved automatic path | Prohibited without an explicit business mapping; not recommended |
| Leave rows unchanged and use a different new code | Supported without data mutation | Safest temporary operational option, but does not repair the duplicate group |

No production session-lifecycle mutation RPC was found in the current Supabase migrations. Direct SQL repair, a new RPC, or migration belongs to a separately authorized task after backup; none is performed here.

## 11. Auditor recommendation

**NO AUTOMATIC RECOMMENDATION**

Evidence is sufficient to present a safe decision but not to choose on the user's behalf. Use these exact recognizers:

- choose `56666af9-...` only if the intended room was exam `test lan`, created/opened around `2026-07-30 22:17` and the pending join `720eaf21` is the relevant student;
- choose `a5c5c4e5-...` only if the intended room was exam `jv`, created/opened around `2026-07-30 23:46`;
- choose `33367b33-...` only if the intended room was exam `lan`, created/opened around `2026-08-01 07:36`;
- choose **none** if the intended real test was the later `lan` run around `07:51–08:03`, which already exists as Finished session `77cb9b9f-...` with an approved participant, official submission, receipt, and returned grade.

Regardless of the choice, finished sessions `77cb9b9f-...` and `66cc9c26-...` are outside the cleanup target and must remain unchanged.

## 12. User decision form

```text
AUTHORITATIVE SESSION:
AUTHORITATIVE EXAM:
DISPOSITION — 56666af9:
DISPOSITION — a5c5c4e5:
DISPOSITION — 33367b33:
PARTICIPANT HANDLING:
SAME CODE ACROSS ORGANIZATIONS: ALLOW / DENY
REASON:
APPROVED BY:
APPROVED AT:
```

## 13. Backup scope after decision

Before any A-01B cleanup/migration, capture and verify:

1. Full relationship graph for the three target sessions from `exam_sessions`, `exams`, `session_participants`, `submissions`, `submission_files`, `grades`, `rubric_scores`, `graded_attachments`, `quiz_attempts`, `quiz_answers`, and `audit_logs`, including receipt fields and cloud versions.
2. A dependency-count manifest per session and a query timestamp to detect drift between decision, backup, and repair.
3. Definitions/checksums for relevant constraints, indexes, triggers, RLS policies, join RPC, and migration history.
4. Finished sessions `77cb9b9f-...` and `66cc9c26-...` as explicit no-touch guard rows. Include their relationship graphs in the backup if any repair predicate uses organization + room code rather than an exact target-ID allowlist.
5. Current local database identity and exact absence/presence manifest; do not treat current SQLite as a backup of the remote-only targets.

No backup was created by this task.

## 14. Restore verification requirements

- Restore by exact IDs, never by room code alone.
- Verify session/exam/participant/submission/file/receipt/grade/rubric/attachment/quiz/audit counts against the pre-repair manifest.
- Verify participant `720eaf21` remains attached to `56666af9-...` unless a separately approved mapping says otherwise.
- Verify finished `77cb9b9f-...` still has one participant, one official submission with receipt, one Returned grade, and unchanged lifecycle timestamps.
- Verify foreign keys, tenant relationships, cloud versions, audit chronology, and no orphan rows.
- Re-run the exact active-room predicate and confirm the chosen disposition produces the approved candidate count.
- Verify the application can read the restored graph and that rollback does not re-enable an unintended room silently.

## 15. Remaining blockers

- User must fill the Decision Form; no authoritative active session can be selected automatically.
- Backup and restore evidence do not yet exist; no cleanup is authorized.
- The three targets are remote-only relative to the current local DB, so the existing local lifecycle API cannot mutate them by ID.
- There is no current production lifecycle RPC for an audited remote-only repair.
- The database still lacks the transaction-safe uniqueness invariant; even after manual disposition, recurrence remains possible until separately authorized A-01B work.

## 16. Result

**PASS — decision dossier complete; user decision still required.**

- Remote duplicate/data verification: PASS; all queries were read-only and rolled back.
- Local SQLite verification: PASS for logical data; exact runtime database opened query-only, target presence was checked, and no row/schema/DB/WAL content changed. The existing `-shm` reader-coordination file modification time changed as disclosed in provenance.
- Release build: PASS, 0 warnings and 0 errors.
- Backend tests: PASS, 253 passed, 0 failed, 1 skipped (254 total). The skip is the existing real-DOCX fixture `QuizDocumentImportTests.RealUserDocx_ParsesAllFiftyQuestionsAndPreservesKnownAnswers`.
- Production source/test/migration/RPC/RLS/config/data changes: none.
- Backup/restore/data repair: not run.
- Automatic authoritative-session selection: not made.

This PASS means the dossier contains enough current evidence and bounded options for the user to decide. It is not approval for cleanup, migration, A-01B, staging, or production mutation.

## 17. Next task

After the user fills the Decision Form: a separate backup-and-restore-verification task. Do not begin cleanup, data repair, migration/RPC/RLS work, A-01B, or A-02 from this dossier.
