-- The inbox is independent of the Platform outbox until motion ingestion is implemented.
CREATE TABLE agent_motion_checkpoint (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    stream_id TEXT NOT NULL,
    sequence INTEGER NOT NULL CHECK (sequence BETWEEN 0 AND 9007199254740991)
);
CREATE TABLE agent_motion_inbox (
    sequence INTEGER PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9007199254740991),
    event_id TEXT NOT NULL UNIQUE,
    event_json TEXT NOT NULL CHECK (length(CAST(event_json AS BLOB)) <= 8192)
);
