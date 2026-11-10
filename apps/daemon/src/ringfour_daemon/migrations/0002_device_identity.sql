-- Milestone 02: durable device identity. The row is committed only after the
-- credential secret is stored in protected storage; it never contains the
-- secret itself. Singleton enforced so the daemon has exactly one binding.
CREATE TABLE device_identity (
    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    robot_id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    platform_url TEXT NOT NULL,
    credential_id TEXT NOT NULL
);
