# Admin Sprint 1B authentication implementation

## Architecture and routes

`SaturdayScout.Admin` is a .NET 10 Blazor Web App. Pages use static server rendering for login and MFA forms so normal HTTP responses can issue Identity cookies. The protected `/dashboard` page uses Interactive Server rendering and revalidates its server-side authentication state every minute. The Admin app and API use the same `ScoutIdentityDbContext`, `ApplicationUser` GUID, domain `Users.Id`, Identity roles, cookie name/scheme, and Data Protection application name. No second user store or mobile bearer scheme was introduced.

| Route | Purpose | Access |
|---|---|---|
| `/` | Administration entry | Public |
| `/login`, `POST /auth/login` | Email/password sign-in | Public form with antiforgery and rate limit |
| `/mfa`, `POST /auth/mfa` | TOTP challenge | Identity pending two-factor session, antiforgery and rate limit |
| `/recovery`, `POST /auth/recovery` | Single-use recovery code | Identity pending two-factor session, antiforgery and rate limit |
| `/enroll`, `POST /auth/enroll` | First-time TOTP enrollment | Recently password-authenticated administrator only |
| `/dashboard` | Protected Interactive Server placeholder | `SystemAdministrator` and completed MFA |
| `/access-denied` | Denied-access guidance | Public |
| `POST /auth/logout` | Sign out and rotate security stamp | Authenticated, antiforgery |
| `GET /auth/session` | Minimal admin session status | `SystemAdministrator` and completed MFA |

No public registration, invitation acceptance, or password-reset endpoint was added in this sprint. League administration UI, game browser, and slate editor remain Sprint 1C work.

## Cookie and MFA behavior

The shared Identity application cookie is `__Host-SaturdayScout`: Secure, HttpOnly, host-only, path `/`, and SameSite Lax. It has an eight-hour sliding lifetime and a 12-hour absolute authentication-time limit. Cookie validation checks the stored Identity security stamp, confirmed email, lockout state, disabled-account claim, matching domain user GUID, and absolute lifetime on each request. The `saturday_scout:disabled=true` Identity user claim is the current disabled-account marker; a dedicated status column would require a future reviewed migration. Interactive Server circuits independently revalidate every minute.

Password sign-in uses Identity lockout after five failures for 15 minutes. Login and MFA posts each have a five-per-minute IP rate limit. Responses use generic failure messages. Forms and cookie-authenticated API mutations validate antiforgery proof. Production enables HTTPS redirection and HSTS. Auth pages are marked `no-store` with a no-referrer policy. No password, authenticator key, cookie, or recovery code is intentionally logged.

First-time administrators receive a password-only cookie marked `amr=pwd`; it can reach enrollment but **cannot** satisfy `SystemAdministrator`. The enrollment page shows the Identity-generated authenticator key only to a recently password-authenticated administrator. A valid six-digit TOTP code enables two-factor authentication and issues a new server-signed cookie marked `amr=mfa`. Eight single-use recovery codes are shown once in the immediate response. Returning administrators must complete TOTP or use one recovery code; remembered-client MFA bypass is disabled. The admin policy also verifies the current database role, two-factor-enabled state, account status, and domain link, so a bare claim or stale role is insufficient.

The operator-only `bootstrap-admin` command accepts email through a command argument and prompts for a password without echoing it. It requires `SATURDAY_SCOUT_BOOTSTRAP_APPROVED=yes` and `--confirm-owned-email`, creates matching domain and Identity GUIDs in one transaction, assigns `Player` and `SystemAdministrator`, and rejects a second first-admin attempt. The flag represents an operator assertion that the email address is owned; this bootstrap exception marks that email confirmed without sending mail. It does not enable MFA or grant full dashboard access. No account is created by application startup.

The exact first-account command is:

```sh
SATURDAY_SCOUT_BOOTSTRAP_APPROVED=yes dotnet run --project src/SaturdayScout.Admin -- bootstrap-admin --email YOUR_OWNED_EMAIL --confirm-owned-email
```

Run it only after explicit operator approval, from an interactive terminal with `SATURDAY_SCOUT_DB_CONNECTION` and `SATURDAY_SCOUT_DP_KEYS_PATH` configured for the intended environment. The application also requires the existing Identity schema. The operator enters a display name and password twice at hidden prompts; neither is a command-line argument. Do not set the approval variable in a persistent shell profile. Repeating this command is refused once the first-admin marker exists, even if the SystemAdministrator role is later removed. There is no force option; a later administrator must be provisioned through an invitation/approved account workflow.

## Separately hosted API

The portal cookie does **not** automatically authenticate calls to another host. For the intended same-origin gateway, route `/admin` to the portal and `/api` to the API while preserving one public HTTPS origin and the host-only cookie. Both servers must point to the same persistent `SATURDAY_SCOUT_DP_KEYS_PATH`, use `SaturdayScout.SharedIdentity.v1` as the Data Protection application name, and share the Identity database. A browser request to `/api` then sends the cookie to the gateway, and the **API independently validates** its signature, security stamp, account status, GUID link, role, and policy. Cookie-authenticated API writes additionally require an antiforgery token from `GET /api/v1/auth/antiforgery` in the `X-CSRF-TOKEN` header. The Development-only header scheme remains isolated to Development for legacy API tests. Sprint 1C adds MFA-protected administrator endpoints under `/api/v1/admin`; see [`api-v1.md`](../api-v1.md). The portal itself invokes the shared application service in-process and does not forward its cookie to the API. The gateway topology still needs an end-to-end deployment test. Do not use credentialed cross-origin CORS as a shortcut.

## Data Protection and production configuration

Both hosts persist Data Protection keys in the configured filesystem directory. Outside Development, startup fails if `SATURDAY_SCOUT_DB_CONNECTION` is missing or `SATURDAY_SCOUT_DP_KEYS_PATH` is not an existing absolute directory; the Admin host also requires `SATURDAY_SCOUT_PUBLIC_ORIGIN` to be an HTTPS origin. The key directory must be a shared, access-controlled persistent volume with backups. Keys are not encrypted at rest by this code, so volume permissions and platform encryption are required before production deployment. Development defaults to a persistent temporary key directory if none is configured.

No new EF Core migration was generated or applied for Sprint 1B. Disposable test databases apply only the previously approved domain and Identity migrations.

## Recovery and remaining work

Administrators should store recovery codes offline in a secure manager. Each code is intended for one use. Integration tests confirm a recovery code can authenticate a session and replay is rejected. If the authenticator is unavailable, use one unused recovery code and then rotate the remaining recovery codes through a future protected account-security flow; recovery-code rotation is not currently exposed by the portal.

If both the authenticator and all recovery codes are lost, the current system deliberately leaves access blocked. There is no supported operator reset command or recovery endpoint, so do not edit Identity tables directly or weaken the administrator MFA policy. The safe recovery procedure to implement before production is: open and record an incident; have two authorized operators independently verify the administrator through a pre-established out-of-band channel; use a narrowly scoped, audited operator tool to revoke existing sessions/security stamps and reset only that user's authenticator state; require fresh TOTP enrollment before restoring administrator access; deliver newly generated recovery codes only to the verified administrator through a secure channel; and record operator identities, approvals, target user GUID, and completion without logging secrets. Until that tool and audit trail exist, recovery from loss of both factors requires the normal database backup/restore and incident-response process, performed by authorized operators, not an ad hoc row edit.

Password reset, email delivery, invitation issuing, privileged audit persistence, durable server-farm rate limits, key-at-rest encryption, reverse-proxy trust configuration, and an end-to-end same-origin gateway test remain open. The portal invokes the shared application service in-process rather than making HTTP calls to administrator API routes.

Implementation follows the official [Blazor authentication guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0), [Identity MFA guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/mfa?view=aspnetcore-10.0), and [Data Protection configuration guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).
