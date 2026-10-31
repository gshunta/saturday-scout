# Data sources and operator import

## College football: CollegeFootballData

Saturday Scout attributes college football schedule, team, and result data to [CollegeFootballData](https://collegefootballdata.com/). Its [API documentation](https://api.collegefootballdata.com/api/docs) and [terms](https://collegefootballdata.com/terms) govern use. Review those terms before production use. The importer uses `GET /teams?year=`, `GET /games/schedule?year=&seasonType=regular&week=`, and `GET /games?year=&seasonType=regular&week=`. Only regular season college football is supported in Phase 4.

The [published API tiers](https://collegefootballdata.com/api-tiers) list 1,000 calls per month for the free tier, but CFBD and related products may share an account quota. Saturday Scout does not assume it owns the full allowance. Confirm the account's actual tier and remaining shared budget before each live run. Other applications can consume calls at any time, so the operator's remaining-call estimate may become stale.

### Configuration and budget

Set `SATURDAY_SCOUT_DB_CONNECTION` to a PostgreSQL connection string. Apply EF Core migrations first. For live runs, set `CFBD_API_KEY` only in the operator environment or a secret store; never place it in source, fixtures, command arguments, or logs. Set `CFBD_SCOUT_MONTHLY_CAP` to the maximum calls Saturday Scout may make in the current UTC month. Set `CFBD_SHARED_REMAINING` to a conservative estimate of calls available to this run **after accounting for all other applications**. Set `CFBD_LIVE_IMPORT_APPROVED=yes` only after separate approval for a live authenticated import.

One week normally requests three endpoints. Every HTTP attempt, including retries and failed attempts, first creates a durable `ProviderRequestUsages` reservation. A PostgreSQL advisory transaction lock serializes reservations; the monthly cap includes reserved rows, so a crashed request still consumes budget conservatively. The in-process shared allowance limits attempts in that run. If the provider returns `X-CallLimit-Remaining`, the latest reported remainder is also enforced after accounting for subsequent Saturday Scout reservations. Because external applications do not write this ledger, the operator must still check shared quota. A missing/unknown provider header does not increase the allowed budget. Requests time out after 15 seconds; transient failures retry at most twice with bounded delay. A rate-limited response consumes a ledger entry.

### Commands

From the repository root, an offline fixture import uses no API key or provider network call:

```sh
dotnet run --project src/SaturdayScout.Import -- college --season 2026 --week 8 --fixture-dir tests/SaturdayScout.Infrastructure.Tests/Fixtures
```

After separate approval and configuration, the live command is:

```sh
dotnet run --project src/SaturdayScout.Import -- college --season 2026 --week 8 --live
```

The CLI prints created, updated, and unchanged counts plus every skipped or ambiguous record. Imports are transactional, preserve local team and game IDs, and can be repeated. A `Final` game is never downgraded or silently corrected; conflicting final scores require manual review. Missing kickoff, unsupported status, and completed games without corroborated scores are skipped. Missing team abbreviations use the stable `CFBD-{provider team ID}` fallback. A source `postponed`, `canceled`, or other unrecognized schedule status is reported and skipped until its exact provider semantics can be confirmed. Slate lock times are not automatically changed by an import.

No HTTP import endpoint exists. Game browsing retains its existing league membership requirement. The fixture files are small representative offline examples, not a provider data dump.
