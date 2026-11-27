CREATE TABLE app_development_sessions (
 session_id TEXT PRIMARY KEY, robot_id TEXT NOT NULL, namespace TEXT NOT NULL,
 body TEXT NOT NULL, closed INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE app_development_source (
 operation_id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES app_development_sessions(session_id),
 base_generation INTEGER NOT NULL, generation INTEGER NOT NULL, archive_sha256 TEXT NOT NULL,
 reported INTEGER NOT NULL DEFAULT 0, UNIQUE(session_id,generation)
);
