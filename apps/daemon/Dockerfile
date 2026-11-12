FROM ghcr.io/astral-sh/uv:0.12.1 AS uv
FROM python:3.13-slim

COPY --from=uv /uv /usr/local/bin/uv

WORKDIR /app
COPY pyproject.toml uv.lock ./
COPY src/ src/
RUN uv sync --frozen --no-dev

RUN useradd --create-home app && mkdir -p /data /staged && chown app:app /data /staged
USER app

EXPOSE 8081 8083
CMD ["/app/.venv/bin/ringfour-daemon"]
