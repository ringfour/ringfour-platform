# 01 — Identity

## Scope and prerequisites

00. Mandatory human accounts and organization authorization; no enrollment yet.

Complete prerequisite milestone 00 before beginning. Shared schemas, authorization, errors, limits, idempotency and durability rules in [architecture](../architecture.md) apply.

## Contracts

Apply the [starter database schema](../database-schema.md) for milestone 01.

GET /v1/me; POST/GET /v1/organizations; GET/POST/PATCH/DELETE /v1/organizations/{org_id}/members with target member IDs for item routes. Define user, organization and membership schemas and role matrix from architecture. Set up Ringfour API (audience `https://api.ringfour.eu`, RS256), Ringfour CLI (Native, OAuth Device Authorization Flow), and Ringfour Web (SPA, Authorization Code + PKCE). No Auth0 Organizations. Creating an organization atomically creates its owner membership.

Freeze request/response/message schemas, state transitions and error examples in packages/contracts before implementation. New interfaces here are proposed Ringfour contracts, not descriptions of existing SDK interfaces.

## Implementation

ASP.NET Core JWT Bearer validates Auth0 access tokens. `GET /v1/me` resolves or creates the local user by `sub`. Add EF Core migrations and Ringfour-owned membership checks in PostgreSQL. CLI `ringfour-cli login` uses device authorization; add `ringfour-cli logout`, `ringfour-cli whoami`, and `org create/list/select`. Persist selected org separately from secrets. Console uses Ringfour Web with Authorization Code + PKCE and calls the same API to inspect identity/membership. Daemon/Runner do not use Auth0.

## Independent component tests

Use xUnit and PostgreSQL Testcontainers for API identity and authorization. JWT wrong issuer/audience/expiry/algorithm or signature rejected; subject stability across email changes; owner role and last-owner protection; cross-org reads denied. CLI credential-store and logout behavior; Console denies unauthenticated access.

Run each affected application's tests independently; use fixtures for external dependencies only at this tier. Unchanged applications need regression coverage, not invented feature work.

## Integrated acceptance

Use a real Auth0 tenant: `ringfour-cli login` → device authorization in Auth0 → valid API access token → `GET /v1/me` creates/looks up the local user → `ringfour-cli whoami` reports that identity. Repeat to verify stable lookup. Sign in to Console via Authorization Code + PKCE, create an organization and inspect membership. A second account outside the organization cannot read it. Run reproducible local authorization integration with test signing fixtures separately.

Exercise real processes and protocols. Record exact commands, expected and observed results, versions and cleanup steps in docs/evidence/01.md. A mock-only result does not satisfy acceptance.

## Definition of done

Live Auth0 and local integration evidence recorded; no bypass credentials or production test issuer; login/logout and isolation pass.

Contracts and generated consumers agree; migrations work from empty and previous milestone state; relevant regressions pass; no secrets in evidence. Review and commit this slice before proceeding. Unavailable external prerequisites remain visible incomplete gates.
