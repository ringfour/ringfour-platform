CREATE TABLE device_operations (
    operation_id TEXT PRIMARY KEY,
    robot_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    payload_version INTEGER NOT NULL,
    payload TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('accepted', 'running', 'succeeded', 'failed', 'cancelled')),
    deadline_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    result TEXT
);

CREATE INDEX idx_device_operations_state ON device_operations(state);
