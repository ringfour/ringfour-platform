CREATE TABLE live_artifact_queue (
    artifact_id TEXT PRIMARY KEY,
    execution_id TEXT NOT NULL,
    path TEXT NOT NULL,
    size INTEGER NOT NULL CHECK (size BETWEEN 0 AND 9007199254740991),
    sha256 TEXT NOT NULL,
    content_type TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'pending' CHECK (state IN ('pending','uploaded')),
    created_at REAL NOT NULL,
    UNIQUE(execution_id, path)
);
