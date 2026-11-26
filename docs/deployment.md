# Azure App Service deployment

**Status: documentation only — no production resources have been provisioned yet.** This covers hosting `SaturdayScout.Api` and `SaturdayScout.Admin` on a single Basic `B1` Linux App Service Plan, the compute half of the ~$29/month total documented in [`database.md`](database.md#tier-and-cost). Provision the database first (`database.md`); the steps here need its server name to build a connection string, and the database's firewall rules need this App Service's outbound IPs.

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

**Not throttled:** `/health/live`, `/health/ready`, `/auth/antiforgery`, `/auth/session`, `/auth/logout` (authenticated and antiforgery-protected; throttling it could stop a player on a shared address from signing out), `/bff/*`, pages, and static or React assets.

**Scaling limitation.** Counters are in memory and per process. Player tracks at most 1,024 active client-address mappings; mappings unused for the configured sliding window plus one minute are reclaimed. If all mappings are active, additional addresses are distributed over 256 bounded overflow buckets, rather than one global bucket that an attacker could exhaust for every new client. Clients that collide in an overflow bucket share its limit, so sustained address churn can still cause temporary collisions. ASP.NET Core also removes idle partition limiter instances after their limiter state has replenished and remained idle; this bounds timer and limiter retention independently of the address map. With more than one App Service instance, or after a restart, each instance keeps its own counters, so the effective limit is `PermitLimit × instance count` and counters reset on restart. That is acceptable for a single-instance beta. Scaling out would need a shared store, which is out of scope here. Rejections are recorded in the built-in `aspnetcore.rate_limiting.requests` metric; the application writes no rate-limit log entries, so no client addresses or credentials are logged.

Reuse the same resource group and location as the database:

```bash
export SATURDAY_SCOUT_AZ_RESOURCE_GROUP=saturday-scout-prod
export SATURDAY_SCOUT_AZ_LOCATION=eastus
```

## Create the plan and the two apps

One Basic plan hosts both sites — App Service bills the plan, not each app on it:

```bash
az appservice plan create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-plan \
  --sku B1 --is-linux

az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-api --runtime "DOTNETCORE:10.0"
az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-admin --runtime "DOTNETCORE:10.0"

az webapp config set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api --always-on true
az webapp config set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin --always-on true
```

Run `az webapp list-runtimes --os linux` first and confirm a `DOTNETCORE:10.0` entry exists. If the built-in stack isn't published yet, deploy both apps as Linux containers from a Dockerfile instead — same plan, same cost, no dependency on when Azure publishes the stack. `--always-on true` is required on both apps: without it, App Service idles the process after 20 minutes with no requests, which would silently break the Admin portal's background MFA/session revalidation and any scheduled work added later.

## Hand off the outbound IPs to the database firewall

The Postgres server rejects all traffic until explicit firewall rules admit these apps. Capture the IPs here and add the rules in `database.md`:

```bash
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api \
  --query outboundIpAddresses --output tsv
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --query outboundIpAddresses --output tsv
```

Follow [`database.md`](database.md#allow-the-right-traffic-through-the-postgres-firewall) to add one `az postgres flexible-server firewall-rule create` per IP returned above. Outbound IPs on a Basic-tier plan are shared infrastructure and can change if the plan's tier changes — re-run this and update the firewall rules after any such change.

## Configure the apps

Never commit any of these values. Set them directly as App Service settings, using the server name and password chosen in `database.md`:

```bash
export SATURDAY_SCOUT_DB_SERVER=saturday-scout-db                        # must match database.md
export SATURDAY_SCOUT_DB_PASSWORD='the-password-chosen-in-database.md'   # set in your shell only; never commit it
export SATURDAY_SCOUT_DB_CONNECTION="Host=${SATURDAY_SCOUT_DB_SERVER}.postgres.database.azure.com;Port=5432;Database=saturday_scout;Username=saturday_scout_admin;Password=${SATURDAY_SCOUT_DB_PASSWORD};Ssl Mode=Require"

for app in saturday-scout-api saturday-scout-admin; do
  az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name "$app" --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    WEBSITES_ENABLE_APP_SERVICE_STORAGE=true \
    SATURDAY_SCOUT_DB_CONNECTION="$SATURDAY_SCOUT_DB_CONNECTION" \
    SATURDAY_SCOUT_DP_PROVIDER=AzureBlob \
    SATURDAY_SCOUT_DP_KEY_BLOB_URI="$SATURDAY_SCOUT_SHARED_DP_KEY_BLOB_URI" \
    SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID="$SATURDAY_SCOUT_SHARED_DP_KEY_VAULT_ID" \
    SATURDAY_SCOUT_DP_APPLICATION_NAME=SaturdayScout.SharedIdentity.v1
done
az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name "$SATURDAY_SCOUT_PLAYER_APP_NAME" --settings \
  ASPNETCORE_ENVIRONMENT=Production \
  WEBSITES_ENABLE_APP_SERVICE_STORAGE=true \
  SATURDAY_SCOUT_DB_CONNECTION="$SATURDAY_SCOUT_DB_CONNECTION" \
  SATURDAY_SCOUT_DP_PROVIDER=AzureBlob \
  SATURDAY_SCOUT_DP_KEY_BLOB_URI="$SATURDAY_SCOUT_PLAYER_DP_KEY_BLOB_URI" \
  SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID="$SATURDAY_SCOUT_PLAYER_DP_KEY_VAULT_ID" \
  SATURDAY_SCOUT_DP_APPLICATION_NAME=SaturdayScout.Player.v1
az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --settings SATURDAY_SCOUT_PUBLIC_ORIGIN=https://your-chosen-domain
```

`Ssl Mode=Require` is mandatory — Flexible Server rejects unencrypted connections. `saturday-scout-admin` additionally requires `SATURDAY_SCOUT_PUBLIC_ORIGIN` as an HTTPS origin, per the startup check in `src/SaturdayScout.Admin/Program.cs`; set it once a custom domain and managed certificate are attached to that app (`az webapp config hostname add` / `az webapp config ssl create --certificate-source Managed`), which isn't covered here.

Set the shell variables `SATURDAY_SCOUT_SHARED_DP_KEY_BLOB_URI`, `SATURDAY_SCOUT_SHARED_DP_KEY_VAULT_ID`, `SATURDAY_SCOUT_PLAYER_APP_NAME`, `SATURDAY_SCOUT_PLAYER_DP_KEY_BLOB_URI`, and `SATURDAY_SCOUT_PLAYER_DP_KEY_VAULT_ID` to the provisioned resource values before running these commands. They are placeholders for deployment configuration, not application credentials. Do not place account keys, SAS tokens, or client secrets in these values.

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
```

Apply pending migrations (`database.md`) before deploying code that depends on them, and never let the application apply migrations itself at startup. After deployment, confirm all hosts start cleanly with `az webapp log tail`. Outside Development, startup requires the database connection, valid Azure Data Protection provider settings, and each host's correct application name; Admin also requires `SATURDAY_SCOUT_PUBLIC_ORIGIN`.
