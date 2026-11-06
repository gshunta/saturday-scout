# Authentication architecture revision (proposed; approval gate)

**Status:** Identity persistence, Admin Sprint 1B cookie login, TOTP enrollment and recovery, shared Data Protection key storage, and the minimal Blazor portal are implemented. The two Identity migrations were applied to the local development database on 2026-11-01. Invitation issuing, email, password reset, privileged audit persistence, and administration features remain. See `identity-migration-review.md` and `admin-portal/authentication-implementation.md`.

## Repository findings (2026-11-01)

- `SaturdayScout.Domain.User` has a GUID `Id`, required `DisplayName` and unique `ExternalSubjectId`, and optional `Email`. `Users.Id` is the principal key for `Leagues.CommissionerUserId`, `LeagueMembers.UserId`, and `PickSheets.UserId`; picks belong to sheets and select teams. All these foreign keys use restrictive delete behavior.
- `LeagueMember.Role` is a **league-scoped** `Member`/`Commissioner` value. `ScoutStore` checks membership or commissioner status against the requested league. A global `LeagueCommissioner` Identity role must not grant access to every league.
- The API has one authenticated `/api/v1` group. It gets the actor GUID from `ClaimTypes.NameIdentifier`. No explicit role policies exist. In Production the default handler denies authentication; Development accepts `X-Dev-Subject`, creates a domain user if absent, and uses the resulting GUID as the actor. That header must remain impossible to authenticate in Production and be retired from normal development after Identity cutover.
- EF Core migrations `InitialCreate` and `CollegeFootballDataTraceAndUsage` manage the current PostgreSQL schema. The application does not auto-migrate. The local database currently has 0 users, 0 leagues, 0 memberships, 0 pick sheets, and 0 picks; it has 682 imported teams and 56 imported games that must remain untouched.
- `AddMemberRequest` currently looks up a user by `ExternalSubjectId`. This is incompatible with invitation by confirmed email and needs a planned API change; do not use an untrusted client-supplied subject for account creation.

## Identity model and migration strategy

Use `ApplicationIdentityUser : IdentityUser<Guid>` and `IdentityRole<Guid>` in an Identity EF context backed by the **existing PostgreSQL database**. Put Identity tables in a distinct `auth` schema, with a separate migrations history table. Keep `SaturdayScoutDbContext` and the domain `Users` table in place. `ApplicationIdentityUser.Id` equals `Users.Id` and has a required, unique foreign key to `Users.Id`. This is the explicit one-to-one mapping and the invariant checked at sign-in. `Users.Id` remains the single actor/domain ID in leagues, sheets, standings, and audit entries. No email or external subject becomes an authorization key.

Create new accounts in one shared PostgreSQL transaction across the domain and Identity contexts: reserve a valid invitation, create the domain `User` with a new GUID, create its Identity user with the **same GUID**, assign the `Player` role and any approved additional role, and mark the invitation redeemed. Existing domain users, if any appear before cutover, must be matched manually using verified ownership; never auto-link by display name or the Development subject header. Their GUIDs and all existing foreign keys stay fixed. Block sign-in if either side of the one-to-one pair is absent or disabled.

Proposed additive migration, only after approval:

1. Back up the database and count domain users, referenced users, imported games, and provider ledger rows. Verify there are no duplicate normalized emails among any existing real users. Decide ownership of any pre-existing domain users individually.
2. Add Identity tables (`AspNetUsers`, roles, user roles, claims, logins, tokens, role claims), the one-to-one FK to `Users`, and an invitation table. Add the append-only privileged audit table and persistent Data Protection key storage before opening Production administration. Keep existing domain tables and foreign keys intact. Review generated SQL for destructive operations; do not call `EnsureCreated` or auto-apply migrations at startup.
3. In a separate controlled data step, pair any pre-existing real domain users to new Identity rows using their existing GUIDs. Require confirmed email setup and password reset; do not invent passwords. Do not pair Development-only users without an owner decision. Since the current local user count is zero, no local relationship backfill is needed, but verify each deployment independently.
4. Deploy account, policy, and cookie changes after the Identity schema is present. Remove the Production deny-only default and make the Identity application cookie the explicit browser scheme. Keep the Development header handler isolated to Development test configuration until tests migrate, then remove it.
5. Verify all pre/post row counts and FK checks, authenticate an invited non-admin and MFA-enrolled admin, and test Production rejection of `X-Dev-Subject`. Roll back application binaries if cutover fails; retain additive tables until a reviewed rollback plan is approved. Never roll back by dropping domain user, league, pick, team, or game rows.

## Authorization model

Identity roles are `SystemAdministrator`, `LeagueCommissioner`, and `Player`. The latter two are account classifications; league authorization still checks `LeagueMembers` for the **specific league**. Assign or remove `LeagueCommissioner` when a user's league commissioner memberships change, but a stale global role must never authorize a league action. `SystemAdministrator` is reserved for administrative routes and does not silently take ownership of player picks. Policies: `AuthenticatedPlayer` requires a linked, confirmed, active account; `SystemAdministrator` additionally requires that role and a current MFA-authenticated session in Production; `LeagueMember` and `LeagueCommissioner` are resource-based policies backed by current membership rows. Existing store-level checks remain as defense in depth.

## Registration, recovery, and bootstrap

No public signup or self-assigned role. An authorized administrator creates a single-use, random, short-lived invitation bound to an email and allowed role/league; persist only a token hash, expiry, issuer, and redemption data. Invite acceptance validates the token and normalized email, creates the linked pair transactionally, sends an email confirmation link, and does not sign in until confirmation. Password reset is a generic-response, expiring, single-use Identity token flow delivered only through a configured email sender. Email change requires confirmation of the new address. Email transport and public callback URL must be configured before Production can accept invitations or reset requests.

Bootstrap the first `SystemAdministrator` through an explicit, one-time operator command with a short-lived invitation delivered out of band to a configured email. It creates no hardcoded password, prints no credential, cannot run on normal startup, fails if a system admin already exists, and records an audit event. The recipient confirms email, sets a password, enrolls an authenticator app, and passes MFA before admin access. Recovery uses limited, audited recovery codes and a documented break-glass procedure; ordinary password reset does not waive MFA.

## Browser and API authentication

Admin Sprint 1 uses Identity's server-side application cookie for the Blazor Server administration portal. Use `Secure`, `HttpOnly`, host-only, `SameSite=Lax`, short idle expiration, sliding expiration with security-stamp revalidation, and a persistent shared Data Protection key ring. Require HTTPS/HSTS in Production. Set `lockoutOnFailure: true`, a bounded attempt/lockout policy, and endpoint rate limits for login, invitation, confirmation, reset, and MFA. Validate antiforgery tokens on every cookie-authenticated state-changing request, including API mutations; check `Origin`/`Referer` for sensitive browser endpoints. Treat unauthenticated API calls as 401 and denied calls as 403, not HTML login redirects.

The portal is a **separate server process** but should initially share one public origin with the API through a reverse proxy (`/admin` and `/api`). Both processes use the same Identity store, cookie scheme/name, Data Protection application name/key ring, and security-stamp policy. Portal server requests to the API forward the incoming session cookie and antiforgery proof only for that user's request; never substitute a privileged service account. Cross-origin browser credentials are not part of Sprint 1. If a common origin is unavailable, choose and review a BFF/session exchange design before deployment; do not open credentialed CORS as an ad hoc workaround.

SwiftUI authentication is deferred. A future native flow must issue revocable, short-lived bearer access credentials with scoped refresh, secure device storage, and the same Identity GUID mapped to `Users.Id`. Native requests must use a separate explicit bearer scheme. Do not treat Identity's built-in proprietary bearer tokens as JWT/OIDC tokens or enable a public token endpoint in Sprint 1. Cookie and bearer schemes must be selected explicitly per route/client when mobile work begins.

## Endpoint design for Admin Sprint 1

| Endpoint | Access | Behavior |
|---|---|---|
| `POST /admin/auth/login` | Anonymous, rate limited | Password sign-in, lockout on failure, MFA challenge when enrolled; issue cookie only after confirmed email and required MFA. |
| `POST /admin/auth/mfa` | Pending MFA session, rate limited | Validate TOTP or recovery code; issue completed admin session. |
| `POST /admin/auth/logout` | Cookie + antiforgery | Revoke current browser session. |
| `GET /admin/auth/session` | Cookie | Return current linked user ID, roles, and MFA state; no secrets. |
| `POST /admin/invitations` | SystemAdministrator + MFA + antiforgery | Create scoped invitation; audit issuer and target. |
| `POST /admin/auth/invitations/accept` | Valid invitation, rate limited | Create linked account and start email confirmation; no public signup. |
| `GET/POST /admin/auth/confirm-email` | Token, rate limited | Confirm address without granting a role. |
| `POST /admin/auth/forgot-password` | Anonymous, rate limited | Always give a generic response; send reset link when eligible. |
| `POST /admin/auth/reset-password` | Token, rate limited | Reset password and invalidate existing sessions. |
| `POST /admin/auth/mfa/setup` | Confirmed, recently password-verified admin enrollment session + antiforgery | Enroll authenticator and recovery codes before admin access; enrollment session cannot call other admin endpoints. |

All token-bearing links use configured HTTPS origins and avoid logging query tokens. Admin API endpoints use `SystemAdministrator` or a resource-scoped commissioner policy, audit each privileged mutation, and retain existing domain validation. `/api/v1` player operations continue to derive the actor from the authenticated GUID and check membership; `AddMemberRequest` must move from external-subject lookup to an accepted invitation or linked user GUID after a separate contract review.

## Privileged audit log

Write append-only records with UTC time, actor GUID (or operator identity for bootstrap), action, target type/ID, league ID when applicable, outcome, and correlation ID. Include role changes, invitations, account disable/enable, MFA reset, league membership and commissioner changes, slate publication, and administrative result corrections. Exclude passwords, reset/invitation tokens, cookie values, and raw sensitive request bodies. Commit successful mutation and audit record in one transaction; record denied/high-risk attempts separately without making audit failure a silent success. Restrict audit read access and define retention and export before Production.

## Security acceptance criteria

- Production has no Development-header authentication, anonymous registration, or anonymous admin writes. Unlinked, unconfirmed, disabled, or locked accounts cannot sign in or act through stale cookies.
- A confirmed invited player can access only leagues with current membership; a commissioner cannot change another league; an admin without completed MFA cannot reach admin actions.
- Every browser mutation rejects missing/invalid antiforgery proof. Cookie flags, HTTPS, key persistence, session invalidation, lockout, and rate limits are exercised in integration tests.
- Invitation reuse, expiry, wrong-email acceptance, password-reset enumeration, brute-force attempts, and role self-escalation fail safely. Admin bootstrap is one-time and audited.
- Migration validation preserves every existing GUID and foreign key plus team/game/provider-ledger counts. Tests cover an existing user with league membership and picks, even though the current local database has none.
- Separate-host portal-to-API requests preserve user identity and produce 401/403 correctly. No mobile bearer endpoint is available in Sprint 1.

## Risks and decisions to resolve

1. **Portal public topology:** approve the common-origin reverse proxy and shared key ring, or design an alternative BFF before deployment.
2. **Email delivery:** choose provider, sender domain, public callback origin, and operational handling for bounced invitations and reset messages.
3. **Existing users elsewhere:** the local database is empty of users, but every target database needs its own identity/ownership inventory before migration.
4. **MFA operations:** choose recovery-code custody and break-glass procedure; avoid an admin MFA bypass during password reset.
5. **Current member API:** replacing `ExternalSubjectId` with invitation/linked-user semantics needs contract and test updates.
6. **Audit and key retention:** set retention, access, backup, and rotation policies before Production.

Implementation references: [Identity model customization](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/customize-identity-model?view=aspnetcore-10.0), [Identity configuration](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0), [antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), and [Identity API token behavior](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0).
