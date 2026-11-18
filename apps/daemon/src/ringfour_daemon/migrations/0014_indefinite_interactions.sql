CREATE TABLE runtime_interactions_new (
    request_id TEXT PRIMARY KEY,
    execution_id TEXT NOT NULL,
    robot_id TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    prompt TEXT NOT NULL,
    choices_json TEXT NOT NULL,
    deadline REAL,
    timeout_outcome TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'answered', 'timed_out', 'cancelled')),
    outcome TEXT,
    response_json TEXT,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL,
    CHECK ((deadline IS NULL AND timeout_outcome IS NULL) OR
           (deadline IS NOT NULL AND timeout_outcome IS NOT NULL)),
    CHECK (state != 'timed_out' OR
           (deadline IS NOT NULL AND timeout_outcome IS NOT NULL AND outcome = timeout_outcome))
);

INSERT INTO runtime_interactions_new (
    request_id, execution_id, robot_id, request_hash, prompt, choices_json,
    deadline, timeout_outcome, state, outcome, response_json, created_at, updated_at
)
SELECT
    request_id, execution_id, robot_id, request_hash, prompt, choices_json,
    deadline, timeout_outcome, state, outcome, response_json, created_at, updated_at
FROM runtime_interactions;

DROP TABLE runtime_interactions;
ALTER TABLE runtime_interactions_new RENAME TO runtime_interactions;

CREATE INDEX runtime_interactions_execution_idx
ON runtime_interactions(execution_id, created_at);
