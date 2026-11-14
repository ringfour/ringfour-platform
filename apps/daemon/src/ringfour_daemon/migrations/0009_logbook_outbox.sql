-- This is a single robot's durable stream. State remains after the queue drains.
CREATE TABLE logbook_outbox_state (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    robot_id TEXT NOT NULL,
    last_sequence INTEGER NOT NULL CHECK (last_sequence BETWEEN 0 AND 9007199254740991),
    acknowledged_sequence INTEGER NOT NULL CHECK (acknowledged_sequence BETWEEN 0 AND last_sequence)
);
CREATE TABLE logbook_outbox (
    local_sequence INTEGER PRIMARY KEY CHECK (local_sequence BETWEEN 1 AND 9007199254740991),
    event_id TEXT NOT NULL UNIQUE,
    event_json TEXT NOT NULL CHECK (length(CAST(event_json AS BLOB)) <= 1024)
);
