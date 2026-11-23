-- Apps device state is additive. Existing enrollment, queued device operations,
-- script output, Logbook and interaction acknowledgements remain untouched.
CREATE TABLE device_deployment_namespace (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1)
        REFERENCES device_identity(singleton_id) ON DELETE RESTRICT,
    deployment_namespace TEXT NOT NULL UNIQUE CHECK (
        length(deployment_namespace) = 36 AND
        substr(deployment_namespace, 9, 1) = '-' AND
        substr(deployment_namespace, 14, 1) = '-' AND
        substr(deployment_namespace, 19, 1) = '-' AND
        substr(deployment_namespace, 24, 1) = '-' AND
        deployment_namespace NOT GLOB '*[^0-9a-f-]*' AND
        deployment_namespace GLOB '????????-????-????-????-????????????'
    ),
    created_at TEXT NOT NULL
);

INSERT INTO device_deployment_namespace (singleton_id, deployment_namespace, created_at)
SELECT singleton_id,
       lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
       lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))),
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM device_identity;

CREATE TRIGGER trg_device_deployment_namespace_no_update
BEFORE UPDATE ON device_deployment_namespace
BEGIN
    SELECT RAISE(ABORT, 'deployment_namespace_requires_operator_recovery');
END;
CREATE TRIGGER trg_device_deployment_namespace_no_delete
BEFORE DELETE ON device_deployment_namespace
BEGIN
    SELECT RAISE(ABORT, 'deployment_namespace_requires_operator_recovery');
END;

CREATE TABLE app_inventory_observation (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    deployment_namespace TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('available', 'unavailable')),
    platform TEXT,
    inventory_json TEXT,
    observed_at TEXT NOT NULL,
    last_successful_at TEXT,
    unavailable_reason TEXT,
    CHECK (length(deployment_namespace) = 36),
    CHECK (
        (state = 'available' AND platform IS NOT NULL AND inventory_json IS NOT NULL AND last_successful_at IS NOT NULL AND unavailable_reason IS NULL) OR
        (state = 'unavailable' AND platform IS NULL AND inventory_json IS NULL AND unavailable_reason IS NOT NULL)
    )
);

CREATE TABLE app_installations (
    installation_id TEXT PRIMARY KEY,
    robot_id TEXT NOT NULL,
    app_id TEXT NOT NULL,
    revision_id TEXT,
    configuration_revision_id TEXT,
    image_json TEXT,
    configuration_json TEXT,
    applied_operation_id TEXT REFERENCES device_operations(operation_id) ON DELETE RESTRICT,
    installed_at TEXT,
    observed_at TEXT,
    candidate_json TEXT,
    updated_at TEXT NOT NULL,
    CHECK (image_json IS NULL OR json_valid(image_json)),
    CHECK (configuration_json IS NULL OR json_valid(configuration_json)),
    CHECK (candidate_json IS NULL OR json_valid(candidate_json)),
    CHECK ((revision_id IS NULL) = (configuration_revision_id IS NULL)),
    CHECK ((revision_id IS NULL) = (image_json IS NULL)),
    CHECK ((revision_id IS NULL) = (configuration_json IS NULL)),
    UNIQUE (installation_id, robot_id)
);

CREATE TRIGGER trg_app_installation_robot_binding
BEFORE INSERT ON app_installations
WHEN NOT EXISTS (SELECT 1 FROM device_identity WHERE robot_id = NEW.robot_id)
BEGIN
    SELECT RAISE(ABORT, 'app_installation_robot_mismatch');
END;

CREATE TRIGGER trg_app_installation_robot_immutable
BEFORE UPDATE OF robot_id ON app_installations
WHEN NEW.robot_id IS NOT OLD.robot_id OR NOT EXISTS (SELECT 1 FROM device_identity WHERE robot_id = NEW.robot_id)
BEGIN
    SELECT RAISE(ABORT, 'app_installation_robot_immutable');
END;

CREATE TABLE app_runs (
    run_id TEXT PRIMARY KEY,
    installation_id TEXT NOT NULL,
    robot_id TEXT NOT NULL,
    operation_id TEXT NOT NULL UNIQUE REFERENCES device_operations(operation_id) ON DELETE RESTRICT,
    app_id TEXT NOT NULL,
    revision_id TEXT NOT NULL,
    image_digest TEXT NOT NULL,
    configuration_revision_id TEXT NOT NULL,
    snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json) AND json_type(snapshot_json) = 'object'),
    container_id TEXT,
    state TEXT NOT NULL CHECK (state IN ('accepted','starting','running','succeeded','failed','stopped','interrupted')),
    requested_at TEXT NOT NULL,
    started_at TEXT,
    ended_at TEXT,
    observed_at TEXT,
    exit_code INTEGER,
    failure_json TEXT CHECK (failure_json IS NULL OR (json_valid(failure_json) AND json_type(failure_json) = 'object')),
    stdout BLOB NOT NULL DEFAULT X'',
    stderr BLOB NOT NULL DEFAULT X'',
    stdout_truncated INTEGER NOT NULL DEFAULT 0 CHECK (stdout_truncated IN (0,1)),
    stderr_truncated INTEGER NOT NULL DEFAULT 0 CHECK (stderr_truncated IN (0,1)),
    stdout_sealed INTEGER NOT NULL DEFAULT 0 CHECK (stdout_sealed IN (0,1)),
    stderr_sealed INTEGER NOT NULL DEFAULT 0 CHECK (stderr_sealed IN (0,1)),
    updated_at TEXT NOT NULL,
    CHECK (length(stdout) <= 65536 AND length(stderr) <= 65536),
    FOREIGN KEY (installation_id, robot_id) REFERENCES app_installations(installation_id, robot_id) ON DELETE RESTRICT
);

CREATE INDEX idx_app_runs_installation_state ON app_runs(installation_id, state, requested_at);
CREATE UNIQUE INDEX idx_app_runs_one_active_per_robot ON app_runs(robot_id)
WHERE state IN ('accepted','starting','running');

CREATE TABLE device_execution_slots (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1)
        REFERENCES device_identity(singleton_id) ON DELETE RESTRICT,
    robot_id TEXT NOT NULL,
    source TEXT NOT NULL CHECK (source IN ('legacy','app')),
    execution_id TEXT NOT NULL UNIQUE,
    claimed_at TEXT NOT NULL
);

INSERT INTO device_execution_slots (singleton_id, robot_id, source, execution_id, claimed_at)
SELECT 1, identity.robot_id, 'legacy', sync.execution_id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM script_sync AS sync
JOIN device_operations AS operation ON operation.operation_id = sync.execution_id
JOIN device_identity AS identity ON identity.robot_id = operation.robot_id
WHERE sync.completed = 0;

CREATE TRIGGER trg_legacy_script_slot_robot
BEFORE INSERT ON script_sync
WHEN NOT EXISTS (
    SELECT 1 FROM device_operations AS operation
    JOIN device_identity AS identity ON identity.robot_id = operation.robot_id
    WHERE operation.operation_id = NEW.execution_id
)
BEGIN
    SELECT RAISE(ABORT, 'legacy_script_robot_mismatch');
END;

CREATE TRIGGER trg_legacy_script_slot_claim
AFTER INSERT ON script_sync
WHEN NEW.completed = 0
BEGIN
    INSERT INTO device_execution_slots (singleton_id, robot_id, source, execution_id, claimed_at)
    SELECT 1, identity.robot_id, 'legacy', NEW.execution_id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    FROM device_operations AS operation
    JOIN device_identity AS identity ON identity.robot_id = operation.robot_id
    WHERE operation.operation_id = NEW.execution_id;
END;

-- RAISE(ABORT) is deliberate: legacy replay uses INSERT OR IGNORE, whose
-- conflict policy would otherwise suppress a nested slot uniqueness failure.
CREATE TRIGGER trg_legacy_script_slot_conflict
BEFORE INSERT ON script_sync
WHEN NEW.completed = 0 AND EXISTS (
    SELECT 1 FROM device_execution_slots
    WHERE source != 'legacy' OR execution_id != NEW.execution_id
)
BEGIN
    SELECT RAISE(ABORT, 'execution_conflict');
END;

CREATE TRIGGER trg_legacy_script_slot_release
AFTER UPDATE OF completed ON script_sync
WHEN OLD.completed = 0 AND NEW.completed = 1
BEGIN
    DELETE FROM device_execution_slots WHERE source = 'legacy' AND execution_id = NEW.execution_id;
END;

CREATE TRIGGER trg_app_run_slot_claim
AFTER INSERT ON app_runs
WHEN NEW.state IN ('accepted','starting','running')
BEGIN
    INSERT INTO device_execution_slots (singleton_id, robot_id, source, execution_id, claimed_at)
    VALUES (1, NEW.robot_id, 'app', NEW.run_id, NEW.requested_at);
END;

CREATE TRIGGER trg_app_run_slot_conflict
BEFORE INSERT ON app_runs
WHEN NEW.state IN ('accepted','starting','running') AND EXISTS (
    SELECT 1 FROM device_execution_slots
    WHERE source != 'app' OR execution_id != NEW.run_id
)
BEGIN
    SELECT RAISE(ABORT, 'execution_conflict');
END;

CREATE TRIGGER trg_app_run_slot_reclaim
AFTER UPDATE OF state ON app_runs
WHEN OLD.state NOT IN ('accepted','starting','running') AND NEW.state IN ('accepted','starting','running')
BEGIN
    INSERT INTO device_execution_slots (singleton_id, robot_id, source, execution_id, claimed_at)
    VALUES (1, NEW.robot_id, 'app', NEW.run_id, NEW.updated_at);
END;

CREATE TRIGGER trg_app_run_slot_release
AFTER UPDATE OF state ON app_runs
WHEN OLD.state IN ('accepted','starting','running') AND NEW.state NOT IN ('accepted','starting','running')
BEGIN
    DELETE FROM device_execution_slots WHERE source = 'app' AND execution_id = NEW.run_id;
END;

CREATE TRIGGER trg_app_run_state_transition
BEFORE UPDATE OF state ON app_runs
WHEN NOT (
    NEW.state = OLD.state OR
    (OLD.state = 'accepted' AND NEW.state IN ('starting','running','failed','interrupted')) OR
    (OLD.state = 'starting' AND NEW.state IN ('running','failed','interrupted')) OR
    (OLD.state = 'running' AND NEW.state IN ('succeeded','failed','stopped','interrupted'))
)
BEGIN
    SELECT RAISE(ABORT, 'invalid_app_run_state_transition');
END;

CREATE TABLE app_run_credentials (
    run_id TEXT PRIMARY KEY REFERENCES app_runs(run_id) ON DELETE RESTRICT,
    token_sha256 BLOB NOT NULL CHECK (length(token_sha256) = 32),
    expires_at TEXT NOT NULL,
    revoked_at TEXT
);

CREATE TABLE app_observation_streams (
    route TEXT PRIMARY KEY CHECK (route IN ('inventory','observations')),
    last_sequence INTEGER NOT NULL DEFAULT 0 CHECK (last_sequence BETWEEN 0 AND 9007199254740991),
    updated_at TEXT NOT NULL
);

CREATE TABLE app_observation_outbox (
    route TEXT NOT NULL REFERENCES app_observation_streams(route) ON DELETE RESTRICT,
    sequence INTEGER NOT NULL CHECK (sequence BETWEEN 1 AND 9007199254740991),
    report_id TEXT NOT NULL UNIQUE,
    deployment_namespace TEXT NOT NULL,
    body_sha256 TEXT NOT NULL CHECK (length(body_sha256) = 64 AND body_sha256 NOT GLOB '*[^0-9a-f]*'),
    body_json TEXT NOT NULL CHECK (json_valid(body_json) AND length(CAST(body_json AS BLOB)) <= 65536),
    created_at TEXT NOT NULL,
    acknowledged_at TEXT,
    acknowledgement_json TEXT CHECK (acknowledgement_json IS NULL OR json_valid(acknowledgement_json)),
    PRIMARY KEY (route, sequence),
    CHECK ((acknowledged_at IS NULL) = (acknowledgement_json IS NULL))
);

CREATE INDEX idx_app_observation_outbox_pending ON app_observation_outbox(route, sequence)
WHERE acknowledged_at IS NULL;

INSERT INTO app_observation_streams(route, last_sequence, updated_at)
VALUES ('inventory', 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
       ('observations', 0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TRIGGER trg_app_observation_sequence
BEFORE INSERT ON app_observation_outbox
WHEN NEW.sequence != (SELECT last_sequence + 1 FROM app_observation_streams WHERE route = NEW.route) OR
     NEW.deployment_namespace IS NOT (SELECT deployment_namespace FROM device_deployment_namespace WHERE singleton_id = 1)
BEGIN
    SELECT RAISE(ABORT, 'stale_observation_sequence_or_namespace');
END;

CREATE TRIGGER trg_app_inventory_namespace
BEFORE INSERT ON app_inventory_observation
WHEN NEW.deployment_namespace IS NOT (SELECT deployment_namespace FROM device_deployment_namespace WHERE singleton_id = 1)
BEGIN
    SELECT RAISE(ABORT, 'app_inventory_namespace_mismatch');
END;

CREATE TRIGGER trg_app_inventory_namespace_immutable
BEFORE UPDATE OF deployment_namespace ON app_inventory_observation
WHEN NEW.deployment_namespace IS NOT OLD.deployment_namespace
BEGIN
    SELECT RAISE(ABORT, 'app_inventory_namespace_immutable');
END;

CREATE TRIGGER trg_app_observation_advance
AFTER INSERT ON app_observation_outbox
BEGIN
    UPDATE app_observation_streams
    SET last_sequence = NEW.sequence, updated_at = NEW.created_at
    WHERE route = NEW.route;
END;

CREATE TRIGGER trg_app_observation_outbox_immutable
BEFORE UPDATE ON app_observation_outbox
WHEN NEW.route != OLD.route OR NEW.sequence != OLD.sequence OR NEW.report_id != OLD.report_id OR
     NEW.deployment_namespace != OLD.deployment_namespace OR NEW.body_sha256 != OLD.body_sha256 OR
     NEW.body_json != OLD.body_json OR NEW.created_at != OLD.created_at OR
     (OLD.acknowledged_at IS NOT NULL AND (NEW.acknowledged_at IS NOT OLD.acknowledged_at OR NEW.acknowledgement_json IS NOT OLD.acknowledgement_json))
BEGIN
    SELECT RAISE(ABORT, 'app_observation_outbox_immutable');
END;

CREATE TRIGGER trg_app_observation_outbox_no_delete
BEFORE DELETE ON app_observation_outbox
BEGIN
    SELECT RAISE(ABORT, 'app_observation_outbox_retained');
END;

CREATE TRIGGER trg_app_run_snapshot_immutable
BEFORE UPDATE ON app_runs
WHEN NEW.run_id != OLD.run_id OR NEW.installation_id != OLD.installation_id OR NEW.robot_id != OLD.robot_id OR
     NEW.operation_id != OLD.operation_id OR NEW.app_id != OLD.app_id OR NEW.revision_id != OLD.revision_id OR
     NEW.image_digest != OLD.image_digest OR NEW.configuration_revision_id != OLD.configuration_revision_id OR
     NEW.snapshot_json != OLD.snapshot_json OR NEW.requested_at != OLD.requested_at
BEGIN
    SELECT RAISE(ABORT, 'app_run_attribution_immutable');
END;
