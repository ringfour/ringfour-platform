-- App live-settings application identities and results survive Daemon replacement.
CREATE TABLE app_settings_applications (
    application_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    settings_revision_id TEXT NOT NULL,
    values_json TEXT NOT NULL CHECK (json_valid(values_json) AND json_type(values_json) = 'object' AND length(CAST(values_json AS BLOB)) <= 16384),
    initial INTEGER NOT NULL CHECK (initial IN (0,1)),
    outcome TEXT CHECK (outcome IS NULL OR outcome IN ('applied','rejected')),
    reason TEXT CHECK (reason IS NULL OR (length(reason) <= 240 AND instr(reason,char(10)) = 0 AND instr(reason,char(13)) = 0 AND instr(reason,char(0)) = 0)),
    result_recorded_at TEXT,
    delivered_at TEXT,
    CHECK ((outcome IS NULL AND result_recorded_at IS NULL AND reason IS NULL) OR (outcome = 'applied' AND result_recorded_at IS NOT NULL AND reason IS NULL) OR (outcome = 'rejected' AND result_recorded_at IS NOT NULL)),
    UNIQUE(run_id, application_id)
);
CREATE INDEX idx_app_settings_pending_run ON app_settings_applications(run_id, result_recorded_at);
