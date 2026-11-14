# Optional SDK development image; the SDK is a Python library, not a service.
FROM python:3.13-slim
COPY apps/runner/vendor/ringfour_sdk-0.1.0.dev0-py3-none-any.whl /tmp/ringfour_sdk-0.1.0.dev0-py3-none-any.whl
RUN python -m pip install --no-cache-dir \
    websockets==17.4 numpy==2.5.4 pydantic==2.14.3 pydantic-core==2.50.4 \
    annotated-types==0.8.0 typing-extensions==4.16.0 typing-inspection==0.4.5 \
    /tmp/ringfour_sdk-0.1.0.dev0-py3-none-any.whl
CMD ["python", "-c", "from ringfour import Robot; print('Ringfour SDK ready')"]
