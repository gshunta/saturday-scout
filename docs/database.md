# Local PostgreSQL and migrations

The database runs in PostgreSQL 17 with a named Docker volume. Compose binds only to localhost. Set a local password in your shell; do not put it in a tracked file. Port 5432 is the default; change `SATURDAY_SCOUT_DB_PORT` if it is occupied.

```bash
export SATURDAY_SCOUT_DB_PASSWORD='choose-a-local-password'
export SATURDAY_SCOUT_DB_PORT=5432
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
