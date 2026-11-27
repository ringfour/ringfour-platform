# Organization leave/delete evidence · 27 November 2026

Implemented `DELETE /v1/organizations/{org_id}/members/me` for self-service leave
and owner-only `DELETE /v1/organizations/{org_id}` for explicit organization
deletion. Updated OpenAPI before implementation; reused existing membership and
error schemas. No authentication bypass or account impersonation was introduced.

The Console allows every member to leave. A sole remaining member is prompted
that leaving deletes the organization, and confirmation chooses the explicit
owner-delete route. A last owner with other members must first promote another
owner to leave. Owners also have a separate deletion button; non-owners do not.
Successful leave/delete updates the workspace dropdown, URL, selected organization
and membership view without a reload. Errors/cancelled confirmation preserve the
selection and membership. Confirmation and the UI explain the loss of access.

Deletion adds the nullable `organizations.deleted_at` tombstone through EF
migration `20261127100016_OrganizationDeletion`. It preserves audit/App provenance
and imports held by other organizations, removes memberships, revokes robot
credentials/pending invitations/shares, expires unused enrollment tokens and
aborts live device sockets. It requires installed Apps to be removed and pending
operations/runs/slots to finish, preventing abandoned workloads. Authorization and
invitation acceptance reject deleted organizations; Store listings hide deleted
publishers without rewriting immutable releases. Existing imported releases remain
readable by their legitimate consumer organizations.

Validation:

- `dotnet test apps/api/tests/Ringfour.Api.Tests --logger 'console;verbosity=normal'`
  — **184 passed**, using the actual API, disposable PostgreSQL 18 and real
  device WebSocket/HTTP protocol fixtures. Covers all member roles, self-only
  leave, last-owner races, owner-only deletion with/without other members,
  credential/enrollment revocation, retained users/other organizations, pending
  and installed App deletion refusal, device disconnect after cleanup, and Store
  hiding with preserved external imports. Log:
  `/tmp/rf-org-management-api-all-final.log`.
- Console `npm test` — **107 passed**; covers confirmation/cancellation, last
  member deletion, owner-only action, API conflict, dropdown/URL updates and the
  empty-membership state. TypeScript checking and production Vite build pass.
- Contracts `npm test` — **181 passed**; OpenAPI references — **5 passed**.
- The initial API verification exposed two fixture issues: attempting to mutate
  immutable revision visibility and reusing an invitation hash across scenarios.
  Corrected the tests to publish a separate official release through the real
  API and generate unique fixture hashes, then reran the full passing suite.

The acceptance panel's setup guide is now visible above AP tests, with six human
steps, saved target IDs and a live membership-overlap explanation. Its live page
and 390 px layout were checked in Playwright without mutating accounts.

No live organization was left/deleted, and no Platform or virtual stack was
reloaded/recreated. The operator must rebuild the Platform API and Console to
activate these changes and apply the new migration on startup:

```bash
cd /home/cdr/ringfour/ringfour-platform
./up.sh -d --no-deps api console
```

This is intentionally left to the operator under `/home/cdr/ringfour/AGENTS.md`.
See [human checks](../human-testing/organization-membership.md).
