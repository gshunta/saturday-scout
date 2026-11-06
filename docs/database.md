# Local PostgreSQL and migrations

The database runs in PostgreSQL 17 with a named Docker volume. Compose binds only to localhost. Set a local password in your shell; do not put it in a tracked file. The current Saturday Scout development container is published on localhost port **55432** (port 5432 is used by another local PostgreSQL container). Set the same port in `SATURDAY_SCOUT_DB_PORT` and `SATURDAY_SCOUT_DB_CONNECTION`; verify the active mapping with `docker compose ps` if you change it.

```bash
export SATURDAY_SCOUT_DB_PASSWORD='choose-a-local-password'
export SATURDAY_SCOUT_DB_PORT=55432
export SATURDAY_SCOUT_DB_CONNECTION="Host=127.0.0.1;Port=${SATURDAY_SCOUT_DB_PORT};Database=saturday_scout;Username=saturday_scout;Password=${SATURDAY_SCOUT_DB_PASSWORD}"
docker compose up -d postgres
docker compose ps
dotnet tool restore
dotnet build -m:1 /nodeReuse:false
dotnet ef database update --no-build --project src/SaturdayScout.Infrastructure --startup-project src/SaturdayScout.Api
```

The design-time factory reads `SATURDAY_SCOUT_DB_CONNECTION`. It does not contain a fallback password. The application does not auto-create or auto-migrate the database. Apply migrations as an explicit deployment step.

To add a future schema migration:

```bash
dotnet build -m:1 /nodeReuse:false
dotnet ef migrations add MigrationName --no-build --project src/SaturdayScout.Infrastructure --startup-project src/SaturdayScout.Api --output-dir Persistence/Migrations
dotnet ef migrations script --no-build --idempotent --project src/SaturdayScout.Infrastructure --startup-project src/SaturdayScout.Api
```

The `InitialCreate` migration includes restrictive foreign keys, uniqueness for provider IDs and league/slate/sheet/pick identities, rank and display-order checks, and a final-score check. `PickSheet.Version` is an optimistic concurrency token; callers must update it through domain methods and handle `DbUpdateConcurrencyException` during saves. Domain rules such as matching a picked team to a game's two teams still require application validation.

For the disposable integration test, set an admin connection that can create and drop databases. The test makes a randomly named database, applies migrations twice, verifies constraints, and drops it in `finally`.

```bash
export SATURDAY_SCOUT_TEST_ADMIN_CONNECTION="Host=127.0.0.1;Port=${SATURDAY_SCOUT_DB_PORT};Database=postgres;Username=saturday_scout;Password=${SATURDAY_SCOUT_DB_PASSWORD}"
dotnet test -m:1 /nodeReuse:false
```

Without `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION`, the database integration test is reported as skipped.

## Azure production database

**Status: documentation only — no production resources have been provisioned yet.** This records the decision and the exact steps to stand up the database within a combined backend hosting budget of about $50/month; see [`deployment.md`](deployment.md) for the App Service hosting that connects to it. PostgreSQL was kept rather than switched to a cheaper engine (e.g. Azure SQL Basic tier, ~$5/month) because `AdminScoutStore`, `AccountProvisioningService`, and `ApiSecurity` already depend on Postgres-specific behavior — `xmin` optimistic concurrency, `SELECT ... FOR UPDATE` row locks, `clock_timestamp()`, `pg_advisory_xact_lock`, and `PostgresException`/`PostgresErrorCodes` mapping — that a different engine would require rewriting. The cheapest viable managed Postgres tier fits the budget without touching any of that code.

### Tier and cost

| Resource | Configuration | Approx. monthly cost |
|---|---|---|
| Azure Database for PostgreSQL Flexible Server | Burstable `Standard_B1ms` (1 vCPU / 2 GiB), single zone, no HA | ~$12 |
| Storage | 32 GiB | ~$4 |
| Backup | Locally redundant, 7-day retention (default; free up to 100% of provisioned storage) | $0 |
| App Service Plan | Basic `B1`, Linux, hosting both `SaturdayScout.Api` and `SaturdayScout.Admin` as separate sites on one plan | ~$13 |
| **Total** | | **~$29/month** |

Do not enable zone-redundant high availability (`--zonal-resiliency Enabled`) or geo-redundant backup (`--geo-redundant-backup Enabled`): HA bills a minimum of 8 vCores regardless of the configured tier, and geo-redundant backup doubles backup storage cost. Neither is needed for a friends-league app's availability requirements. Provisioning through the Azure portal instead of the commands below defaults to the General Purpose tier, which costs roughly 8–10x as much for this workload — use the CLI or explicitly select Burstable in the portal.

### Provision the server

```bash
export SATURDAY_SCOUT_AZ_RESOURCE_GROUP=saturday-scout-prod
export SATURDAY_SCOUT_AZ_LOCATION=eastus
export SATURDAY_SCOUT_DB_SERVER=saturday-scout-db
export SATURDAY_SCOUT_DB_PASSWORD='choose-a-strong-password'   # set in your shell only; never commit it

az group create --name "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --location "$SATURDAY_SCOUT_AZ_LOCATION"

az postgres flexible-server create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name "$SATURDAY_SCOUT_DB_SERVER" \
  --location "$SATURDAY_SCOUT_AZ_LOCATION" \
  --tier Burstable \
  --sku-name Standard_B1ms \
  --storage-size 32 \
  --version 17 \
  --backup-retention 7 \
  --geo-redundant-backup Disabled \
  --zonal-resiliency Disabled \
  --public-access Disabled \
  --admin-user saturday_scout_admin \
  --admin-password "$SATURDAY_SCOUT_DB_PASSWORD"

az postgres flexible-server db create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --server-name "$SATURDAY_SCOUT_DB_SERVER" \
  --database-name saturday_scout
```

`--version 17` matches the local development container; keep them in sync when either changes. `--public-access Disabled` leaves the server unreachable until firewall rules are added explicitly below, rather than opening it broadly.

App Service hosting for `SaturdayScout.Api` and `SaturdayScout.Admin` is documented separately in [`deployment.md`](deployment.md); the steps below assume that App Service exists.

### Allow the right traffic through the Postgres firewall

The server has no reachable inbound address until rules are added. Get the App Service's outbound IPs (see `deployment.md`) and add one firewall rule per IP, plus a temporary one for the machine applying migrations:

```bash
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api \
  --query outboundIpAddresses --output tsv
# Add one firewall rule per IP returned above:
az postgres flexible-server firewall-rule create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --server-name "$SATURDAY_SCOUT_DB_SERVER" \
  --name allow-api-outbound-1 --start-ip-address <ip> --end-ip-address <ip>

# Temporarily, to run migrations from an operator machine:
az postgres flexible-server firewall-rule create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --server-name "$SATURDAY_SCOUT_DB_SERVER" \
  --name allow-operator-temp --start-ip-address <your-public-ip> --end-ip-address <your-public-ip>
```

A Basic-tier App Service plan's outbound IPs are shared infrastructure and can change if the plan's tier changes; re-check `outboundIpAddresses` after any such change. Remove `allow-operator-temp` once migrations are applied — don't leave an operator workstation's IP open indefinitely. A VNet-integrated plan with a Postgres private endpoint removes this maintenance entirely but adds Private Link cost (~$7/month plus data processing); revisit it if the budget grows.

### Apply migrations to the production database

Follow the same backup-first discipline used for the Identity migration (see `identity-migration-review.md`): take a `pg_dump -Fc` backup, verify it with `pg_restore -l`, and restore it to a disposable database to confirm before trusting the production run. With the operator firewall rule above in place:

```bash
export SATURDAY_SCOUT_DB_CONNECTION="Host=${SATURDAY_SCOUT_DB_SERVER}.postgres.database.azure.com;Port=5432;Database=saturday_scout;Username=saturday_scout_admin;Password=${SATURDAY_SCOUT_DB_PASSWORD};Ssl Mode=Require"

dotnet build -m:1 /nodeReuse:false
dotnet ef database update --no-build --project src/SaturdayScout.Infrastructure --startup-project src/SaturdayScout.Api
dotnet ef database update --no-build --context ScoutIdentityDbContext \
  --project src/SaturdayScout.Infrastructure --startup-project src/SaturdayScout.Api
```

This is the same connection string set as the `SATURDAY_SCOUT_DB_CONNECTION` App Service setting in `deployment.md` — `Ssl Mode=Require` is mandatory, since Flexible Server rejects unencrypted connections. Never run migrations automatically at application startup, and never widen the Postgres firewall beyond the App Service outbound IPs and a temporary operator rule to do this.
