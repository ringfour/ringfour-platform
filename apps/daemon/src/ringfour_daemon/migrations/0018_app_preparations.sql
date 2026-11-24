CREATE TABLE app_preparations (
    operation_id TEXT PRIMARY KEY REFERENCES device_operations(operation_id) ON DELETE RESTRICT,
    installation_id TEXT NOT NULL REFERENCES app_installations(installation_id) ON DELETE RESTRICT,
    state TEXT NOT NULL CHECK (state IN ('staging','staged','applied','failed')),
    terminal_report_json TEXT CHECK (terminal_report_json IS NULL OR json_valid(terminal_report_json)),
    updated_at TEXT NOT NULL
);
CREATE TRIGGER trg_local_dev_app_prepare_exclusion
BEFORE INSERT ON local_dev_runs
WHEN NEW.completed = 0 AND EXISTS (
    SELECT 1 FROM app_installations WHERE json_extract(candidate_json,'$.state') IN ('staging','staged')
)
BEGIN
    SELECT RAISE(ABORT, 'execution_conflict');
END;
CREATE TRIGGER trg_legacy_app_prepare_exclusion
BEFORE INSERT ON script_sync
WHEN NEW.completed = 0 AND EXISTS (
    SELECT 1 FROM app_installations WHERE json_extract(candidate_json,'$.state') IN ('staging','staged')
)
BEGIN
    SELECT RAISE(ABORT, 'execution_conflict');
END;
