# A-01A PublicCloud P0003 Read-Only Diagnosis

## 1. Provenance

- Task: `ET-A01A-PUBLICCLOUD-P0003-READONLY-DIAGNOSIS`, resumed by `ET-A01A-R1-EXCLUDE-KNOWN-LOCAL-ASSETS-AND-RESUME`.
- Git root: `D:/MMO/PhanMemNopThuBaiKiemTra`.
- Branch: `integration/person-a-plus-b`.
- Start HEAD: `3c6a951795b1c14e852dc42228814d9490a651c7`.
- Supabase project ref: `uythsrpriegwwdwnbisi` (linked project, `ACTIVE_HEALTHY`).
- Remote database: PostgreSQL 17, schema compatibility version `23`; newest applied repository migration is `20260731113609`.
- Environment: Windows/PowerShell; Supabase CLI `2.109.1`. The local Supabase stack was not running because the Docker Desktop Linux engine was unavailable. Remote Management API SQL was available.
- Remote SQL safety: every catalog/data query used `BEGIN TRANSACTION READ ONLY` and `ROLLBACK`; `transaction_read_only=on`, isolation `read committed`.
- Date: 2026-08-02 (Asia/Saigon).
- Local hygiene: only the three approved local assets were added to `.git/info/exclude`; the prior exclude was backed up as `.git/info/exclude.et-a01a-backup`. Neither file is tracked.

## 2. Scope and prohibitions

This audit read source, migrations, tests, Supabase catalog metadata, and sanitized production rows. It did not change production source, tests, migration, RPC, RLS, Supabase configuration, project link, or remote data. It did not invoke a mutation RPC, create test data, select an authoritative duplicate session, or begin A-01B.

The only task artifacts are this report and `backend/scripts/diagnostics/a01a-public-room-duplicates-readonly.sql`.

## 3. P0003 source path

### RPC/function

`backend/supabase/migrations/20260727122721_session_first_open_request.sql:135` defines:

`public.join_open_public_session_by_room_code(text,text,text,text,jsonb) -> jsonb`

Inputs are room code, device ID, optional machine name/app version, and a bounded capability JSON object. The function is `SECURITY DEFINER`, has `search_path=''`, requires an active student profile, and is executable only by `authenticated` (`:135-145`, `:147`, `:282-285`). Remote catalog definition matches the repository implementation.

The function normalizes only its input with `upper(btrim(coalesce(p_room_code, '')))` (`:151`). It counts candidate sessions at `:165-172`. `count=0` raises `OPEN_PUBLIC_SESSION_NOT_FOUND` / SQLSTATE `P0002`; `count>1` raises `OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS` / SQLSTATE `P0003` (`:173-176`).

Exact ambiguity predicate:

```text
s.organization_id = authenticated student's profile organization_id
AND s.room_code = upper(trim(input room code))
AND s.access_mode = 'PublicCloud'
AND s.admission_mode = 'OpenRequest'
AND s.status = 'Waiting'
AND s.accepting_participants = true
AND candidate count > 1
```

There is no archived/deleted column predicate, expiry/deadline predicate, exam-owner predicate, participant-visibility predicate, or teacher-owner predicate. Terminal rows are excluded only because their status is not `Waiting`. `organization_id`, `room_code`, `access_mode`, `admission_mode`, `status`, and `accepting_participants` are non-null remotely.

The room comparison is case-sensitive and exact against the stored value after normalizing the input. No database check or trigger normalizes stored room codes. Current affected rows are canonical, so normalization drift did not cause the observed P0003.

### Transaction context

Remote default isolation is `read committed`. The candidate count occurs before any lock. Only after the single-session lookup does the RPC take an advisory transaction lock keyed by the selected session ID and a row lock on that ID (`:179-197`). That lock protects participant creation/capacity for one selected session; it cannot prevent or resolve duplicate room-code producers.

Because the count and selection are separate statements, a concurrent insert can also commit between them. A count of one can therefore be followed by a multi-row non-strict `SELECT INTO`, selecting an unspecified candidate without emitting P0003. This is a second race symptom; it is not a uniqueness mechanism.

### Backend/cloud call path

PublicCloud room-code join does not go through `SessionsController.Join`. The desktop client calls the database RPC directly:

1. `frontend/src/ExamTransfer.Desktop/Infrastructure/SupabasePublicCloudClient.cs:265-301` checks schema compatibility and calls `join_open_public_session_by_room_code`.
2. It retries only `OPEN_PUBLIC_SESSION_NOT_FOUND`, at most three delayed retries (`:292-300`). It does not retry P0003 and does not fall back to LAN.
3. `EnsureSuccessAsync` extracts the PostgREST error code/message and throws `PublicCloudApiException` (`:1070-1111`).

The local backend explicitly rejects a PublicCloud room routed through its LAN join path (`CoreWorkflowPersistenceTests.cs:499-521`), preserving authority separation.

### Frontend mapping

`frontend/src/ExamTransfer.Desktop/ViewModels/StudentConnectViewModel.cs:698-734` maps both `P0003` and `OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS` to the actionable duplicate-room message and typed `JoinMutationFailed`. `frontend/tests/ExamTransfer.Desktop.Tests/PublicCloudRoomJoinTests.cs:40-134` verifies mapping and that no student session mutation is committed.

## 4. Session mutation paths

| Path | Entry/authorization | Normalization and state | Transaction/write | Uniqueness, idempotency, retry, concurrency |
|---|---|---|---|---|
| Create Draft | `POST /api/v1/sessions`; `TeacherOrAdmin`; `SessionsController.cs:16-17` | Custom code uses `RoomCodeRules.Normalize` (trim + invariant uppercase); generated code uses the restricted alphabet. New session is `Draft`, `AcceptingParticipants=true`; access/admission come from request. | `SessionService.CreateAsync` wraps local SQLite create + audit + outbox in `BeginTransactionAsync`; `SessionService.cs:74-82`, `:430-509`. | A local `AnyAsync` rejects the same code for any nonterminal local session, irrespective of organization/access/admission. No database unique index and no request ID. Concurrent/multi-database producers are not serialized by a shared invariant. |
| Create and open | `POST /api/v1/sessions/create-and-open`; `TeacherOrAdmin`; `SessionsController.cs:19-21` | Same create rules, then `Draft -> Waiting`; accepting remains true. | One local transaction writes create, transition, audit, and an `exam_sessions` upsert outbox row; `SessionService.cs:85-112`. | Same precheck gap. This is the path whose paired audit events match all three live duplicate rows. |
| Open existing Draft | `POST /api/v1/sessions/{id}/open`; `TeacherOrAdmin`; `SessionsController.cs:47-48` | State machine permits `Draft -> Waiting`; it does not recheck room uniqueness. | Local transaction, audit, and outbox upsert; `SessionService.cs:158-178`. | A Draft created earlier can become joinable after another machine has published the same code. No shared lock/reservation. |
| Update Draft/Waiting | `PUT /api/v1/sessions/{id}`; `TeacherOrAdmin`; `SessionsController.cs:44-45` | Only planned start, settings, auto-approve, capacity, and optional pending approval change. Room code, access mode, admission mode, status, and accepting flag are not update inputs. | Local EF write + audit + outbox; `SessionService.cs:125-155`. | RowVersion protects the one local row. It cannot create/change a room-code key through this API. |
| Lifecycle close/archive | Teacher/Admin transition endpoints; `SessionsController.cs:49-65` | `Collecting`, `Finished`, and `Cancelled` set accepting false. Finished/Cancelled can archive; no transition reopens a terminal row (`Entities.cs:252-260`, `Domain/Common.cs:18-39`). | Local transaction + audit + same-ID cloud upsert. | Correct lifecycle removes a row from the join predicate, but there is no expiry/automatic closure. Missing a close leaves `Waiting + true` valid indefinitely. |
| Direct authenticated table write | PostgREST table endpoint; RLS insert/update requires same organization and `Admin`/`Teacher`. | Database access/admission and tenant trigger checks apply. No room normalization or lifecycle/uniqueness check applies. | Each REST request is a remote transaction at `read committed`. | Distinct IDs with the same business key are accepted. Authorized direct/manual writes can bypass local prechecks. |
| Cloud outbox push | `CloudSyncWorker.cs:40-139` -> `SupabaseCloudAdapter.PushAsync`; organization is injected from configured authenticated cloud context (`SupabaseCloudAdapter.cs:1362-1394`). | Payload contains normalized local room code and current state. | REST insert with `on_conflict=id`; if the ID exists, optimistic update by ID/cloud version (`:1316-1359`). | Retry/coalescing reuses the same session ID. It does not by itself clone a session, but distinct local session IDs with the same room business key all succeed. |
| Projection retry | `POST /api/v1/sessions/{id}/cloud-projection/retry`; `TeacherOrAdmin`; `PublicCloudProjectionExecution.cs:78-112`. | Resets only the newest non-synced outbox item for the same session ID. | Local queue state write; worker later performs same-ID upsert. | Not a duplicate-session producer by itself. |
| Pull/synchronization | `exam_sessions` is `LocalOwned` in `CloudOwnership.cs:51-68`. | PublicCloud participant/submission data are cloud-owned/source-dependent, but session metadata is pushed from local. | Pull worker does not author a second `exam_sessions` ID. | No evidence that reverse pull created the live duplicates. |
| Reopen/copy session | No production session reopen/clone/copy path was found. | State machine forbids terminal -> Waiting. | Not applicable. | Test fixtures and authorized SQL can insert sessions, but are not runtime application paths. |

The live duplicate sessions use three distinct IDs/exams and three distinct audit traces from the same sanitized host fingerprint. Their timestamps are separated by about 89 minutes and then more than a day. This proves repeated independent create-and-open operations; it does not prove why older local rows were absent from or bypassed the later local precheck.

## 5. Existing database invariants

Remote catalog and repository migrations agree:

- Primary key: unique `exam_sessions(id)` only.
- Foreign keys: organization, exam, and optional class.
- Checks: `access_mode`, `admission_mode`, and workflow snapshot fields.
- Tenant trigger: `trg_public_tenant_consistency` runs before insert/update. It enforces same-tenant exam/class rules and classless OpenRequest (`20260727140650_open_request_tenant_compatibility_final.sql:3-45`); it does not inspect room-code availability.
- RLS: same-organization Admin/Teacher may insert/update/delete; authorized staff/member/participant select is tenant-scoped.
- Index under audit:

```sql
CREATE INDEX ix_exam_sessions_open_public_room
ON public.exam_sessions (organization_id, room_code)
WHERE access_mode = 'PublicCloud'
  AND admission_mode = 'OpenRequest'
  AND status = 'Waiting'
  AND accepting_participants = true;
```

The index originates at `20260727122721_session_first_open_request.sql:27-32`. It exactly matches the join eligibility fields and tenant key, but it is non-unique. It improves lookup speed only.

Missing invariants:

- no unique constraint/partial unique index for active PublicCloud OpenRequest room codes;
- no transaction-safe reservation keyed by organization + normalized room code;
- no storage normalization check for `room_code`;
- no atomic publish RPC/lock keyed by the room business key;
- no expiry or automatic closure invariant for abandoned Waiting sessions;
- no constraint coupling terminal/Collecting status to `accepting_participants=false`.

NULL does not bypass the observed key because organization, room code, access mode, admission mode, status, and accepting flag are non-null. `exam_id` can be null in remote schema, although the tenant trigger rejects an invalid PublicCloud exam relationship.

## 6. Predicate comparison

| Consumer/path | Organization | Code | Mode/admission | Status | Accepting | Difference from join |
|---|---|---|---|---|---|---|
| Local create/generate | Not represented in local session query | normalized exact equality | ignored | all except Archived/Cancelled/Finished | ignored | Broader and local-only; blocks LAN/Draft/in-progress codes in one SQLite DB but cannot see other machines/cloud rows. |
| Create-and-open/open | Same precheck only during create; none during later open | normalized on creation | request value | changes to Waiting | remains true | Opening an existing Draft does not recheck the cloud key. |
| Update | Not applicable | immutable | immutable | only Draft/Waiting allowed for settings update | immutable | Cannot repair or produce key changes through this DTO. |
| Join RPC | Student profile organization | input `upper(btrim())`, stored exact equality | PublicCloud + OpenRequest | exactly Waiting | exactly true | Authoritative ambiguity predicate. |
| Partial index | Included in key | stored raw value | PublicCloud + OpenRequest | exactly Waiting | exactly true | Predicate matches join, but index is non-unique and does not normalize. |
| Dashboard active count | none in local single-tenant DB | ignored | ignored | Waiting/InProgress/Paused/Collecting | ignored | Operational metric, not room eligibility. |
| Dashboard recent sessions | none | ignored | ignored | all statuses | ignored | Recent list is not an active-room predicate. |
| OnlyLAN discovery | local only | normalized exact when supplied | LanOnly + OpenRequest | Waiting | true | Explicitly excludes PublicCloud; no direct A-01B impact if cloud predicate remains partial. |
| SQL tests | fixture-specific tenant | exact uppercase | PublicCloud + OpenRequest | Waiting for join | true | Covers valid/not-found/capacity/tenant paths, not duplicate count or uniqueness. |

## 7. Existing test coverage

| Scenario | Coverage | Classification |
|---|---|---|
| One valid PublicCloud OpenRequest room | `0009_session_first_open_request.sql:149-168`; frontend canonical RPC test | Present; real PostgreSQL pgTAP when local Supabase is available, frontend transport mocked. |
| Room not found / Draft excluded | `0009:169-176`; cross-tenant not-found `:226-229` | Present in pgTAP. |
| Capacity/rejoin/device conflict | `0009:164-184` | Present in pgTAP; session row lock exercised functionally, not concurrently. |
| P0003 frontend mapping/no student mutation | `PublicCloudRoomJoinTests.cs:40-134` | Present, mocked error only. |
| Database P0003 with two candidate rows | None found | Missing. |
| Unique active room invariant | None; no invariant exists | Missing. |
| Concurrent create/publish | None | Missing; requires PostgreSQL concurrency test and preferably two-producer integration coverage. |
| Same code, different organization | Tenant non-disclosure exists, but no two-session same-code acceptance test | Incomplete. |
| OnlyLAN unaffected | `0011_open_request_tenant_compatibility.sql:216-225` and `OpenSessionDiscoveryTests.cs:20-71` | Present for adjacent trigger/discovery behavior, not for a future unique invariant. |
| Draft -> Waiting direct remote update | `0011:226-238` | Present; demonstrates publish can activate a row without room uniqueness validation. |
| Terminal/non-accepting room exclusion | Draft/not-found is covered; terminal matrix and terminal accepting invariant are not | Incomplete. |
| Retry projection | Existing execution tests cover same-ID queue behavior; no room duplicate test | Present for retry behavior, insufficient for business-key uniqueness. |
| Local create room conflict | No direct `ROOM_CODE_CONFLICT` test found | Missing. |

The local Supabase stack/pgTAP was not run because Docker was unavailable. Remote production mutation tests were intentionally prohibited. Current build and .NET regression results are recorded in section 17 after the gates run.

## 8. Remote read-only data findings

Verification status: **verified** on project `uythsrpriegwwdwnbisi` through Supabase Management API SQL. All data queries reported `transaction_read_only=on` and were rolled back.

Summary:

- Active sessions matching the exact RPC predicate: **3**.
- Exact duplicate groups: **1**.
- Affected sessions: **3**.
- Sessions with participants: **1**.
- Sessions with submissions: **0**.
- Sessions with grades: **0**.
- Sessions with quiz attempts: **0**.
- Non-canonical active room codes after trim/uppercase comparison: **0**.
- Terminal PublicCloud sessions with `accepting_participants=true`: **0**.
- Current cross-organization shared active-code groups: **0** (absence of current examples does not change the organization-scoped design).

Duplicate group (sanitized; no student identity):

| Organization | Room | Session | Exam | Created UTC | Updated UTC | Status/accepting | Participants | Submissions | Grades | Quiz attempts |
|---|---|---|---|---|---|---|---:|---:|---:|---:|
| `560f402b-5db4-413f-aa1c-391bbc78fbe0` | `222222` | `56666af9-b930-444f-83cb-dd072a3bdf6e` | `8bf26ff2-e449-458d-99f3-a336c089d997` | 2026-07-30 15:17:41 | 2026-07-30 15:17:41 | Waiting / true | 1 | 0 | 0 | 0 |
| `560f402b-5db4-413f-aa1c-391bbc78fbe0` | `222222` | `a5c5c4e5-6631-4d9d-a868-055f897a6b57` | `2deba7a3-a469-4da0-a7f8-28855ecb28e5` | 2026-07-30 16:46:53 | 2026-07-30 16:46:53 | Waiting / true | 0 | 0 | 0 | 0 |
| `560f402b-5db4-413f-aa1c-391bbc78fbe0` | `222222` | `33367b33-927b-4235-bad6-bf8dcec8ddc0` | `bf3c3481-b7aa-4123-839d-03f13e34b4d1` | 2026-08-01 00:36:45 | 2026-08-01 00:36:45 | Waiting / true | 0 | 0 | 0 | 0 |

All three have null `started_at`/`ended_at`, null exam `created_by`, distinct exam IDs, the same sanitized host fingerprint, and separate audit trace fingerprints. For each row the audit trail is exactly `SessionCreated` (Draft) followed by `SessionStateChanged` (Draft -> Waiting); no later close/cancel/archive event exists remotely.

Why the old session remains valid: the join predicate has no age, planned-start, deadline, owner, or audit-recency check. Each old row still contains exactly `Waiting + accepting=true`, so it remains eligible indefinitely. Source lifecycle would clear accepting on normal closure, but no closure event was recorded for these rows.

The read-only query is preserved at `backend/scripts/diagnostics/a01a-public-room-duplicates-readonly.sql`.

## 9. Race-condition analysis

### Producer race

Teacher/machine A and B can each perform:

1. Read its own SQLite database and see the code as available.
2. Create a distinct local session ID and transition it to Waiting.
3. Commit locally and enqueue an outbox upsert.
4. Independently POST to Supabase with `on_conflict=id`.

The cloud conflict key is only `id`; the business key is not constrained. Both cloud writes can commit at `read committed`. There is no advisory lock keyed by organization + room code, no reservation table, and no unique check at commit. Therefore both can succeed.

The same race exists for two authorized direct cloud transactions and for two preexisting Draft rows transitioned to Waiting. The current live rows were not created concurrently, so race is a confirmed reproducible design defect but not the most likely historical mechanism for this particular group.

### Join race

The RPC counts before locking. Its advisory/row lock is keyed by selected session ID, not the room business key. It serializes participant mutation within one session but neither prevents duplicate sessions nor guarantees P0003 if a new duplicate commits between count and selection.

## 10. Root-cause assessment

| Candidate | Supporting evidence | Counter-evidence / missing evidence | Confidence |
|---|---|---|---|
| Missing remote database invariant | Remote catalog has only primary-key uniqueness; the partial room index is non-unique; three live rows violate the intended business key. | None. | **High / confirmed root cause** |
| Repeated independent create-and-open producers | Three distinct IDs/exams, paired create/open audits, same host fingerprint, separate traces and widely separated times. Same-ID outbox retry cannot explain distinct IDs. | Why the later local prechecks did not see older local rows is not available from remote data. Local database reset/path change/manual local loss remains unproven. | **High** |
| Lifecycle never closes abandoned Waiting sessions | No expiry predicate or automatic close source was found; no close audit exists; rows remain Waiting/true. | A missing external/manual process cannot be ruled out, but no evidence of one exists. | **High contributing cause** |
| Concurrent create/publish race | Source/DDL permit both writers to commit; no shared lock/constraint. | Current rows are hours/days apart, so this group is not evidence of an actual simultaneous race. | **High for vulnerability; low for current history** |
| Inconsistent normalization | Database does not enforce normalized storage. | All three affected raw codes equal normalized `222222`; remote active set has zero non-canonical codes. | **Low for current incident; medium future risk** |
| Projection retry creates a second session | Retry reuses the same entity/session ID and cloud upsert conflicts on ID. | Live rows have distinct IDs and distinct create traces. | **Rejected** |
| Legacy migration/test fixture | Rows were produced after the relevant migrations and carry application-style create/open audits. | No fixture marker or migration event ties them to test data. | **Low** |
| Reverse cloud synchronization | `exam_sessions` is local-owned and pushed, not a cloud-owned pull projection. | No source path creates a new session ID during pull. | **Rejected** |

Root cause: **the application relies on a per-local-database availability check while Supabase lacks an organization-scoped unique active-room invariant. Repeated create-and-open operations can therefore project distinct IDs with the same room key, and abandoned Waiting sessions have no expiry/closure guard.**

## 11. Current data risk

Risk is **production-critical** for remediation, although no submission/grade is currently attached.

- One of the three candidate sessions has a real participant relationship.
- No evidence identifies which session the user intends to keep authoritative.
- The three sessions refer to different exams, so choosing only by newest/oldest can attach future students to the wrong exam.
- Automatic deletion, archive, room-code change, participant move, or flag change is not safe without a user decision and backup.

Classification:

- Session `56666af9-...`: **needs user decision / no-go without backup** because it has a participant.
- Sessions `a5c5c4e5-...` and `33367b33-...`: no dependent participant/submission/grade/quiz attempt, but still **not automatic-safe** until the authoritative exam/session is chosen.

## 12. A-01B options

### Option 1 — Normalized partial unique invariant

Key: organization + normalized/canonical room code. Predicate must be exactly PublicCloud + OpenRequest + Waiting + accepting. A storage-normalization invariant (or an equivalent generated normalized key) must accompany the unique invariant so join and index cannot diverge.

Advantages: database-enforced at commit, race-safe across machines/REST/RPC, organization-scoped, and OnlyLAN excluded. Disadvantages: existing duplicates must be resolved first; a failed outbox projection needs a clear business error; normalization cleanup may expose additional legacy conflicts. Migration risk is high because one live group already violates the proposed invariant. Rollback removes the new invariant only after confirming no producer now depends on it; data cleanup requires a separate reversible plan.

Required tests: duplicate migration preflight, same/different organization, OnlyLAN, terminal/non-accepting rows, normalization variants, two concurrent inserts/activations, outbox error mapping, and rollback.

### Option 2 — Transaction-safe reservation

Reserve organization + normalized room code before local publish, with explicit owner/session ID, expiry, release, and idempotency key. Advantages: can model temporary Draft ownership and expiration. Disadvantages: highest operational complexity, stale reservation cleanup, split-brain risk between local state and cloud, and every producer/direct write must be forced through the reservation protocol. A unique reservation key is still required, so this does not remove the need for a database invariant.

OnlyLAN should not participate. Rollback must stop new reservations, retain audit history, and safely release only reservations with no active session/dependency.

### Option 3 — Atomic publish RPC with room-key lock

Publish/activate through one RPC that validates tenant/role, normalizes code, obtains an advisory transaction lock on organization + normalized code, checks existing active rows, applies an idempotent request ID, and transitions/upserts atomically.

Advantages: typed conflict errors and one transaction boundary; can close the count/select race and coordinate existing Draft activation. Disadvantages: direct table writes must be revoked or separately guarded; advisory-lock correctness depends on every producer using the RPC; more contract/deployment work. Rollback restores the previous producer only after the unique invariant/data state remains safe.

## 13. Recommended A-01B design

Recommendation, once the user selects the authoritative session and a verified backup exists: **Option 1 as the mandatory last-line invariant**, paired with canonical storage normalization. Option 3 can be added for typed publish/idempotency behavior, but must not replace the unique database invariant.

Proposed A-01B sequence (design only):

1. Take and verify a production backup of the affected tables and audit/migration metadata.
2. Record the user's authoritative-session decision for group `organization 560f... / room 222222`.
3. Produce a dry-run dependency manifest and explicit per-session action plan; do not delete/move participant-bearing data automatically.
4. Apply the approved data plan in a separately reviewed transaction with before/after counts and rollback instructions.
5. Enforce canonical room storage and an organization-scoped partial unique invariant matching the join predicate.
6. Map constraint conflict to a typed room-code conflict in the producer/publish path.
7. Run PostgreSQL concurrency, tenant, legacy, OnlyLAN, migration, and rollback tests before staging acceptance.

## 14. Backup and rollback requirements

Backup is mandatory before A-01B. Minimum scope:

- `exam_sessions`, `exams`, `session_participants`, `submissions`, `submission_files`, `grades`, `rubric_scores`, `quiz_attempts`, `quiz_answers`, `audit_logs` for the affected organization/session IDs;
- migration history, constraints/index definitions, and relevant RLS/trigger/function definitions;
- a dependency-count manifest and checksums/timestamps sufficient to detect drift between backup and repair.

Rollback must restore the complete relationship graph, not only `exam_sessions`; verify participant/session ownership, submission/grade counts, room eligibility, and cloud versions after restore. No automatic deletion is acceptable for a session with participant, submission, grade, or quiz attempt data.

## 15. Required tests for A-01B

- one valid active room joins successfully;
- no room returns typed not-found;
- a second active same-organization room/code is rejected at insert and Draft -> Waiting activation;
- concurrent create/publish allows exactly one winner;
- same code in different organizations is allowed if the organization-scoped rule is approved;
- LanOnly uses the same code without being affected;
- non-Waiting or non-accepting PublicCloud rows do not reserve the active key;
- canonical and non-canonical input/storage variants cannot bypass uniqueness or become unjoinable;
- legacy duplicates fail preflight with a deterministic manifest rather than being silently selected;
- P0003 remains for unresolved legacy ambiguity until cleanup completes;
- outbox retry is same-ID idempotent and reports constraint conflict clearly;
- backup restore and migration rollback restore all dependency counts;
- no participant/submission/grade/quiz data is orphaned or reassigned without an explicit approved mapping.

## 16. Decisions required from user

1. Which of the three sessions/exams for room `222222` is authoritative?
2. What approved disposition applies to the other two sessions (close, archive, code change, or another explicit policy)?
3. How should the participant attached to `56666af9-...` be preserved if that session is not authoritative?
4. Is same room code across different organizations explicitly allowed? Current schema/join/index design says yes.
5. Approve the backup/restore evidence and the exact A-01B write scope before any cleanup/migration.

## 17. PASS / FAIL / BLOCKED

**RESULT: BLOCKED — diagnosis complete; A-01B is NO-GO.**

Completed: provenance, source/RPC/producer/DDL/test audit, remote schema match, remote read-only duplicate/dependency query, race analysis, and A-01B design.

Blocker: a real duplicate group contains a participant and there is no evidence-based authoritative-session decision or approved backup/rollback. This is a user/business/data decision, not an A-01A implementation failure.

Execution gates completed on 2026-08-02:

- The committed read-only SQL artifact executed against linked project `uythsrpriegwwdwnbisi` with exit code 0 inside `BEGIN TRANSACTION READ ONLY ... ROLLBACK`; it reconfirmed 3 active candidate sessions, 1 exact duplicate group, 3 affected sessions, 1 session with participants, and 0 with submissions/grades/quiz attempts.
- `dotnet build .\ExamTransfer_Product_FullStack\ExamTransfer.slnx -c Release`: PASS, 0 warnings and 0 errors.
- `dotnet test .\ExamTransfer_Product_FullStack\backend\ExamTransfer.sln -c Release --no-build`: PASS, 253 passed, 0 failed, 1 skipped (254 total).
- Local Supabase/pgTAP execution: SKIP because the Docker Desktop Linux engine was unavailable. Remote read-only catalog/function/data checks and the repository pgTAP coverage audit were completed; this SKIP is not represented as a PASS.

No production source/test/migration/RPC/RLS/config or Supabase data was changed.

## 18. Next task recommendation

Do not start A-01B yet. The next authorized checkpoint should be a reviewed data-decision and backup-verification task for the single affected group. Only after that checkpoint may a separately scoped C4/R3 production-critical A-01B cleanup/invariant task be considered.
