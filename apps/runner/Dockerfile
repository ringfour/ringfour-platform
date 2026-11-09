FROM ghcr.io/astral-sh/uv:0.12.1 AS uv
FROM python:3.13-slim

# uv is both the readiness-checked dependency and the future environment provisioner.
COPY --from=uv /uv /usr/local/bin/uv

WORKDIR /app
COPY pyproject.toml uv.lock ./
COPY src/ src/
RUN uv sync --frozen --no-dev

RUN useradd --create-home app && mkdir -p /data && chown app:app /data
USER app

EXPOSE 8082
CMD ["/app/.venv/bin/uvicorn", "ringfour_runner.main:create_app", "--factory", "--host", "0.0.0.0", "--port", "8082"]
