BEGIN TRANSACTION READ ONLY;

-- 1. Active duplicate check
SELECT
    id, room_code, status
FROM public.exam_sessions
WHERE room_code = '222222'
  AND access_mode = 'PublicCloud'
  AND admission_mode = 'OpenRequest'
  AND status = 'Waiting'
  AND accepting_participants = true;

-- 2. Manifest check
WITH target_sessions AS (
    SELECT id, 'TARGET' as category FROM public.exam_sessions WHERE id IN (
        '56666af9-b930-444f-83cb-dd072a3bdf6e',
        'a5c5c4e5-6631-4d9d-a868-055f897a6b57',
        '33367b33-927b-4235-bad6-bf8dcec8ddc0'
    )
    UNION ALL
    SELECT id, 'NO-TOUCH GUARD' as category FROM public.exam_sessions WHERE id IN (
        '77cb9b9f-42d3-4b3d-807e-6ab6c1060bed',
        '66cc9c26-0a43-427f-9b66-25d9cc778172'
    )
)
SELECT
    s.id AS session_id,
    ts.category,
    s.exam_id,
    s.status,
    s.accepting_participants,
    s.cloud_version,
    (SELECT count(*) FROM public.session_participants p WHERE p.session_id = s.id) AS participant_count,
    (SELECT count(*) FROM public.submissions sub WHERE sub.session_id = s.id) AS submission_count,
    (SELECT count(*) FROM public.audit_logs a WHERE a.entity_id = s.id::text) AS audit_log_count
FROM public.exam_sessions s
JOIN target_sessions ts ON ts.id = s.id;

ROLLBACK;
