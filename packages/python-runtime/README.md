# Ringfour Platform runtime client

This optional Python package lets a mission write an intentional Logbook entry
when it runs under a configured Ringfour Platform Runner:

```python
from ringfour_runtime import logbook

logbook.info("Inspection skipped", station="B", reason="Operator reported maintenance")
```

Install and hash-pin `ringfour-platform-runtime` in the mission's dependency
lock. It is independently packageable and does not depend on the standalone
`ringfour-sdk` or Agent. Robot SDK usage does not require this package. The
Runner supplies the local runtime URL and an execution-scoped credential; if
the runtime is unavailable, `logbook.info` raises `LogbookError` instead of
claiming the entry was saved. Author fields should not contain secrets.

For local validation and deployment requirements, see the [Logbook contract](../../docs/logbook-contract.md)
and [human test guide](../../docs/human-testing/08.md).
