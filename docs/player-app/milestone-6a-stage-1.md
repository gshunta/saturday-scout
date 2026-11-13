# Milestone 6A.2 Stage 1 — API contract and authentication boundary

## Outcome

SaturdayScout.Api now has a repeatable, versioned OpenAPI 3 artifact for its existing `/api/v1` endpoints. The generator runs the API in Development on a temporary loopback port, fetches the existing OpenAPI document, validates its version and Player routes, and writes `docs/api/openapi-v1.json`. It uses inert configuration in the child process and does not connect to PostgreSQL. No endpoint behavior, database schema, Identity setup, or authentication policy was changed.

Generate or refresh the artifact from the repository root:

```sh
scripts/generate-api-contract.sh
```

The API project is built first. OpenAPI is mapped only when `ASPNETCORE_ENVIRONMENT=Development`; production continues to return 404 for `/openapi/v1.json`. The document is contract version `1.0.0` (distinct from the OpenAPI format version). It currently contains 24 routes and 13 schemas, including Player, commissioner, and administrator routes because they share the existing `/api/v1` surface. The API has a `/api/v1` route prefix but no separate API versioning package or negotiated version header.

When the React client is introduced, generate TypeScript types from this artifact rather than duplicating DTO declarations. A lightweight first option is `openapi-typescript docs/api/openapi-v1.json -o <client-types-output>`; pin the generator in the frontend toolchain and regenerate in CI, then review contract diffs. This stage adds no Node dependency.

## Proposed same-origin topology

Keep the current Player host as the React asset host and future lightweight BFF. This reuses the existing Player Identity integration, dedicated cookie, assertion issuer, and server-side API client. It avoids adding another independently secured host and keeps browser requests same-origin. The current Player app is still Blazor Interactive Server; the future BFF routes below are a proposal, not implemented in this stage.

| Purpose | Local development | Azure beta proposal |
| --- | --- | --- |
| Player React app and BFF | `https://localhost:7276/` | `https://scout.example.com/` |
| Player auth routes | same origin, existing `/auth/*` | same origin, `/auth/*` |
| Future browser-to-BFF API routes | same origin `/bff/api/v1/*` | same origin `/bff/api/v1/*` |
| Existing backend API | server-to-server `https://localhost:7275/api/v1/*` | private/internal API origin `/api/v1/*` |
| Existing Admin portal | separate local Admin origin/port | separate `https://admin.example.com/` host |

The browser should call only the Player origin. The BFF authenticates the Player cookie, applies CSRF validation to mutations, and uses its server-side RS256 assertion to call the existing API. The API remains the authority for league membership, slate state and deadlines, confidence validation, submitted-sheet immutability, and scoring. Do not forward the Player cookie to the API, return the assertion to JavaScript, or put the API signing key in static assets. Existing Admin routes and its authentication remain separate and unchanged during Player migration.

### Cookie, antiforgery, and transport boundaries

- The existing Player cookie is `__Host-SaturdayScout.Player`: host-only, path `/`, Secure, HttpOnly, and SameSite=Lax. Keep the `__Host-` requirements: HTTPS, path `/`, and no `Domain` attribute. Its root path means it is sent to the same host's BFF routes but not to the API or Admin host.
- The existing Player host exposes `/auth/antiforgery`, `/auth/login`, `/auth/logout`, and `/auth/session`. Login/logout mutations use antiforgery validation. The React client should fetch the antiforgery token from the same origin and send the configured `X-CSRF-TOKEN` header with cookie-authenticated state changes. Keep SameSite as defense in depth; it does not replace antiforgery validation.
- RS256 assertions remain server-to-server credentials. Player stores the private key; API stores the corresponding public key ring. Only the BFF-to-API request carries the assertion.
- Serve the app and BFF over HTTPS. For a reverse proxy or container ingress, process forwarded headers only from explicitly trusted proxy addresses/networks, before HTTPS redirection and secure-cookie decisions. Configure the public HTTPS origin and API internal origin separately; do not rely on localhost defaults in deployed configuration.
- Browser-to-BFF requests are same-origin, so the React app does not need cross-origin API access. Keep the backend API private/restricted where the hosting topology permits. If an API origin is intentionally exposed to another client, define its CORS policy narrowly for that client rather than using wildcard origins with credentials.

### Data Protection persistence

Player and Admin use separate cookie names and remain on separate hosts. They do not need to share a browser cookie or Data Protection key ring for the proposed topology. The current `SATURDAY_SCOUT_DP_KEYS_PATH` filesystem configuration must point to durable storage in Azure; a container-local writable directory can be lost on restart or deployment. For multiple Player replicas, all replicas must use a shared persistent key ring and stable application name. Protect persisted keys at rest (for example with an Azure Key Vault key). If a future design deliberately shares cookies across hosts, align the key ring, application name, cookie domain, and cookie security design only after a separate review.

## Verified trust boundaries

1. ASP.NET Core Identity remains the canonical user store, linked to the existing domain user by GUID.
2. The browser authenticates to Player with the dedicated Player cookie. Admin has a separate cookie and authentication configuration.
3. The Player server checks the authenticated principal and Player role before issuing a short-lived RS256 assertion. The private key is loaded only by the Player server.
4. The API validates issuer, audience, client ID, key ID, signature, lifetime, and token identifiers, then resolves the subject through Identity and re-checks account eligibility and Player role. API resource authorization still checks membership and domain rules.
5. Development-only API identity simulation remains Development-only. Production OpenAPI is not mapped. Existing API mutating cookie-authenticated requests retain antiforgery validation.

This stage added regression checks for dedicated Player cookie configuration and Admin-cookie isolation, unauthenticated protected access, login antiforgery, unauthenticated logout, RS256 assertion contents/private-key non-disclosure, and invalid assertion signature/expiry/client/audience/issuer. Existing API integration coverage also checks Development-only auth and production OpenAPI denial. The checked-in contract is checked for required Player paths, schemas, and absence of private signing material.

## PostgreSQL-backed test setup

The PostgreSQL tests are gated by `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION`. No safe disposable test connection was configured for this run, so PostgreSQL-backed tests were not run. Do not set this variable to `saturday_scout` or to a production database.

To enable them locally, provide `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION` only in the test process environment. It must connect to a local disposable PostgreSQL server using a role allowed to create and drop databases. Test fixtures replace the database name with `postgres` for administration, create uniquely GUID-suffixed databases (for example `saturday_scout_api_test_<guid>`), apply migrations to those isolated databases, and drop only those uniquely named databases with `WITH (FORCE)` during fixture cleanup. The fixture connection uses non-pooling for the disposable target. Run the suite, inspect that no `saturday_scout_*_<guid>` test databases remain, then remove the process-scoped variable. Never run the integration suite against the development database as its maintenance connection.

## Files changed in Stage 1

- `src/SaturdayScout.Api/Program.cs` — named/versioned OpenAPI document metadata; Development-only mapping is preserved.
- `scripts/generate-api-contract.sh` — repeatable local contract generation and validation.
- `docs/api/openapi-v1.json` — generated versioned API contract.
- `tests/SaturdayScout.Api.IntegrationTests/ApiContractArtifactTests.cs` — artifact regression coverage.
- `tests/SaturdayScout.Api.IntegrationTests/PlayerAssertionPrevalidationTests.cs` — wrong-issuer rejection coverage.
- `tests/SaturdayScout.Player.Tests/PlayerAuthenticationBoundaryTests.cs` — unauthenticated logout and private-key non-disclosure coverage.
- `docs/player-app/milestone-6a-stage-1.md` — this baseline and topology.

## Remaining decisions

- Confirm the eventual Player and Admin hostnames and whether the API is fully private or has a narrowly exposed client surface.
- Choose the Azure persistent Data Protection provider and key-at-rest protection. Ensure every Player replica shares the same key ring and application name.
- Decide whether `/auth/*` paths remain unchanged for React or are grouped under a new `/bff/auth/*` prefix. Keeping the current routes minimizes migration risk.
- Decide whether the artifact is committed and reviewed as a generated contract (recommended) and whether CI should fail when regeneration produces a diff.
- Establish proxy trust ranges and health-check/readiness routes as part of the hosting implementation.

## Stage 2 recommendation

Implement the React shell and same-origin BFF incrementally on the existing Player host. First add a React/Vite build output and static asset fallback while preserving the current login/session endpoints. Add a typed client generated from the checked-in OpenAPI contract, then add only the BFF routes needed by the first migrated Player workflow. Keep the existing API and domain services authoritative. Migrate one read flow, then draft writes and submission with explicit conflict and ambiguous-response handling. Preserve the Blazor app behind a temporary development route until regression and manual acceptance are complete. Do not begin Admin conversion or native OIDC work in that stage.
