# Saturday Scout Developer Guide

## Provision a development test Player

Use the existing development-only Identity provisioning command through the
wrapper below. If `SATURDAY_SCOUT_DB_CONNECTION` is already set, the script uses
it without prompting and refuses anything other than
`127.0.0.1:55432` / `saturday_scout`. Otherwise, run it from an interactive
terminal: it prompts for the PostgreSQL username (default `postgres`) and then
for the PostgreSQL password with terminal echo disabled. The generated Npgsql
connection string is passed only to the provisioning child process; it is not
written to a file or exported into the calling shell. Do not put database
credentials in this repository or shell history.

```bash
./scripts/dev/create-test-player.sh manual-player3@example.test
```

The script prompts for a display name if `--display-name` is omitted. It then
delegates to `SaturdayScout.Admin provision-dev-player`, which separately
prompts for a new Player password and confirmation without echoing them. The
PostgreSQL password and Player password are distinct credentials. The script explicitly
sets `ASPNETCORE_ENVIRONMENT=Development` and
`SATURDAY_SCOUT_DEV_PLAYER_APPROVED=yes` only for the provisioning child process.

For an interactive run, start the script without an email:

```bash
./scripts/dev/create-test-player.sh
```

Options:

- `--display-name "Manual Player Three"` — provide a display name instead of
  prompting.
- `--league-id GUID` — choose an existing development league. The default is
  `37770620-bb3b-4efd-b149-3a4e6737a26b`.
- `--help` — show usage.

The provisioning service is idempotent for a fully linked, confirmed Player
account with the expected league membership. It reports a valid existing account
without changing the password. Incomplete or inconsistent Identity/domain
records are rejected for operator review; the script does not repair them. It
does not create picks or change existing players' picks.

Run the script-level safety tests with:

```bash
bash tests/scripts/dev/test-create-test-player.sh
```

### Provision the development QA league

The Development-only `SaturdayScout.Admin provision-dev-qa-league` command
creates the fixed `Saturday Scout QA` league using an existing confirmed
SystemAdministrator as commissioner, then adds an existing confirmed Player as
a regular league member. It uses the existing league and membership application
services, creates no account, and refuses a league with a conflicting
commissioner or any existing slate. Repeated runs preserve the same league and
membership. Both the explicit CLI confirmation and approval environment
variable are required; the command also validates the local
`127.0.0.1:55432` / `saturday_scout` target.

With `SATURDAY_SCOUT_DB_CONNECTION` securely configured for the local database,
run:

```bash
ASPNETCORE_ENVIRONMENT=Development \
SATURDAY_SCOUT_DEV_QA_LEAGUE_APPROVED=yes \
dotnet run --project src/SaturdayScout.Admin -- \
  provision-dev-qa-league \
  --commissioner-email gshunta@gmail.com \
  --player-email manual-player3@example.test \
  --confirm-operator-approval
```

The commissioner must be an existing SystemAdministrator domain/Identity
account. The Player must already exist, have a matching domain user, and hold
the Player role. The operation does not change either account's credentials,
roles, or existing league memberships.
