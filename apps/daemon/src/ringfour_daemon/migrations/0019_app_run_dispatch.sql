CREATE TABLE app_run_reports (
    run_id TEXT PRIMARY KEY REFERENCES app_runs(run_id),
    fingerprint TEXT NOT NULL
);
CREATE TABLE app_run_operation_reports (
    operation_id TEXT PRIMARY KEY REFERENCES device_operations(operation_id),
    observation_json TEXT NOT NULL
);
DROP TRIGGER trg_app_run_state_transition;
CREATE TRIGGER trg_app_run_state_transition
BEFORE UPDATE OF state ON app_runs
WHEN NOT (
    NEW.state = OLD.state OR
    (OLD.state = 'accepted' AND NEW.state IN ('starting','running','failed','stopped','interrupted')) OR
    (OLD.state = 'starting' AND NEW.state IN ('running','failed','stopped','interrupted')) OR
    (OLD.state = 'running' AND NEW.state IN ('succeeded','failed','stopped','interrupted'))
)
BEGIN
    SELECT RAISE(ABORT, 'invalid_app_run_state_transition');
END;
