# Milestone 6A.2 Stage 2 — Player BFF boundary

## Implemented boundary

The existing `SaturdayScout.Player` ASP.NET Core host now has a small same-origin BFF surface for a future React client. The Blazor Player pages remain in place and continue to use `PlayerPortalService` and `IPlayerApiClient.SendAsync`. The new HTTP proxy uses `HttpContext.User` directly because the Blazor `AuthenticationStateProvider` is circuit-scoped; it reuses the same `IPlayerApiAssertionIssuer` and configured server-side API `HttpClient` through `IPlayerApiClient.ForwardAsync`.

No API routes, domain rules, Identity tables, Admin behavior, or authentication scheme names changed. The proxy accepts only explicitly mapped route templates and constructs the backend API paths itself. It does not accept backend URLs, arbitrary path values, or forwarded authorization headers.

## Endpoint inventory

| Player-host route | Method | Backend API route | Mutation protection |
| --- | --- | --- | --- |
| `/auth/session` | GET | None | Player cookie + Player role |
| `/auth/antiforgery` | GET | None | Issues request token and antiforgery cookie |
| `/auth/login` | POST | None | Existing antiforgery-protected Identity login |
| `/auth/logout` | POST | None | Existing Player-authorized, antiforgery-protected logout |
| `/bff/api/v1/leagues/me` | GET | `/api/v1/leagues/me` | Player cookie + Player role |
| `/bff/api/v1/leagues/{leagueId}/slates/active` | GET | Matching active-slate API route | Player cookie + Player role |
| `/bff/api/v1/games` | GET | `/api/v1/games` | Player cookie + Player role; only `sport`, `season`, `week`, `page`, `pageSize` query keys pass through |
| `/bff/api/v1/leagues/{leagueId}` | GET | Matching league API route | Player cookie + Player role |
| `/bff/api/v1/leagues/{leagueId}/leaderboard` | GET | Matching leaderboard API route | Player cookie + Player role; only `scope`, `season`, `week` query keys pass through |
| `/bff/api/v1/slates/{slateId}` | GET | Matching slate API route | Player cookie + Player role |
| `/bff/api/v1/slates/{slateId}/picks/me` | GET | Matching own-sheet API route | Player cookie + Player role |
| `/bff/api/v1/slates/{slateId}/picks` | PUT | Matching picks API route | Player cookie + Player role + same-origin check + antiforgery header |
| `/bff/api/v1/slates/{slateId}/submit` | POST | Matching submit API route | Player cookie + Player role + same-origin check + antiforgery header |

League creation, membership management, commissioner slate editing, and administrator routes are intentionally not exposed through this Player BFF surface. The Admin host and API routes remain unchanged.

`/auth/session` reuses the existing route rather than introducing a duplicate session URL. It returns `userId`, a nullable `displayName` claim when available, and the Player role. It sends `Cache-Control: no-store`. It does not return email, Identity tokens, antiforgery tokens, the API assertion, or signing-key information.

## Authentication and CSRF behavior

- BFF routes require the existing `Player` authorization policy. The Player cookie remains host-only, `__Host-SaturdayScout.Player`, Secure, HttpOnly, SameSite=Lax, and uses the configured sliding and absolute session limits.
- Anonymous BFF and session requests receive a 401 Problem Details JSON response; authenticated users without the Player role receive 403.
- The forwarding method receives the authorized `HttpContext.User`, verifies authentication and role again, and asks the existing assertion issuer to create the short-lived RS256 credential. The bearer assertion is attached only to the server-to-server API request.
- React obtains a CSRF token with `GET /auth/antiforgery`. It sends the returned token in `X-CSRF-TOKEN` and includes the same-origin cookie on every POST/PUT mutation. The browser must use `credentials: "same-origin"` for fetch requests. Login and logout retain their current antiforgery behavior.
- For BFF PUT/POST requests, the host checks a supplied `Origin` against the request origin and validates the antiforgery token. Missing or invalid tokens return 400 Problem Details; a hostile Origin returns 403. JSON mutation bodies are limited to 64 KiB. SameSite is defense in depth, not a replacement for token validation.
- All BFF responses use `Cache-Control: no-store`. The proxy forwards only status, content type, response body, and `Retry-After`; it never forwards `Set-Cookie`, the backend URL, or the Authorization header to the browser.

The Origin check compares with the scheme and host seen by Player. When TLS terminates at a reverse proxy, configure trusted forwarded headers before relying on this comparison, HTTPS redirection, or secure cookies. Do not trust forwarded headers from arbitrary networks.

## Status and error propagation

Successful responses and API Problem Details are returned with the original status and body. In particular, 204 remains an empty 204; 404 and 409 are not converted into success or redirects; validation responses such as 400/422 retain the API's machine-readable body. Upstream connection failures and timeouts return a generic 503 Problem Details response. Unexpected proxy failures return a generic 500 Problem Details response and log only the exception type, not the exception message or request credentials. Large upstream responses over 4 MiB are rejected with 502.

## Local verification

1. Start PostgreSQL and SaturdayScout.Api using the existing Development configuration and signing key pair. Keep the API origin and RSA key material in local configuration/User Secrets.
2. Start the Player host using the existing local HTTPS profile, normally `https://localhost:7276`; the API commonly uses `https://localhost:7275`.
3. In a browser or API client, call `GET /auth/antiforgery`, retain the returned antiforgery cookie, and read the JSON `token`.
4. Submit the existing login form or a same-origin `POST /auth/login` with the CSRF header/token. Do not put passwords in command arguments or logs.
5. Call `GET /auth/session` and confirm only the safe session fields are returned. Test `/bff/api/v1/leagues/me` and the slate, picks, and standings routes with the Player cookie.
6. Before `PUT /bff/api/v1/slates/{slateId}/picks` or `POST /bff/api/v1/slates/{slateId}/submit`, reacquire a valid antiforgery token if needed and send it in `X-CSRF-TOKEN`. Verify missing token gives 400 and a hostile Origin gives 403.
7. Confirm an API 204, 404, 409, and validation error retain their status. Confirm `/bff/api/v1/admin/dashboard` and unknown BFF routes are not proxied.

The automated endpoint tests use a test-only authentication handler for authenticated BFF route checks so they do not depend on development Identity records. Separate existing tests verify the real Player cookie configuration, Admin-cookie isolation, and expired-cookie challenge. PostgreSQL integration tests exercise the existing Identity-to-domain account and API authorization behavior using disposable databases.

## Files changed in Stage 2

- `src/SaturdayScout.Player/PlayerBffEndpoints.cs` — explicit route allowlist, JSON body limits, origin and antiforgery checks, status forwarding, safe failure responses.
- `src/SaturdayScout.Player/PlayerApiClient.cs` — server-side raw response forwarding through the existing assertion issuer and API `HttpClient`; existing Blazor `SendAsync` behavior remains.
- `src/SaturdayScout.Player/PlayerAuthEndpoints.cs` — safe session fields and no-store headers on session/antiforgery responses.
- `src/SaturdayScout.Player/Program.cs` — BFF route mapping and machine-readable cookie challenge/forbid responses for session/BFF routes.
- `tests/SaturdayScout.Player.Tests/PlayerAuthenticationBoundaryTests.cs` — session, expiry, role, CSRF, hostile-origin, status propagation, no-open-proxy, logout, and assertion boundary tests.
- `docs/player-app/milestone-6a-stage-2.md` — endpoint and operational documentation.

## Remaining risks noted before Stage 3

Stage 3 has since added the React static assets and scoped `/react` fallback described in [Milestone 6A Stage 3](milestone-6a-stage-3.md). The other forward-looking risks below remain relevant:

- The current Player API assertion has no stateful replay cache; it remains short-lived and server-only as documented in Stage 1.
- The Origin comparison depends on accurate trusted proxy scheme/host forwarding in hosted environments. Azure ingress/reverse-proxy settings must be verified before deployment.
- API serialization is relayed as raw JSON, so the OpenAPI artifact remains the backend API contract and should continue to be checked for DTO changes.
- Request/response size limits may need measured adjustment if later API payloads materially grow; do not remove them without a bounded replacement.

Stage 3 can proceed to the React/Vite shell and typed API client. Begin with static assets and one read-only BFF workflow, then migrate drafts and submission with explicit conflict and ambiguous-response handling. Keep the Blazor routes available as a temporary fallback and run both React and Blazor flows against the same existing API/domain services during transition.
