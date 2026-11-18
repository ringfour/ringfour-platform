CREATE TABLE runtime_interactions (
    request_id TEXT PRIMARY KEY,
    execution_id TEXT NOT NULL,
    robot_id TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    prompt TEXT NOT NULL,
    choices_json TEXT NOT NULL,
    deadline REAL NOT NULL,
    timeout_outcome TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending', 'answered', 'timed_out', 'cancelled')),
    outcome TEXT,
    response_json TEXT,
    created_at REAL NOT NULL,
    updated_at REAL NOT NULL
);

CREATE INDEX runtime_interactions_execution_idx
ON runtime_interactions(execution_id, created_at);
