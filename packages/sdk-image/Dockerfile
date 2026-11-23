# syntax=docker/dockerfile:1
# Build context: a self-contained AP-04 wheel bundle, not the Platform tree.
FROM ghcr.io/astral-sh/uv:0.13.8@sha256:5cc1805e2426484abca4ad46ea783bab281fdb225fd82d8ce6d66a81254bac85 AS uv
FROM python:3.13.18-slim-trixie@sha256:e48f59cdba69a3cbd9ed2cf0f348d90d45a8d350d98134cf1070bc106ee9ed90
COPY --from=uv /uv /usr/local/bin/uv
ARG TARGETARCH
ARG INSTALL_RUNTIME=false
COPY linux-${TARGETARCH}/requirements*.txt /opt/ringfour/
RUN --mount=type=bind,source=linux-${TARGETARCH}/wheels,target=/wheels \
    uv pip install --system --offline --no-index --find-links /wheels \
        --require-hashes --only-binary=:all: -r /opt/ringfour/requirements-sdk.txt \
    && case "$INSTALL_RUNTIME" in \
        true) uv pip install --system --offline --no-index --find-links /wheels \
            --require-hashes --only-binary=:all: -r /opt/ringfour/requirements-runtime.txt ;; \
        false) ;; \
        *) echo 'INSTALL_RUNTIME must be true or false' >&2; exit 1 ;; \
       esac \
    && uv pip check --system
CMD ["python", "-c", "from ringfour import Robot; print(Robot.imu)"]
