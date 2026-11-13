# Milestone 6A Stage 3 — React Player PWA foundation

## Scope and hosting boundary

`src/SaturdayScout.Player.Web` is the React/TypeScript/Vite client. The existing `SaturdayScout.Player` host, Blazor routes, Identity configuration, Player cookie, RS256 assertion issuer, API, Admin application, and database schema remain in place. React is introduced side by side under `/react`; it does not replace the root Blazor application. The Player host serves the generated `wwwroot/react` files and maps history fallback routes only below `/react`. It does not install a root catch-all that could intercept `/auth`, `/bff`, or existing Blazor routes. Until the React bundle is built, this static route is not mapped.

The BFF uses the existing same-origin endpoints only:

- `GET /auth/session`, `GET /auth/antiforgery`, `POST /auth/login`, `POST /auth/logout`
- `GET /bff/api/v1/leagues/me`
- `GET /bff/api/v1/leagues/{leagueId}/slates/active`
- `GET /bff/api/v1/leagues/{leagueId}/leaderboard`

The React client does not call the API origin and does not receive RS256 assertions, signing keys, Identity tokens, or private session material. Existing Player login, logout, role enforcement, API authorization, and antiforgery remain authoritative. Login/logout use the existing antiforgery token endpoint and `X-CSRF-TOKEN`. API reads use `credentials: same-origin`, `cache: no-store`, and a memory-only TanStack Query cache. No response cache is persisted. The only browser-persisted value is a selected league ID preference; it is checked against the authorized membership list returned by the server on every session load.

## Project, routes, and implemented workflows

The Vite app uses strict TypeScript, React Router, and TanStack Query. Routes under `/react/` are:

| Route | Behavior |
| --- | --- |
| `/react/login` | Existing Identity login endpoint, antiforgery protected |
| `/react/` | Authenticated dashboard, authorized league list and active published slate |
| `/react/picks` | Deliberate placeholder linking to the existing Blazor Picks workflow |
| `/react/standings` | Selected league leaderboard using official API results |
| `/react/league` | Selected league summary and league selector |
| `/react/profile` | Profile placeholder with safe session display name |

Unauthenticated protected routes go to login with a local return path. The React application reads `/auth/session` to restore the existing Player cookie session. A 401 from a protected BFF read expires the in-memory session and returns the user to login; 403 displays an access-denied state. Generic validation, conflict, missing-resource, upstream, and network errors are mapped in the typed API helper. It does not assume every successful endpoint returns JSON; 204 is treated as an empty result.

The OpenAPI 3 artifact is generated into `src/api/openapi.d.ts` using the pinned `openapi-typescript` command. The current checked-in OpenAPI document includes routes/request schemas but omits response schemas. Therefore route/request types are generated, while the read response interfaces mirror `ScoutContracts.cs` in `src/api/contracts.ts`. Enrich response schemas in the API contract before relying on fully generated response types in later work.

## Local development

Prerequisites: Node 24.14+ and npm, PostgreSQL and the existing local Identity/API configuration, and the already trusted ASP.NET Core HTTPS development certificate. Do not create or trust a replacement certificate as part of this setup.

1. Start PostgreSQL with the repository's existing local setup.
2. Start `SaturdayScout.Api` at `https://localhost:7275` using its existing Development configuration and public assertion key:

   ```sh
   ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SaturdayScout.Api --launch-profile https
   ```

3. Start `SaturdayScout.Player` at `https://localhost:7276` using its existing Development database, Player private assertion key, Data Protection, Identity configuration, and `Player:ApiOrigin=https://localhost:7275`. Keep local secrets in User Secrets or other ignored local configuration. The checked-in Player HTTPS profile chooses a dynamic port, so pin the expected local port with:

   ```sh
   ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SaturdayScout.Player --no-launch-profile --urls https://localhost:7276
   ```

4. Check the already trusted ASP.NET Core certificate, then export it and its key to a private temporary directory (PEM format produces a certificate and a separate `.key` file):

   ```sh
   dotnet dev-certs https --check --trust
   CERT_DIR="$(mktemp -d)"
   dotnet dev-certs https --export-path "$CERT_DIR/localhost.pem" --format Pem --no-password
   chmod 600 "$CERT_DIR/localhost.key"
   export SCOUT_DEV_TLS_CERT="$CERT_DIR/localhost.pem"
   export SCOUT_DEV_TLS_KEY="$CERT_DIR/localhost.key"
   export NODE_EXTRA_CA_CERTS="$CERT_DIR/localhost.pem"
   ```

   This uses the existing certificate and does not install or trust a new one. Keep the temporary directory outside the repository and remove it when finished. Set these process environment variables for Vite:
   - `SCOUT_DEV_TLS_CERT` — exported certificate PEM path
   - `SCOUT_DEV_TLS_KEY` — exported private key PEM path
   - `NODE_EXTRA_CA_CERTS` — a certificate file Node should trust for the local Player HTTPS origin (if needed by Node's proxy TLS validation)
5. From `src/SaturdayScout.Player.Web`, copy `.env.example` to the ignored `.env.local` (its local Player host URL keeps the Picks placeholder linked to the existing Blazor workflow), install locked dependencies, and start Vite:

   ```sh
   cp -n .env.example .env.local
   npm ci
   npm run dev
   ```

   Browse to `https://localhost:5173/react/`. The Vite server requires both TLS certificate paths and will fail closed if absent. Its `/auth` and `/bff` proxies target `https://localhost:7276`, preserve the browser `Host` and `Origin`, and validate the Player host certificate. This is intentional: the existing Origin/antiforgery protections remain enabled. Do not set `changeOrigin` to true, disable TLS verification, add permissive CORS, or use a browser HTTP origin.

For a production-style local static-host check, run `npm run build` before starting/restarting Player. Vite writes the bundle to the ignored generated directory `src/SaturdayScout.Player/wwwroot/react`; Player then serves the React app at `/react/` while its Blazor app remains at `/`.

Refresh API-derived information through the browser reload. No static/API service worker caching is enabled at this stage. The app includes a standalone manifest and icon references but no offline workflow.

## Verification

- Frontend `npm run build`: TypeScript project compilation and Vite production build.
- Frontend `npm test`: React route, session, protected-route, membership validation, dashboard empty-state, standings, logout, and BFF error-handling tests.
- Backend: run the complete solution build/test suite. PostgreSQL persistence tests must use `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION` set only in the test process to a local administrative connection; test fixtures create GUID-suffixed disposable databases and must clean them up. Never point this variable to `saturday_scout` or a production database.

Manual smoke check: sign in at `/react/login`, verify the dashboard loads authorized memberships and active-slate state, switch between two authorized leagues, open standings, inspect the league and profile pages, and sign out. Confirm `/react/` refreshes to the React app, while `/`, `/login`, `/picks`, `/auth/session`, and `/bff/api/v1/leagues/me` retain their existing host behavior. Test unauthenticated direct navigation and confirm login appears; confirm an unknown API BFF route is not forwarded.

## Files introduced or updated

- `src/SaturdayScout.Player.Web/` — React/Vite source, strict TS configuration, lockfile, generated route/request types, tests, styles, manifest and icons.
- `src/SaturdayScout.Player/Program.cs` — conditional React static-file serving and `/react`-scoped SPA fallback.
- `.gitignore` — local frontend environment files and generated React host output.

## Risks and Stage 4 recommendation

- The backend OpenAPI artifact lacks response DTO schemas, so those read contracts are currently handwritten mirrors and can drift. Add response schemas and contract drift checks before expanding the React surface.
- This stage has no browser automation against a live Player host; verify the Vite HTTPS proxy and mobile layout manually before beta use.
- Local TLS export paths vary by developer machine. Use the trusted existing ASP.NET Core development certificate and keep the private key outside the repository with restrictive file permissions.
- Picks editing and submission remain in Blazor. Stage 4 should migrate one write workflow only after documenting draft/submission endpoint payloads, concurrency tokens, deadline rules, 409 behavior, and ambiguous network outcomes. Keep Blazor as the fallback until manual and automated parity checks pass.
- The profile and Picks views are placeholders. Invitation acceptance/enrollment is not implemented in React; use the existing invitation workflow.
- Azure proxy forwarded-header trust, persistent Data Protection storage, deployment routing, and production static asset delivery remain deployment-stage work.

No database migrations, API business-rule changes, or schema changes are part of Stage 3.
