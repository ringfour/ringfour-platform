# Legacy full-stack E2E fixture

This Compose file exists for older automated acceptance scripts that test the
Platform API, PostgreSQL, daemon, Runner, and Console together. It is not a
deployment manifest or the recommended local development setup.

For everyday local work, start the server with `platform/./up.sh` and use the
sibling `ringfour-virtual` project for Base101, Agent, daemon, and Runner. That
keeps the API stack and device stack independently startable.

The `.env` symlink in this directory is local and ignored; it points to the
Platform root `.auth.env` for this fixture's Compose interpolation.
