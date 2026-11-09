import sqlite3
from datetime import UTC, datetime
from pathlib import Path

MIGRATIONS_DIR = Path(__file__).parent / "migrations"


def migrate(db_path: str) -> list[int]:
    """Apply pending SQL migrations in filename order; returns applied versions.

    Repeatable: applied versions are recorded in schema_migrations and skipped.
    """
    Path(db_path).parent.mkdir(parents=True, exist_ok=True)
    applied: list[int] = []
    with sqlite3.connect(db_path) as conn:
        conn.execute(
            "CREATE TABLE IF NOT EXISTS schema_migrations ("
            "version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)"
        )
        done = {row[0] for row in conn.execute("SELECT version FROM schema_migrations")}
        for path in sorted(MIGRATIONS_DIR.glob("*.sql")):
            version = int(path.name.split("_")[0])
            if version in done:
                continue
            conn.executescript(path.read_text())
            conn.execute(
                "INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)",
                (version, datetime.now(UTC).isoformat()),
            )
            applied.append(version)
    return applied
