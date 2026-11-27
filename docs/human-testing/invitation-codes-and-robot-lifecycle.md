# Invitation codes and robot removal

## Activate the changes

The operator rebuilds the Platform API/Console; startup applies the new EF migrations:

```bash
cd /home/cdr/ringfour/ringfour-platform
./up.sh -d --no-deps api console
```

For virtual robots, open Ringfour Development → Stacks → Settings → Update daemon
for each stack that needs unenrollment/re-pairing. This updates the Daemon code
that verifies release and resets its local enrollment on startup. Leave modified
source builds enabled for this development checkout. These stack operations are
reserved for the operator by `/home/cdr/ringfour/AGENTS.md`.

## Invite an account without email

1. Sign in as an organization **Owner**. Open **Organizations**, select the
   intended organization, then find **Invitations**.
2. Choose Operator, Developer or Owner and click **Create invitation code**.
   Copy **Share this invitation code** using the copy button or the selected
   read-only field. It is shown only in this session; refreshing loses the copy.
3. Share that code directly. The recipient signs in using their own account,
   opens **Organizations → Join organization**, pastes the code and clicks
   **Check invitation**. This also works for an account with no organizations.
4. Verify the organization name and role, then click **Join organization**.
   The workspace selector and membership view update immediately.
5. A code expires in seven days and admits one account. A second account cannot
   reuse an accepted code. Owners can revoke pending codes in Invitations.
   If the copy is lost, create a new code and revoke the unused old invitation.

No SMTP service or verified email address is required for invitation codes.
The code is a secret grant: share it only with the intended recipient. The
invitation's database UUID is a reference for management, not a join code.
Email invitations remain an optional, separate delivery method.

For **Local virtual AP**, A's owner creates an **Operator** code for the third
user. That new user joins **A's organization**, then connects their own account
runner and supplies their user UUID/runner label in the environment's Operator
role. Keep B outside A's organization. Role C instead uses another robot in A's
organization and can reuse A's existing account/runner.

## Unenroll or delete a robot

1. Sign in as its organization's **Owner**, open the robot's **Access** page and
   find **Enrollment and removal**. Other roles have no removal buttons.
2. Stop active Apps and wait for pending operations first. Active runs/slots
   and queued/accepted/running operations cause a conflict that states the
   blocking reason. Idle installed Apps are detached automatically; no explicit
   uninstall is required. Their installation revisions and run history remain.
   This administrative detachment does not claim a device-confirmed uninstall.
3. **Unenroll robot** revokes its credentials and unused enrollment tokens,
   disconnects it and clears the cloud enrollment. Its record and history stay.
4. **Delete robot** also removes it from active lists and direct access pages.
   Historic runs/installations remain available through history pages. A deleted
   UUID cannot be paired again; create/select another robot record instead.
5. After confirmed release, restart/update that stack's Daemon once from the
   development panel. With the Platform reachable, the updated Daemon verifies
   its release receipt, archives old state under `enrollment-history`, removes
   its old credential and generates a fresh pairing code.
6. Sign into the desired destination organization as its Owner. Use that **new
   pairing code** to enroll into a new or eligible unenrolled robot record.
   The new binding has a fresh deployment namespace and active local database.

Ordinary credential revocation, expiry, an unreachable Platform, invalid proof or
an unsupported API never authorize clearing the local enrollment. Missing local
credentials, another active writer or remaining owned App containers leave the
old binding intact for operator recovery. Retry after resolving the prerequisite;
do not erase device volumes to bypass these checks. The archive retains the old
SQLite state and local evidence files remain on disk, outside the new binding.
