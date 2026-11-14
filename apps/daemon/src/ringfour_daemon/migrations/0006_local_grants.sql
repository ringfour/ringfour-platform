-- Local capability hashes and session tombstones; no bearer token is stored.
CREATE TABLE local_dev_sessions (
    session_id TEXT PRIMARY KEY,
    expires_at TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('open','closed'))
);
CREATE TABLE local_dev_grants (
    session_id TEXT PRIMARY KEY REFERENCES local_dev_sessions(session_id),
    grant_id TEXT NOT NULL UNIQUE,
    operation_id TEXT NOT NULL UNIQUE,
    token_sha256 TEXT NOT NULL,
    expires_at TEXT NOT NULL
);
CREATE TABLE local_dev_grant_operations (
    operation_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    grant_id TEXT NOT NULL UNIQUE,
    fingerprint TEXT NOT NULL
);
