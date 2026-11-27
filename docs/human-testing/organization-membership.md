# Leaving and deleting organizations

The workspace operator must deploy the updated API and Console. From the Platform checkout:

```bash
./up.sh -d --no-deps api console
```

The startup migration adds a nullable `organizations.deleted_at` field. No existing membership changes automatically. The coding agent does not reload either stack under the workspace AGENTS.md rule.

## Leave a shared organization

1. Sign in as the account that wants to leave and open **Organizations**.
2. Select the organization. Click **Leave organization** and check its name in the confirmation.
3. Cancel once: membership and workspace selection must stay unchanged.
4. Confirm: the organization must disappear from your cards and the navbar's organization selector. If it was your active workspace, another available organization is selected; if none remain, the page offers organization creation/joining.
5. Direct robot/member reads under the organization must now be denied. The account, other organizations, robots, Apps and history remain.

Operator and Developer members can leave their own membership. Owners can leave when another Owner remains. An organization that still has other members cannot lose its last Owner: promote another member to Owner first, or explicitly delete the organization as an Owner.

## Last remaining member

1. Use a disposable organization whose only member is its Owner.
2. Click **Leave organization**. The confirmation must say that leaving will **delete the organization**.
3. Cancel and confirm that the organization remains.
4. Confirm on a retry: the Console uses the explicit owner-only deletion endpoint. It removes the organization from the workspace selector and shows the deletion result.

The plain leave API never silently deletes an organization; the confirmed UI action explicitly chooses deletion.

## Delete an organization (Owner only)

1. Select a disposable organization as its Owner. **Delete organization** must be visible even if other members remain.
2. Check with Operator and Developer accounts: they have no deletion button; direct DELETE requests return 403. An outsider receives 404; an unauthenticated caller receives 401.
3. Stop and uninstall Apps and wait for pending operations. Attempting deletion while installed Apps, active runs/slots or queued/accepted/running operations remain returns a useful conflict and preserves the organization.
4. Click **Delete organization**, check its name and confirm. All members lose access. Robot credentials are revoked, unused enrollment tokens expire, pending invitations and shares are revoked, and connected device sessions are disconnected. Users and their other workspaces remain.
5. Old enrollment tokens, robot credentials, direct links and invitation links must not restore access. Repeating deletion returns 404.
6. Immutable run/history records and published revisions remain internally for audit and existing consumer imports. A deleted publisher disappears from fresh App Store discovery; an App already imported into another organization remains accessible there. Deletion is not an artifact/history purge or a robot uninstall operation.

## Local virtual AP privacy prerequisite

`ring1` was discovered as a member of both **kraftwerk** (A) and **source** (B). The A/B isolation cases require each account to have no membership in the other test organization. Sign in as ring1 and leave kraftwerk if that membership is no longer needed. If ring1 is the last Owner while other members remain, appoint another Owner first. Keep cdr's intended ownership and robot enrollment intact.

Do not delete kraftwerk simply to fix a cross-membership prerequisite. Alternatively choose two dedicated, isolated test organizations and enroll one test robot in each. After leaving, allow the acceptance account runners to refresh their discovery, then start a **new Preflight** campaign. Previous campaigns retain their original results.
