CREATE TABLE app_artifact_spool (
    artifact_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES app_runs(run_id),
    path TEXT NOT NULL,
    size INTEGER NOT NULL CHECK (size BETWEEN 0 AND 16777216),
    sha256 TEXT NOT NULL,
    content_type TEXT NOT NULL,
    delivered INTEGER NOT NULL DEFAULT 0 CHECK (delivered IN (0,1)),
    UNIQUE(run_id,path)
);
CREATE TABLE app_output_delivery (
    run_id TEXT PRIMARY KEY REFERENCES app_runs(run_id),
    stdout_offset INTEGER NOT NULL DEFAULT 0,
    stderr_offset INTEGER NOT NULL DEFAULT 0,
    stdout_sealed INTEGER NOT NULL DEFAULT 0,
    stderr_sealed INTEGER NOT NULL DEFAULT 0,
    stdout_truncated INTEGER NOT NULL DEFAULT 0,
    stderr_truncated INTEGER NOT NULL DEFAULT 0
);
