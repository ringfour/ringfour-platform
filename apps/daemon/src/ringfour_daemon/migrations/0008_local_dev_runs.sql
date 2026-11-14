CREATE TABLE local_dev_runs (
  execution_id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES local_dev_sessions(session_id),
  operation_id TEXT NOT NULL UNIQUE,
  started INTEGER NOT NULL DEFAULT 0,
  completed INTEGER NOT NULL DEFAULT 0
);
