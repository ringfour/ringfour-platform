# Ringfour — App Container Registry Specification

## 1. Objective

Provide secure, multi-tenant OCI image storage and distribution for user-published Ringfour apps, using existing Ringfour authentication, organization permissions and robot identities. The registry is infrastructure, not a separate user-facing product.

## 2. Infrastructure

- Deploy OCI Distribution as an additional container in the existing platform Docker Compose stack.
- Expose through `repository.ringfour.eu` using Cloudflare Tunnel, subject to transport validation.
- Use persistent filesystem storage initially.
- Support migration to S3-compatible storage without changing client interfaces.
- Disable anonymous registry access.
- Delegate all authorization to the Ringfour backend.
- No separate registry accounts, database or management UI.

## 3. Component responsibilities

| Component | Responsibility |
|---|---|
| Backend | Authentication, authorization, token issuance, revision metadata |
| Registry | OCI manifests, image layers, storage, token validation |
| CLI | Image building, uploading and publishing |
| Daemon | Authenticated image retrieval and local caching |
| Studio | App visibility, permissions and revision management |

## 4. Repository naming

Each app receives a unique, backend-assigned repository:

```text
repository.ringfour.eu/apps/<org-id>/<app-id>
```

Each published revision references an immutable image digest:

```text
repository.ringfour.eu/apps/<org-id>/<app-id>@sha256:...
```

Repository names must derive from immutable identifiers, not user-editable display names.

## 5. Authentication and authorization

Implement a Distribution-compatible token endpoint in the existing backend:

```http
GET /registry/token
```

The backend authenticates the requesting user or robot using existing Ringfour credentials and issues a short-lived, asymmetrically signed JWT containing authorized repository scopes. The registry validates the signature, issuer, audience, expiration and requested permissions.

| Identity | Push | Pull |
|---|---|---|
| App owner | Yes | Yes |
| Authorized developer | Yes | Yes |
| Authorized organization member | No | Yes |
| Authorized robot | No | Yes |
| Marketplace customer | No | Entitled apps only |
| Unauthorized identity | No | No |

Requested scopes must always be intersected with backend-calculated permissions. Clients cannot grant themselves access by requesting additional scopes. Registry signing private keys remain exclusively within the backend.

## 6. App publishing

```bash
ringfour app push
```

1. CLI requests publication authorization.
2. Backend validates app ownership and permissions.
3. CLI obtains temporary repository-scoped credentials.
4. CLI builds and pushes the OCI image.
5. CLI submits the resulting image digest.
6. Backend independently verifies the manifest and finalizes the app revision.

Failed or incomplete uploads must not produce deployable revisions. Published revision digests are immutable.

## 7. App deployment

1. Backend resolves the selected app revision.
2. Daemon receives the image reference and digest.
3. Daemon requests temporary pull credentials using its existing robot identity.
4. Backend validates robot authorization.
5. Daemon pulls the image by digest.
6. Daemon caches the image locally and continues the existing app execution lifecycle.

Registry unavailability must not interrupt already-running apps or prevent execution of locally cached, authorized revisions.

## 8. App revision metadata

Extend the existing app revision entity with:

```json
{
  "image": {
    "repository": "repository.ringfour.eu/apps/org-id/app-id",
    "digest": "sha256:...",
    "sizeBytes": 104857600,
    "platforms": ["linux/arm64", "linux/amd64"]
  }
}
```

The backend derives image metadata from verified registry manifests and descriptors rather than trusting client-supplied values.

## 9. Security and storage

- Short-lived, repository-scoped credentials.
- Strict organization isolation.
- Pull-only robot credentials.
- No direct registry access to the platform database.
- No persistent registry credentials on robots.
- No user-accessible registry deletion operations.
- Configurable organization storage quotas.
- Audit logging for publication and authorization events.
- Retention and garbage collection that preserve deployable revisions and rollback targets.
- Rate limiting and upload resource limits.

## 10. Cloudflare transport

Validate large OCI image uploads through Cloudflare Tunnel. If standard Docker pushes exceed Cloudflare's per-request limits, use either:

- Chunked, resumable OCI uploads through the CLI.
- A dedicated direct HTTPS registry endpoint.

**This is the main unresolved infrastructure decision.** Validate it before implementing a custom uploader.

## 11. Acceptance tests

- [ ] Developer can publish an owned app.
- [ ] Developer cannot push to another organization's repository.
- [ ] Unauthorized users cannot pull private apps, even with a known digest.
- [ ] Authorized robot can pull its assigned app.
- [ ] Robot cannot push or delete images.
- [ ] Expired and tampered tokens are rejected.
- [ ] Incomplete uploads cannot become deployable revisions.
- [ ] Multi-architecture images are supported.
- [ ] Large robotics images can be uploaded successfully.
- [ ] Previously cached apps remain executable during registry downtime.
- [ ] Garbage collection preserves rollback targets.

## 12. Out of scope

- Custom registry protocol implementation.
- Harbor or separate registry management interface.
- Marketplace payments and billing.
- Image DRM.
- Per-revision registry authorization.
- Distributed registry replication.

Repository-level authorization is sufficient for the initial implementation. Per-revision entitlements may require separate repository isolation later.

---

**Architectural principle:** Ringfour owns app identity, authorization, versioning and deployment. OCI Distribution owns container storage and transfer. Neither duplicates the other's responsibilities.
