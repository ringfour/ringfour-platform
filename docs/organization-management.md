# Organization management and invitations

The Console's **Organizations** view lists the caller's workspaces, creates a
workspace, displays each member's name, email, profile picture and user ID,
changes member roles, and removes members. Organization owners can also invite an email address, inspect recent
invitations, resend pending or expired invitations, and revoke invitations.
Other workspace members can read the roster but cannot manage membership.

## Email invitations

The API sends invitation mail through MailKit and an operator-provided SMTP
service. Configure these environment variables for the Platform API:

| Variable | Purpose |
|---|---|
| `SMTP_HOST` | SMTP server hostname |
| `SMTP_PORT` | `587` for STARTTLS (default) or `465` for implicit TLS |
| `SMTP_USERNAME` | Optional SMTP username |
| `SMTP_PASSWORD` | SMTP password when a username is set |
| `SMTP_FROM_ADDRESS` | Sender email address |
| `SMTP_FROM_NAME` | Sender display name (defaults to Ringfour) |
| `CONSOLE_ORIGIN` | Origin used for invitation links |

Without valid SMTP settings, sending an invitation returns `503 email_delivery_failed`;
the Console displays that error. SMTP credentials belong in the ignored server
environment or secret manager, never in source control.

An invitation email can be sent to an existing Auth0/Ringfour user or to someone
who still needs an account. The recipient opens the single-use link, signs in
with an existing account or creates one, then accepts. The API verifies the
email through Auth0 `/userinfo` and requires an exact match with the invited
address and `email_verified: true`. No Auth0 Management API client is needed.
The invitation expires after seven days. Resending rotates its token and
extends the expiry; revocation makes the previous link unusable. Only a SHA-256
hash of the token is persisted.

## Acceptance

After configuring SMTP and reloading the existing Platform stack:

1. As an owner, invite an existing test account to a workspace as Developer.
2. Confirm one email arrives, sign in with the existing account, and accept.
3. Confirm the new workspace and Developer role appear in `ringfour-cli whoami`
   and Console Organizations.
4. Invite a new test email, complete Auth0 sign-up from the link, and accept.
5. Verify wrong-email acceptance, expired links, revoked links and reuse after
   acceptance are rejected; resend creates one usable replacement link.
6. Confirm an Operator cannot list, create, resend or revoke invitations.

The SMTP provider is environment-specific, so outbound delivery must be
accepted against the configured provider before treating email invitations as
live-accepted.

## Personal profile

The Console has a **My profile** entry at the bottom of the sidebar under
**Settings**. It displays the caller's verified Auth0 email and lets them set a
display name, public HTTPS profile picture URL, and phone number. Name and
picture are shown in organization member rosters. Email stays managed by the
verified Auth0 identity and cannot be edited here. The API stores the profile
fields on the local user and exposes them through `GET /v1/me`; `PATCH /v1/me`
updates only the editable fields.
