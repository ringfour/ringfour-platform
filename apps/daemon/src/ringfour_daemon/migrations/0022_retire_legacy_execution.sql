-- AP-15: retain App ownership/recovery and shared App/Agent outboxes.
DROP TRIGGER IF EXISTS trg_local_dev_app_prepare_exclusion;
DROP TRIGGER IF EXISTS trg_legacy_app_prepare_exclusion;
DROP TABLE IF EXISTS local_dev_grants;
DROP TABLE IF EXISTS local_dev_grant_operations;
DROP TABLE IF EXISTS local_source_syncs;
DROP TABLE IF EXISTS local_dev_runs;
DROP TABLE IF EXISTS local_dev_sessions;
DROP TABLE IF EXISTS script_sync;
DROP TABLE IF EXISTS live_artifact_queue;
DELETE FROM device_execution_slots WHERE source = 'legacy';
DELETE FROM device_operations WHERE kind NOT LIKE 'app.%';
