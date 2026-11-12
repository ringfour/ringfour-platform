CREATE TABLE IF NOT EXISTS script_sync (
    execution_id TEXT PRIMARY KEY REFERENCES device_operations(operation_id),
    stdout_offset INTEGER NOT NULL DEFAULT 0 CHECK (stdout_offset BETWEEN 0 AND 65536),
    stderr_offset INTEGER NOT NULL DEFAULT 0 CHECK (stderr_offset BETWEEN 0 AND 65536),
    completed INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1))
);
