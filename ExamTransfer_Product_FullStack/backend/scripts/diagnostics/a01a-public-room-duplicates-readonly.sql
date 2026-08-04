BEGIN TRANSACTION READ ONLY;

WITH active AS (
    SELECT
        s.*,
        upper(btrim(s.room_code)) AS normalized_room_code
    FROM public.exam_sessions AS s
    WHERE s.access_mode = 'PublicCloud'
      AND s.admission_mode = 'OpenRequest'
      AND s.status = 'Waiting'
      AND s.accepting_participants = true
),
exact_keys AS (
    SELECT
        organization_id,
        room_code,
        count(*)::integer AS session_count
    FROM active
    GROUP BY organization_id, room_code
    HAVING count(*) > 1
),
exact_sessions AS (
    SELECT
        a.*,
        (SELECT count(*)::integer
           FROM public.session_participants AS p
          WHERE p.session_id = a.id) AS participant_count,
        (SELECT count(*)::integer
           FROM public.submissions AS sub
          WHERE sub.session_id = a.id) AS submission_count,
        (SELECT count(*)::integer
           FROM public.grades AS g
           JOIN public.submissions AS sub ON sub.id = g.submission_id
          WHERE sub.session_id = a.id) AS grade_count,
        (SELECT count(*)::integer
           FROM public.quiz_attempts AS qa
          WHERE qa.session_id = a.id) AS quiz_attempt_count,
        (SELECT e.created_by
           FROM public.exams AS e
          WHERE e.id = a.exam_id) AS teacher_id
    FROM active AS a
    JOIN exact_keys AS k
      ON k.organization_id = a.organization_id
     AND k.room_code = a.room_code
),
exact_group_json AS (
    SELECT
        es.organization_id,
        es.room_code,
        count(*)::integer AS session_count,
        jsonb_agg(
            jsonb_build_object(
                'sessionId', es.id,
                'organizationId', es.organization_id,
                'examId', es.exam_id,
                'teacherId', es.teacher_id,
                'rawRoomCode', es.room_code,
                'normalizedRoomCode', es.normalized_room_code,
                'accessMode', es.access_mode,
                'admissionMode', es.admission_mode,
                'status', es.status,
                'acceptingParticipants', es.accepting_participants,
                'createdAt', es.created_at,
                'updatedAt', es.updated_at,
                'startedAt', es.started_at,
                'endedAt', es.ended_at,
                'participantCount', es.participant_count,
                'submissionCount', es.submission_count,
                'gradeCount', es.grade_count,
                'quizAttemptCount', es.quiz_attempt_count
            )
            ORDER BY es.created_at, es.id
        ) AS sessions
    FROM exact_sessions AS es
    GROUP BY es.organization_id, es.room_code
),
normalized_keys AS (
    SELECT
        organization_id,
        normalized_room_code,
        count(*)::integer AS session_count,
        count(DISTINCT room_code)::integer AS raw_code_count,
        array_agg(DISTINCT room_code ORDER BY room_code) AS raw_codes
    FROM active
    GROUP BY organization_id, normalized_room_code
    HAVING count(*) > 1
),
cross_org AS (
    SELECT
        normalized_room_code,
        count(DISTINCT organization_id)::integer AS organization_count,
        count(*)::integer AS session_count
    FROM active
    GROUP BY normalized_room_code
    HAVING count(DISTINCT organization_id) > 1
),
terminal_accepting AS (
    SELECT
        s.id,
        s.organization_id,
        s.exam_id,
        s.room_code,
        s.access_mode,
        s.admission_mode,
        s.status,
        s.accepting_participants,
        s.created_at,
        s.updated_at,
        s.started_at,
        s.ended_at
    FROM public.exam_sessions AS s
    WHERE s.access_mode = 'PublicCloud'
      AND s.status IN ('Finished', 'Cancelled', 'Archived')
      AND s.accepting_participants = true
)
SELECT jsonb_build_object(
    'transactionReadOnly', current_setting('transaction_read_only'),
    'activePredicateSessionCount', (SELECT count(*) FROM active),
    'exactDuplicateGroupCount', (SELECT count(*) FROM exact_keys),
    'exactAffectedSessionCount', (SELECT count(*) FROM exact_sessions),
    'sessionsWithParticipants', (SELECT count(*) FROM exact_sessions WHERE participant_count > 0),
    'sessionsWithSubmissions', (SELECT count(*) FROM exact_sessions WHERE submission_count > 0),
    'sessionsWithGrades', (SELECT count(*) FROM exact_sessions WHERE grade_count > 0),
    'sessionsWithQuizAttempts', (SELECT count(*) FROM exact_sessions WHERE quiz_attempt_count > 0),
    'exactGroups', coalesce((
        SELECT jsonb_agg(
            jsonb_build_object(
                'organizationId', organization_id,
                'normalizedRoomCode', room_code,
                'accessMode', 'PublicCloud',
                'sessionCount', session_count,
                'sessions', sessions
            )
            ORDER BY organization_id, room_code
        )
        FROM exact_group_json
    ), '[]'::jsonb),
    'normalizedCollisionGroups', coalesce((
        SELECT jsonb_agg(to_jsonb(n) ORDER BY organization_id, normalized_room_code)
        FROM normalized_keys AS n
    ), '[]'::jsonb),
    'nonCanonicalActiveRoomCodeCount', (
        SELECT count(*)
        FROM active
        WHERE room_code IS DISTINCT FROM normalized_room_code
    ),
    'crossOrganizationSharedCodeGroups', coalesce((
        SELECT jsonb_agg(to_jsonb(c) ORDER BY normalized_room_code)
        FROM cross_org AS c
    ), '[]'::jsonb),
    'terminalAcceptingCount', (SELECT count(*) FROM terminal_accepting),
    'terminalAcceptingSessions', coalesce((
        SELECT jsonb_agg(to_jsonb(t) ORDER BY created_at, id)
        FROM terminal_accepting AS t
    ), '[]'::jsonb)
) AS findings;

ROLLBACK;
