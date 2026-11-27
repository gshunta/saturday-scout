# Azure App Service deployment

**Status: documentation only — no production resources have been provisioned yet.** The intended deployment has three independent Linux App Services—`SaturdayScout.Player`, `SaturdayScout.Api`, and `SaturdayScout.Admin`—on one Basic `B1` Linux App Service Plan. Provision PostgreSQL first (`database.md`); database firewall configuration requires the plan's outbound addresses.

## Health and readiness endpoints

Player, API, and Admin expose anonymous `GET` and `HEAD` health routes. `GET /health/live` returns only `Healthy` while the process can serve HTTP; it does not contact PostgreSQL. `GET /health/ready` returns `Healthy` with HTTP 200 when the configured PostgreSQL database accepts a lightweight connectivity check, or `Unavailable` with HTTP 503 when it cannot. Both use `Cache-Control: no-store, no-cache`; responses contain no dependency details. Readiness uses the existing EF Core/Npgsql registration and connection pool, does not run migrations, and does not modify application data. The default database timeout is three seconds; `Health__DatabaseTimeoutSeconds` can set it from 1 through 10 seconds.

Configure the App Service Health Check path as `/health/ready` after deployment. Validate it over the deployed HTTPS hostname and confirm healthy status returns 200 and an intentionally unavailable database returns 503 without a redirect or diagnostic response body. Keep liveness separate from readiness: a PostgreSQL outage must not make a running process appear dead, which could provoke unnecessary restarts and would obscure whether the application itself is responsive. During a transient PostgreSQL outage, readiness will return 503 until connectivity recovers; App Service health-check thresholds and polling frequency should tolerate brief outages and should be selected deliberately during platform configuration. Read-only GET requests can be retried by clients where appropriate, but application writes must retain their existing no-automatic-replay behavior.

## Trusted reverse proxy settings

Player, API, and Admin use the shared `TrustedReverseProxy` configuration section. Forwarded headers are disabled by default, which supports direct HTTPS hosting and local Development without a proxy. Enable processing only after the hosting topology and trusted peer addresses/networks have been established:

```text
TrustedReverseProxy__Enabled=true
TrustedReverseProxy__ForwardLimit=1
TrustedReverseProxy__KnownProxies__0=<explicit-proxy-ip>
# Or configure explicitly trusted CIDR entries:
TrustedReverseProxy__KnownNetworks__0=<explicit-proxy-cidr>
```

Set these settings consistently on each hosted app that receives traffic through that proxy. `ForwardLimit` is bounded to 1–5 and defaults to one hop. Enabling forwarding without at least one valid proxy or network fails application startup. Invalid IP/CIDR values also fail startup. Do not use wildcard trust or enable forwarding before confirming the actual platform path. Direct HTTPS hosting should leave `TrustedReverseProxy__Enabled` unset or set it to `false`.

Before enabling this in Azure App Service, discover and validate the **inbound** proxy peer address/network and forwarded-header chain from a staging deployment. Confirm how many trusted hops append `X-Forwarded-For` and `X-Forwarded-Proto`, and test the chosen limit with requests through that path. App Service outbound IPs used for PostgreSQL firewall rules do not establish the inbound proxy trust list.

## Player sign-in rate limiting

`SaturdayScout.Player` rate-limits `POST /auth/login`, its only anonymous credential-handling endpoint; Player has no invitation-acceptance, registration, password-reset, or MFA endpoints today, and any added later must apply the same `player-login` policy (a test fails if an anonymous `/auth/*` POST lacks it). The limit uses ASP.NET Core's built-in rate limiter:

| Setting | Default | Allowed range |
|---|---|---|
| `PlayerRateLimiting__Login__PermitLimit` | `10` attempts | 1–100 |
| `PlayerRateLimiting__Login__WindowSeconds` | `300` (5 minutes) | 1–3600 |

The algorithm is a sliding window with five segments and no request queue, so a burst cannot double the limit at a window boundary and excess attempts are rejected immediately. Every submission counts, successful or not, so the limiter gives no signal about whether an account exists. Values outside the allowed range fail startup. There is no setting to disable the limiter.

Limits are partitioned by `HttpContext.Connection.RemoteIpAddress`: the full address for IPv4 (IPv4-mapped IPv6 addresses are normalized to IPv4), and the `/64` prefix for IPv6, which stops a single subscriber from rotating addresses within their own allocation. The limiter never reads `X-Forwarded-For` itself. When the `TrustedReverseProxy` settings above are enabled and correct, `RemoteIpAddress` is the real client address. If they are missing or wrong behind App Service, every client shares the front end's address and one partition; that is safe (it fails closed) but lets anyone delay sign-in for all players. Validate proxy trust before beta. The limiter runs after forwarded-header processing and before authentication, antiforgery validation, and the login handler, so a rejected request never touches Identity.

A rejected request returns HTTP 429 with `Cache-Control: no-store` and the plain-text body `Too many sign-in attempts. Wait a few minutes and try again.` There is no `Retry-After` header, because the sliding-window limiter does not report one. The React app already shows its generic sign-in error for any non-success response. The Blazor `/login` form posts natively, so a throttled user sees that plain-text page and uses the browser's back button.

**Interaction with Identity lockout.** Identity lockout is unchanged: five failed passwords lock that account for 15 minutes, independent of IP. The IP limit (10 per 5 minutes by default) is deliberately higher than the lockout threshold. One person mistyping a password hits account lockout first, while password spraying across many accounts from one origin hits the IP limit. Attempts rejected with 429 do not count toward lockout. Ten attempts per five minutes leaves headroom for several beta players signing in from one shared household or office address.

Admin separately limits login, MFA, recovery, and enrollment to 5 requests per minute per client partition. Its partition manager has a fixed pool of 1,024 tracked-client keys and 256 overflow keys. It expires idle address mappings after two minutes and reuses a key only after that interval; this exceeds the one-minute fixed limiter window so a returning address does not inherit an active counter. Excess addresses are spread over the bounded overflow buckets. As with Player, correct trusted-proxy configuration is required for distinct client IPs behind App Service. Both limiters are in-memory and process-local; restarts reset counters.

**Not throttled:** `/health/live`, `/health/ready`, `/auth/antiforgery`, `/auth/session`, `/auth/logout` (authenticated and antiforgery-protected; throttling it could stop a player on a shared address from signing out), `/bff/*`, pages, and static or React assets.

**Scaling limitation.** Counters are in memory and per process. Player tracks at most 1,024 active client-address mappings; mappings unused for the configured sliding window plus one minute are reclaimed. If all mappings are active, additional addresses are distributed over 256 bounded overflow buckets, rather than one global bucket that an attacker could exhaust for every new client. Clients that collide in an overflow bucket share its limit, so sustained address churn can still cause temporary collisions. Admin uses a fixed pool of bounded limiter keys as described above. With more than one App Service instance, or after a restart, each instance keeps its own counters, so the effective limit is `PermitLimit × instance count` and counters reset on restart. That is acceptable for a single-instance beta. Scaling out would need a shared store, which is out of scope here. Rejections are recorded in the built-in `aspnetcore.rate_limiting.requests` metric; the application writes no rate-limit log entries, so no client addresses or credentials are logged.

Reuse the same resource group and location as the database:

```bash
export SATURDAY_SCOUT_AZ_RESOURCE_GROUP=saturday-scout-prod
export SATURDAY_SCOUT_AZ_LOCATION=centralus
```

## Create the plan and three apps

App Service bills the plan rather than each app. Set Production explicitly on every web app. For `WebApplication.CreateBuilder`, `DOTNET_ENVIRONMENT` takes precedence over `ASPNETCORE_ENVIRONMENT`; do not set a conflicting `DOTNET_ENVIRONMENT`. An App Service host marker combined with Development or Testing now makes the application fail startup.

```bash
az appservice plan create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-plan \
  --sku B1 --is-linux

az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-api --runtime "DOTNETCORE:10.0"
az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-admin --runtime "DOTNETCORE:10.0"
az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-player --runtime "DOTNETCORE:10.0"

for app in saturday-scout-player saturday-scout-api saturday-scout-admin; do
  az webapp config set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name "$app" --always-on true
done
```

Run `az webapp list-runtimes --os linux` first and confirm a `DOTNETCORE:10.0` entry exists. If the built-in stack is unavailable, a container deployment needs separate images/entrypoints for the three hosts. `Always On` is recommended for all three on the B1 plan to reduce idle shutdowns; it does not guarantee a sub-15-second cold start after platform maintenance or a restart.

For each app, configure **HTTPS Only**, minimum inbound TLS 1.2 or higher for both the app and SCM/Kudu site, disable FTP/FTPS when unused, disable remote debugging, and disable SCM basic authentication when the approved CI pipeline uses federated deployment. These are App Service platform controls; `UseHttpsRedirection` and HSTS in application code do not replace them. See [App Service security guidance](https://learn.microsoft.com/en-us/azure/app-service/app-service-security-readme) and [minimum TLS settings](https://learn.microsoft.com/en-us/azure/app-service/tls-minimum-version).

## Hand off the outbound IPs to the database firewall

The Postgres server rejects all traffic until explicit firewall rules admit these apps. Capture the IPs here and add the rules in `database.md`:

```bash
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api \
  --query outboundIpAddresses --output tsv
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --query outboundIpAddresses --output tsv
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-player \
  --query outboundIpAddresses --output tsv
```

Follow [`database.md`](database.md#allow-the-right-traffic-through-the-postgres-firewall) to add one `az postgres flexible-server firewall-rule create` per IP returned above. Outbound IPs on a Basic-tier plan are shared infrastructure and can change if the plan's tier changes — re-run this and update the firewall rules after any such change.

## Configure Production settings

The code requires a database connection, Azure Data Protection settings, explicit `AllowedHosts`, and the Player/API assertion contract. Use Key Vault references for secret values. A Key Vault reference provides an app-setting value to the process; it does **not** create a file. Grant each App Service identity only the required secret access. See [Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references).

Store the complete PostgreSQL connection string as a Key Vault secret and reference it as `SATURDAY_SCOUT_DB_CONNECTION` in each app. Grant each system-assigned identity `Key Vault Secrets User` at the secret scope (or the narrowest supported scope). It must use `Ssl Mode=VerifyFull`; this is enforced at Production startup. `VerifyFull` encrypts traffic and validates the server certificate and hostname. The prior `Require` setting encrypted without validating server identity and is no longer acceptable. Maintain trusted Azure PostgreSQL root certificates; do not set `Trust Server Certificate=true`. Azure's current guidance recommends `verify-full` where the connection hostname supports it. [Azure PostgreSQL TLS guidance](https://learn.microsoft.com/azure/postgresql/security/security-tls-how-to-connect), [Npgsql SSL modes](https://www.npgsql.org/doc/security?tabs=tabid-1).

Configure the host allowlists to the actual App Service hostnames plus the chosen custom domains. Replace the example domains before deployment; do not leave `*`:

| App | `AllowedHosts` value template |
|---|---|
| Player | `saturday-scout-player.azurewebsites.net;player.example.org` |
| API | `saturday-scout-api.azurewebsites.net;api.example.org` |
| Admin | `saturday-scout-admin.azurewebsites.net;admin.example.org` |

The Player host requires these app settings:

```text
ASPNETCORE_ENVIRONMENT=Production
AllowedHosts=saturday-scout-player.azurewebsites.net;player.example.org
Player__ApiOrigin=https://saturday-scout-api.azurewebsites.net
ApiAssertion__Issuer=SaturdayScout.Player
ApiAssertion__Audience=SaturdayScout.Api
ApiAssertion__ClientId=saturday-scout-player
ApiAssertion__KeyId=<active-key-id>
ApiAssertion__PrivateKeyPem=@Microsoft.KeyVault(SecretUri=https://<vault-name>.vault.azure.net/secrets/saturday-scout-player-api-rsa/<secret-version>)
ApiAssertion__TokenLifetimeSeconds=120
```

The API requires matching `PlayerAssertions__Issuer`, `PlayerAssertions__Audience`, and `PlayerAssertions__AuthorizedClientId`, plus one or more `PlayerAssertions__PublicKeys__N__KeyId` and `PlayerAssertions__PublicKeys__N__PublicKeyPem` entries. `KeyId` must match the Player signing key ID; the public key must correspond to the Player private key. Public keys can overlap during rotation. The Player signing PEM is private and must be retrievable only by the Player identity. Do not put it in source control, container images, publish output, or deployment artifacts.

Production receives the private PEM through the `ApiAssertion__PrivateKeyPem` App Service setting, whose value is a version-pinned Key Vault reference. App Service resolves the reference into process configuration; it does not create a file. The Player host imports the multiline PEM directly into its in-process RSA signer and fails startup if the reference remains unresolved or the key is invalid. `PrivateKeyPath` remains the local Development/Testing source. Do not set both sources in Production. The private secret is readable only by Player's system-assigned managed identity; API and Admin must not receive access to it. See [App Service Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references).

### Provision the Player assertion signing secret

Run these commands from a trusted operator machine. They generate a 3072-bit RSA key in a private temporary directory, upload the private PEM as a Key Vault secret, and print only the secret resource ID. They do not put key material into shell history, source control, an image, or published output. Keep the local file protected until upload verification, then remove it according to the operator workstation's secure-file policy.

```bash
umask 077
key_dir="$(mktemp -d "${TMPDIR:-/tmp}/saturday-scout-player-key.XXXXXX")"
key_file="$key_dir/player-private.pem"
public_file="$key_dir/player-public.pem"
trap 'rm -f "$key_file" "$public_file"; rmdir "$key_dir" 2>/dev/null || true' EXIT

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$key_file"
openssl pkey -in "$key_file" -pubout -out "$public_file"

PLAYER_PRINCIPAL_ID="$(az webapp identity assign \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-player \
  --query principalId --output tsv)"
PLAYER_APP_ID="$(az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name saturday-scout-player --query id --output tsv)"
VAULT_ID="$(az keyvault show --name "$SATURDAY_SCOUT_KEY_VAULT" \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --query id --output tsv)"

SECRET_ID="$(az keyvault secret set --vault-name "$SATURDAY_SCOUT_KEY_VAULT" \
  --name saturday-scout-player-api-rsa --file "$key_file" --query id --output tsv)"
SECRET_SCOPE="$VAULT_ID/secrets/saturday-scout-player-api-rsa"
az role assignment create --assignee-object-id "$PLAYER_PRINCIPAL_ID" \
  --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" \
  --scope "$SECRET_SCOPE" --output none

SECRET_VERSION="${SECRET_ID##*/}"
SECRET_URI="https://${SATURDAY_SCOUT_KEY_VAULT}.vault.azure.net/secrets/saturday-scout-player-api-rsa/$SECRET_VERSION"
KEY_ID="player-rsa-$SECRET_VERSION"
PUBLIC_PEM="$(cat "$public_file")"

# First-time API key setup. For rotation, add the new public key at another index and keep
# the old key at index 0 until the overlap period has completed.
az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name saturday-scout-api --settings \
  PlayerAssertions__PublicKeys__0__KeyId="$KEY_ID" \
  PlayerAssertions__PublicKeys__0__PublicKeyPem="$PUBLIC_PEM" --output none

# App Service Key Vault references use the system-assigned identity by default.
az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name saturday-scout-player --settings \
  ApiAssertion__PrivateKeyPem="@Microsoft.KeyVault(SecretUri=$SECRET_URI)" \
  ApiAssertion__KeyId="$KEY_ID" --output none
az webapp restart --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name saturday-scout-player
```

`Key Vault Secrets User` is assigned at the individual secret scope; do not grant it to API or Admin identities. This uses Azure RBAC. Secret-level assignments are supported, though Microsoft recommends vault-level assignments for typical deployments; this one-secret access boundary is intentional here. The Player identity still receives only the separate Storage and Key Vault cryptographic permissions required for its Data Protection ring. [Key Vault RBAC guidance](https://learn.microsoft.com/azure/key-vault/general/rbac-guide).

The reference must resolve before the Player host starts. Startup validation recognizes the unresolved `@Microsoft.KeyVault(...)` marker and rejects it without logging the value. Confirm reference health in App Service configuration diagnostics and then perform the BFF assertion smoke test below. Never query or print the secret value for verification.

### Signing-key rotation and rollback

Keep the Key Vault reference version-pinned so the PEM and `ApiAssertion__KeyId` move together in a controlled App Service configuration change. Do not rely on versionless automatic refresh for signing-key rotation: App Service caches references and may take up to 24 hours to refresh them. An app configuration update restarts the app and refetches references; App Service also supports an explicit refresh operation. [Key Vault reference refresh behavior](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references#understand-rotation).

1. Generate and upload a new RSA 3072-bit PEM as a new Key Vault secret version. Record its versioned URI and new key ID without retrieving the secret value.
2. Add the new public key to API configuration while retaining the old `PlayerAssertions__PublicKeys` entry. For example, when old is at index 0:

   ```bash
   az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
     --name saturday-scout-api --settings \
     PlayerAssertions__PublicKeys__0__KeyId="$OLD_KEY_ID" \
     PlayerAssertions__PublicKeys__0__PublicKeyPem="$OLD_PUBLIC_PEM" \
     PlayerAssertions__PublicKeys__1__KeyId="$NEW_KEY_ID" \
     PlayerAssertions__PublicKeys__1__PublicKeyPem="$NEW_PUBLIC_PEM" --output none
   ```

   Confirm the API host is healthy. Allow for Azure RBAC assignment propagation before configuring the reference.
3. Update Player's version-pinned `ApiAssertion__PrivateKeyPem` reference and `ApiAssertion__KeyId` together, then restart every Player instance. Confirm all instances use the new configuration and verify `GET /bff/api/v1/leagues/me` returns 200. The assertion must not appear in browser responses or logs.
4. Retain the old API public key until every old Player instance has stopped issuing assertions, then wait at least the maximum configured Player assertion lifetime (currently at most 180 seconds) plus API clock skew (20 seconds) and measured clock synchronization error. Include the full rollout/restart duration before starting that wait. Add operational margin if instance turnover cannot be established precisely.
5. Remove the old API public key only after that interval and a successful smoke test. Retain the old Key Vault secret version for rollback according to key-retention policy.

For rollback, restore the previous version-pinned secret reference and matching key ID, restart all Player instances, and keep both API public keys trusted until new-key assertions have expired using the same lifetime-plus-clock-skew calculation. To force App Service to resolve references again, use its authenticated management refresh endpoint:

```bash
az rest --method post \
  --url "https://management.azure.com${PLAYER_APP_ID}/config/configreferences/appsettings/refresh?api-version=2022-03-01"
```

Follow the refresh with a restart when needed so every Player instance reloads the resolved value.

Set the following Data Protection settings per the existing Stage 4 design. Player gets its own ring and key; Admin and API share theirs. The `SATURDAY_SCOUT_DB_CONNECTION` value below is a Key Vault reference to a secret containing the complete VerifyFull connection string, not the string itself:

```bash
DB_CONNECTION_REFERENCE='@Microsoft.KeyVault(SecretUri=https://<vault-name>.vault.azure.net/secrets/saturday-scout-db-connection/)'

az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-player --settings \
  ASPNETCORE_ENVIRONMENT=Production \
  AllowedHosts='saturday-scout-player.azurewebsites.net;player.example.org' \
  SATURDAY_SCOUT_DB_CONNECTION="$DB_CONNECTION_REFERENCE" \
  SATURDAY_SCOUT_DP_PROVIDER=AzureBlob \
  SATURDAY_SCOUT_DP_KEY_BLOB_URI="$SATURDAY_SCOUT_PLAYER_DP_KEY_BLOB_URI" \
  SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID="$SATURDAY_SCOUT_PLAYER_DP_KEY_VAULT_ID" \
  SATURDAY_SCOUT_DP_APPLICATION_NAME=SaturdayScout.Player.v1 \
  Player__ApiOrigin=https://saturday-scout-api.azurewebsites.net \
  ApiAssertion__Issuer=SaturdayScout.Player \
  ApiAssertion__Audience=SaturdayScout.Api \
  ApiAssertion__ClientId=saturday-scout-player \
  ApiAssertion__KeyId='<active-key-id>' \
  ApiAssertion__PrivateKeyPem='@Microsoft.KeyVault(SecretUri=https://<vault-name>.vault.azure.net/secrets/saturday-scout-player-api-rsa/<secret-version>)' \
  ApiAssertion__TokenLifetimeSeconds=120

for app in saturday-scout-api saturday-scout-admin; do
  host="$app.azurewebsites.net"
  if [ "$app" = saturday-scout-api ]; then host="$host;api.example.org"; else host="$host;admin.example.org"; fi
  az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name "$app" --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    AllowedHosts="$host" \
    SATURDAY_SCOUT_DB_CONNECTION="$DB_CONNECTION_REFERENCE" \
    SATURDAY_SCOUT_DP_PROVIDER=AzureBlob \
    SATURDAY_SCOUT_DP_KEY_BLOB_URI="$SATURDAY_SCOUT_SHARED_DP_KEY_BLOB_URI" \
    SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID="$SATURDAY_SCOUT_SHARED_DP_KEY_VAULT_ID" \
    SATURDAY_SCOUT_DP_APPLICATION_NAME=SaturdayScout.SharedIdentity.v1
done

az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api --settings \
  PlayerAssertions__Issuer=SaturdayScout.Player \
  PlayerAssertions__Audience=SaturdayScout.Api \
  PlayerAssertions__AuthorizedClientId=saturday-scout-player \
  PlayerAssertions__MaximumLifetimeSeconds=180 \
  PlayerAssertions__PublicKeys__0__KeyId='<active-key-id>' \
  PlayerAssertions__PublicKeys__0__PublicKeyPem='<public-RSA-PEM>'

az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --settings SATURDAY_SCOUT_PUBLIC_ORIGIN=https://admin.example.org
```

Replace every example hostname, key ID, versioned secret URI, and Key Vault/Blob identifier with the actual provisioned values before execution. These commands use no database password or private signing-key value in shell history; the database credential and private PEM are contained in Key Vault secrets. The public assertion PEM is not private material. Admin requires `SATURDAY_SCOUT_PUBLIC_ORIGIN` to be an HTTPS origin, per [Admin Program.cs](../src/SaturdayScout.Admin/Program.cs). This setting currently validates startup configuration; it is not an authentication callback URL.

Set the shell variables `SATURDAY_SCOUT_SHARED_DP_KEY_BLOB_URI`, `SATURDAY_SCOUT_SHARED_DP_KEY_VAULT_ID`, `SATURDAY_SCOUT_PLAYER_DP_KEY_BLOB_URI`, and `SATURDAY_SCOUT_PLAYER_DP_KEY_VAULT_ID` to the provisioned resource values before applying settings. They are configuration values, not credentials. Do not put account keys, SAS tokens, or client secrets in them.

### Durable Data Protection key rings

Production requires `SATURDAY_SCOUT_DP_PROVIDER=AzureBlob` and all three settings below. There is no filesystem or ephemeral fallback in hosted environments; the `Testing` environment is reserved for automated integration hosts with an explicit local key path. The Player app must use its dedicated ring (`SaturdayScout.Player.v1`, Player-specific Blob URI and Key Vault key); API and Admin must use the same shared ring (`SaturdayScout.SharedIdentity.v1`, identical Blob URI and Key Vault key). Keep the Player ring inaccessible to API/Admin identities and vice versa.

| Setting | Player | API and Admin |
|---|---|---|
| `SATURDAY_SCOUT_DP_PROVIDER` | `AzureBlob` | `AzureBlob` |
| `SATURDAY_SCOUT_DP_KEY_BLOB_URI` | Player ring blob URI | Same shared ring blob URI in both apps |
| `SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID` | Player versionless key ID | Same shared versionless key ID in both apps |
| `SATURDAY_SCOUT_DP_APPLICATION_NAME` | `SaturdayScout.Player.v1` | `SaturdayScout.SharedIdentity.v1` |

Assign a system-managed identity to each App Service. Grant each identity `Storage Blob Data Contributor` only on its ring's container and `Key Vault Crypto User` only on its wrapping key. Configure the vault to use the Azure RBAC permission model; `Key Vault Crypto User` does not work with legacy access policies. Key-level role assignments are supported; the shared Admin/API wrapping key is intentionally a key shared by those two applications. The Player cookie is not consumed by the API; the Admin Identity application cookie must remain compatible between API and Admin. No client secret or storage account key belongs in app settings.

The URI settings are configuration values, not credentials. The Blob URI must be HTTPS, identify a single container and blob, and contain no SAS query. The Key Vault identifier must be HTTPS and versionless (`.../keys/<key-name>`). Retain previous Key Vault key versions: existing Data Protection XML keys refer to the key version that encrypted them, and deleting or disabling that version makes existing cookies unreadable.

The previous `/home/dpkeys` instructions were incorrect for sharing between separate App Service apps. App Service `/home` is persistent for an app and shared by that app's scaled instances, but the content share is dedicated to each app. Giving two apps the same `/home` path does not give them the same key ring. `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true` is still appropriate for Linux custom containers, but it does not create cross-app sharing. See [App Service filesystem behavior](https://learn.microsoft.com/azure/app-service/operating-system-functionality).

Local Development defaults to filesystem storage and does not contact Azure or require Azure credentials. Set `SATURDAY_SCOUT_DP_KEYS_PATH` to an absolute directory when a specific location is needed; use one shared directory for API and Admin, and a separate directory for Player. To explicitly exercise Azure locally, set `SATURDAY_SCOUT_DP_PROVIDER=AzureBlob` and authenticate through the Azure CLI or developer tooling; all Blob and Key Vault settings are then mandatory.

### Data Protection operational validation

Before enabling public traffic, verify each managed identity's scoped access, start each app, protect/unprotect a test payload across service-provider recreation, and verify that Admin can validate its cookie through the API while Player uses only its own ring. Restart and redeploy each host and repeat the checks. Test a missing Blob permission and a disabled/missing Key Vault key version in a non-production environment; the app must fail the operation/readiness path without creating another key ring. Do not include key material, full URIs, Azure exception details, or credentials in health responses. Liveness remains independent of Blob and Key Vault. Readiness performs no Key Vault operation per request.

## Deployment smoke tests

1. Confirm the startup log reports `Production` for all three apps. Confirm no Development authentication, QA provisioning, or ambiguous-write simulation settings are present.
2. Call `GET /health/live` and `GET /health/ready` on each HTTPS hostname; expect 200 with PostgreSQL reachable. In a non-production slot, verify a controlled database outage produces only 503/`Unavailable` from readiness while liveness stays 200.
3. Sign in to Admin, complete TOTP MFA, and load a protected page. A password-only session must remain denied.
4. Sign in to Player. Call `/auth/session` and then `GET /bff/api/v1/leagues/me`; expect 200. This exercises the deployed Player signer, API public-key selection, issuer, audience, authorized client, lifetime checks, and domain-user mapping without returning the assertion to browser JavaScript. Confirm no bearer assertion is present in browser responses or storage.
5. Verify Player and API use the same active key ID and corresponding RSA pair. During rotation, configure the new API public key before switching Player, keep both API public keys during overlap, restart Player to load its new signing key, exercise the BFF request, then remove the old API key only after all old assertions have expired.
6. Verify `/auth/login` over HTTP redirects to HTTPS, cookies are Secure/HttpOnly/SameSite=Lax and host-only, and `/react/` plus `/bff` remain same-origin. Confirm no cross-origin API access or permissive CORS policy was added.
7. From a staging deployment, validate the trusted proxy peer and forwarded-header chain before enabling `TrustedReverseProxy`. Confirm HTTPS redirection does not loop and distinct client IPs partition authentication limits correctly.

The current Admin UI uses its existing application services and database access; it does not call the separate API host. Sharing the Admin/API Data Protection ring preserves cryptographic compatibility, but does not make a host-only Admin cookie automatically accompany a request to the API hostname. Do not test that boundary by copying a production browser cookie between hosts.

## Deploy the code

No CI pipeline exists yet (a gap already flagged in the architecture review — land one before relying on this manually). Until then, publish and zip-deploy each app from a trusted machine:

```bash
dotnet publish src/SaturdayScout.Api -c Release -o ./publish/api
(cd ./publish/api && zip -r ../api.zip .)
az webapp deploy --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api \
  --src-path ./publish/api.zip --type zip

dotnet publish src/SaturdayScout.Admin -c Release -o ./publish/admin
(cd ./publish/admin && zip -r ../admin.zip .)
az webapp deploy --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --src-path ./publish/admin.zip --type zip

npm --prefix src/SaturdayScout.Player.Web ci
npm --prefix src/SaturdayScout.Player.Web run build
dotnet publish src/SaturdayScout.Player -c Release -o ./publish/player
(cd ./publish/player && zip -r ../player.zip .)
az webapp deploy --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-player \
  --src-path ./publish/player.zip --type zip
```

Apply pending migrations (`database.md`) before deploying code that depends on them, and never let the application apply migrations itself at startup. After deployment, confirm all hosts start cleanly with `az webapp log tail`. Outside Development and Testing, startup requires the database connection with `Ssl Mode=VerifyFull`, valid Azure Data Protection provider settings and ring name, and explicit `AllowedHosts`; Admin also requires `SATURDAY_SCOUT_PUBLIC_ORIGIN`. Player requires the configured API origin and a resolved, valid RSA private PEM from its Key Vault reference; API requires at least one valid public key. Azure identities, RBAC assignments, Key Vault references, and the signing key must be provisioned before deployment.
