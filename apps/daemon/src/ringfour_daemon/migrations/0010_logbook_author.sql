-- Preserve queued events while allowing bounded mission-authored payloads.
CREATE TABLE logbook_outbox_new (
    local_sequence INTEGER PRIMARY KEY CHECK (local_sequence BETWEEN 1 AND 9007199254740991),
    event_id TEXT NOT NULL UNIQUE,
    event_json TEXT NOT NULL CHECK (length(CAST(event_json AS BLOB)) <= 12288)
);
INSERT INTO logbook_outbox_new SELECT * FROM logbook_outbox;
DROP TABLE logbook_outbox;
ALTER TABLE logbook_outbox_new RENAME TO logbook_outbox;
CREATE TABLE logbook_entry_replays (
    entry_id TEXT PRIMARY KEY,
    execution_id TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    local_sequence INTEGER NOT NULL,
    created_at REAL NOT NULL
);
CREATE INDEX logbook_entry_replays_created ON logbook_entry_replays(created_at);
