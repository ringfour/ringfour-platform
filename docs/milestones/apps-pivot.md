# Apps pivot — active implementation plan

Governed by [specification](../../../app-pivot.md) and
[implementation backlog](../../../apps-pivot-backlog.md). Work sequentially.
The earlier 00–10 milestones describe legacy behavior and regression evidence.

## AP-01 — contracts and authority (contract acceptance complete)

- [x] Resolve socket and deployment authority in normative instructions.
- [x] Define bounded manifest/configuration subset and semantic validation rules.
- [x] Add Robot Check manifest, canonical digest and rejected fixtures.
- [x] Restore strict identity email/URI format validation and regression tests.
- [x] Define API/runtime schemas for App/revision/import/install/config/run,
  operation transitions, actor/timestamp provenance, grants and errors.
- [x] Define negotiated device support, target/current inventory, stale/offline
  observations and all affected typed consumers; preserve legacy protocol.
- [x] Cover direct shares, early acknowledgement, offline observations, lost
  containers and delayed stop retries in cross-language fixtures.
- [x] Verify all OpenAPI/schema reference targets, new route authentication and required App retry headers.
- [x] Finish negotiated-device and cross-language AP-01 acceptance before AP-02.

Evidence: [AP-01](../evidence/AP-01.md). Human checks:
[AP-01](../human-testing/AP-01.md). Contract-only changes require no stack reload.

## Subsequent work

AP-02 forward migrations and compatible readers follows completed AP-01.
Then AP-03 inventory and AP-04 released libraries precede distribution and
publishing (AP-05/06). Owner-scoped engine management (AP-08) precedes install
(AP-07) and mutually exclusive runs (AP-09). Runtime evidence (AP-10), Studio
(AP-11), CLI (AP-12) and virtual topology (AP-08V) converge at AP-13 acceptance.
Retire managed UI (AP-14) and remaining Runner clients/defaults (AP-15) only
through their explicit drain and recovery gates. AP-16 is a separate follow-up.

AP-01 defines the contracts and supplies independently tested readers; its acceptance does not claim live App execution. App operation/negotiation routes remain dark. AP-02 is next and has not started.

No App endpoints or dispatch are enabled by these contract slices. Do not
mark an external registry, Auth0, simulator or physical gate as passing from
schema fixtures. The operator performs every Platform/virtual stack operation.
