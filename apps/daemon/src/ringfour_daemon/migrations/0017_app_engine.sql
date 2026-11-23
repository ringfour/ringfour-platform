CREATE TABLE app_engine_intents (
    run_id TEXT PRIMARY KEY REFERENCES app_runs(run_id) ON DELETE RESTRICT,
    robot_id TEXT NOT NULL,
    deployment_namespace TEXT NOT NULL,
    image_id TEXT NOT NULL,
    network_id TEXT NOT NULL,
    container_name TEXT NOT NULL UNIQUE,
    phase TEXT NOT NULL CHECK (phase IN ('create','created','start','observed','terminal','removed')),
    stop_requested INTEGER NOT NULL DEFAULT 0 CHECK (stop_requested IN (0,1)),
    updated_at TEXT NOT NULL
);
