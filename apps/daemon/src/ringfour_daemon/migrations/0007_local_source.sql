CREATE TABLE local_source_syncs (
  operation_id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES local_dev_sessions(session_id),
  key_hash TEXT NOT NULL,
  request_hash TEXT NOT NULL,
  grant_id TEXT NOT NULL,
  base_generation INTEGER NOT NULL,
  generation INTEGER NOT NULL,
  archive_sha256 TEXT NOT NULL,
  archive_size INTEGER NOT NULL,
  manifest TEXT NOT NULL,
  staged_archive TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('pending','committed','reported','failed','cancelled')),
  error_code TEXT,
  UNIQUE(session_id, key_hash)
);
CREATE UNIQUE INDEX local_source_sync_generation ON local_source_syncs(session_id, generation)
  WHERE state IN ('pending','committed','reported');
