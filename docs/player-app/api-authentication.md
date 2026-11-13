# Player-to-API authentication boundary

## Same-origin BFF routes

`SaturdayScout.Player` serves the existing Blazor Player application and now also hosts the initial same-origin BFF surface for a future React client. The authenticated session is exposed at the existing `GET /auth/session` route; it returns only the user GUID, an optional given-name display claim, and the Player role, with `Cache-Control: no-store`. The existing `/auth/login`, `/auth/logout`, and `/auth/antiforgery` routes remain the only Player login/logout flow.

React-facing API paths use `/bff/api/v1/*`. The Player host maps a fixed allowlist for league discovery, active slate, game browsing, slate and pick-sheet reads, leaderboard, draft save, and submission. It constructs the matching `/api/v1/*` API route internally. No caller-supplied backend URL or arbitrary API path is accepted. Administrator, league-creation, membership-management, and commissioner write routes are not proxied.

All BFF API routes require the Player cookie and Player role. Cookie-authenticated writes (`PUT` picks and `POST` submit) require an antiforgery token from `GET /auth/antiforgery` in the `X-CSRF-TOKEN` header. They reject a mismatching `Origin`, require JSON, and cap JSON request bodies at 64 KiB. React fetch calls should use same-origin credentials. Private BFF responses are `no-store`.

The BFF forwards the API's response status and body, including 204, 404, 409, and validation Problem Details. It does not forward the server-to-server Authorization header or response cookies. The API assertion remains issued and attached inside the Player server process. Upstream failures become generic 503 Problem Details; unexpected forwarding failures return generic 500 Problem Details without exception details.

See [Milestone 6A.2 Stage 2](milestone-6a-stage-2.md) for the complete route inventory, local verification steps, and known hosting constraints.

## Trust flow

The Player Blazor server is a backend-for-frontend. The browser holds only the Player-specific secure cookie. On each API call, the Player server reads the authenticated server-side principal, confirms it has the `Player` role and a GUID subject, and issues a signed RS256 JWT assertion. It sends the assertion in the `Authorization: Bearer` header to the configured API origin. Browser code never receives the assertion or signing key.

Assertions use issuer `SaturdayScout.Player`, audience `SaturdayScout.Api`, the configured client ID, a GUID `sub`, `iat`, `exp`, and a random per-request `jti`. The default lifetime is 120 seconds. The API accepts only RS256, validates issuer, audience, signing-key ID, signature, not-before/expiry, client ID, a well-formed token ID, and a maximum lifetime of 180 seconds. It then resolves `sub` to the canonical Identity user and linked domain `Users` row, and checks confirmation, lockout, disabled status, and the current `Player` role. API role claims are rebuilt from the Identity store; role claims supplied in the assertion do not grant access.

The API still authorizes league membership, resource ownership, deadlines, and pick-sheet rules on every operation. A valid assertion authenticates a user; it does not grant access to every league. Assertions are bearer credentials and can be replayed by anyone who steals one until it expires; HTTPS, server-only handling, and the short lifetime limit this exposure. The API does not currently keep a stateful token replay cache.

## Key configuration

The Player server needs a private RSA PEM file of at least 3072 bits. Configure the path and key ID outside source control. For local development, use User Secrets or environment variables:

- `ApiAssertion:KeyId`
- `ApiAssertion:PrivateKeyPath`
- `ApiAssertion:Issuer` (normally `SaturdayScout.Player`)
- `ApiAssertion:Audience` (normally `SaturdayScout.Api`)
- `ApiAssertion:ClientId` (normally `saturday-scout-player`)
- `Player:ApiOrigin`

The API receives only the public key PEM, issuer/audience/client ID, and key ID. Environment-variable names use `PlayerAssertions__PublicKeys__0__KeyId` and `PlayerAssertions__PublicKeys__0__PublicKeyPem`; add further indexed entries to overlap keys during rotation. The API project has a `UserSecretsId` for local Development configuration, so the public key and key ID may also be stored with `dotnet user-secrets set --project src/SaturdayScout.Api`. The Player private key must never be configured on the API or checked into source control. Use HTTPS for non-Development API origins. Keep the private key readable only by the Player service identity, and provision it through the hosting secret/file mechanism.

Example local key generation (run locally; do not commit either output):

```sh
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out player-api-assertion-private.pem
openssl pkey -in player-api-assertion-private.pem -pubout -out player-api-assertion-public.pem
```

The API supports multiple public keys. To rotate, deploy the new public key to every API instance while retaining the old key, switch the Player signer to the new private key and key ID, wait at least the maximum assertion lifetime plus clock skew, then remove the old API public key. Keep key IDs unique and do not reuse an ID for different key material.

## API scheme compatibility

Player API endpoints use the `SaturdayScout.ApiUser` policy scheme. It routes bearer assertions to `PlayerAssertion`, Development-only `X-Dev-Subject` requests to the existing Development handler, and Identity cookies to the existing Identity application scheme. Existing Development header behavior remains limited to Development. Administrator API policies continue to use the existing Identity cookie and MFA authorization requirements.

The scheme router is the extension point for a future native iOS OAuth/OIDC bearer scheme. That flow should validate its own issuer, audience, signature, and client policy, then map the authenticated subject to the same canonical Identity/domain user and reuse API authorization. Do not make the iOS app depend on Player cookies or the Player signing key.

## Client behavior

`IPlayerApiClient` is server-side and restricts requests to relative `/api/v1/` paths on the configured API origin. It maps 401, 403, validation errors, 409 conflicts, and transient 408/429/5xx responses to distinct outcomes. It does not automatically retry mutations; a caller must reload authoritative state before retrying an uncertain write or submission.

## Development behavior and limits

The API can start in Development without a public assertion key so the existing `X-Dev-Subject` workflow remains usable. In that configuration, signed assertions cannot validate until a test/local public key is configured. Outside Development, the API refuses startup without at least one valid public key, and the Player refuses startup unless its HTTPS API origin and private-key configuration are present and valid.

This milestone establishes the authentication boundary and client foundation. Player workflows, call sites, league discovery, and end-to-end mobile user journeys are outside Milestone 3.
