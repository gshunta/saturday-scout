# Admin Sprint 1C — Dashboard, Games, and Slate Drafts

## Routes

| Route | Behavior |
|---|---|
| `/dashboard` | Live college football team/game counts, league/slate totals, latest available game source timestamp, and upcoming scheduled games |
| `/games` | Server-filtered college football game browser, 25 rows per page, with season, week, team search, status, and kickoff sorting |
| `/slates` | College football slate list and completeness/readiness |
| `/slates/new` | Create an empty draft for a selected existing league, season, and week |
| `/slates/{id}/edit` | Add, remove, and reorder eligible games; save partial drafts or a ten-game draft |
| `/slates/{id}` | Read-only ordered slate summary |

All routes require an enrolled SystemAdministrator with completed MFA. The server-side portal rechecks the MFA-backed policy before every call to its application-service facade. UI components do not inject or query EF Core contexts.

## Existing model constraints and workflow

The existing `WeeklySlate` belongs to a league and the database has a unique key on league, sport, season, and week. Therefore the administrator selects an existing league; Sprint 1C does not create a global slate or change the schema. Games are college football only and must match the selected season and week. The domain permits partial drafts up to ten distinct games and rejects final, canceled, and no-contest games. The UI reports fewer than ten games as incomplete and ten eligible games as ready for a later publishing review. No publish action is provided.

Slates in non-Draft states are read-only. Draft writes take a PostgreSQL row lock and compare the expected `xmin` transaction ID before applying changes. A stale editor gets a conflict and must reload. The update transaction replaces only slate-game membership/order; it does not update picks. Submitted sheets are checked and remain immutable. This concurrency token uses PostgreSQL's built-in system column and adds no EF migration or user data column.

## Dashboard and game metadata

Counts are queried from the database on each dashboard load. The import metadata is the latest college game record with non-null `Games.SourceRetrievedUtc`, including its provider and raw source status; there is no import-batch entity in the current schema. Upcoming games are scheduled college games whose kickoff is in the future. Team search matches name or abbreviation using a server-side case-insensitive query. Status, season, and week filters, count, sort, and pagination are performed in PostgreSQL.

The current `Team` model contains no conference value. The UI explains that conference filtering is unavailable; no conference values are invented. `Game.KickoffUtc` is non-null in the current model, and the UI still renders missing future/API metadata as “Time TBD”. All times display in UTC.

## API and Admin/API boundary

New MFA-protected endpoints are listed in [`api-v1.md`](../api-v1.md). The separately hosted API validates its own Identity cookie and antiforgery token. The Admin Blazor server does not make HTTP calls to the separately hosted API: it invokes the same application service in-process through an authorization facade that rechecks the current principal against the DB-backed MFA policy on every operation. This avoids treating an Admin cookie as ambient API authorization and avoids a cross-origin cookie assumption. Any future browser-to-API call must use the documented common-origin gateway and be tested end to end.

## Local startup and checks

Follow [`local-development.md`](local-development.md) to configure the already-migrated local PostgreSQL database, shared Data Protection directory, and HTTPS certificate. Start the Admin portal with:

```sh
dotnet run --project src/SaturdayScout.Admin --launch-profile https
```

After signing in and completing MFA, use Dashboard → Games or Dashboard → Weekly slates. A league must exist before a draft can be created. To test slate operations without changing the local database, set `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION` to a PostgreSQL maintenance-database connection; persistence tests create uniquely named disposable databases, apply the existing migrations there, and drop the databases afterward.

Sprint 1C integration coverage exercises dashboard counts and import metadata, team search, filters/paging/order, duplicate slate creation, eligibility and week checks, partial and complete drafts, stale-version conflicts, and preservation of published slates and submitted picks. Authorization checks cover missing credentials, the production Development-header denial, and rejection of Player-only or role-only principals by the admin policy.

No migration was generated or applied for Sprint 1C. No imported team or game records are updated by the Admin portal.

## Known limitations and next work

- Conference filtering awaits provider conference data and a separately reviewed additive migration/import plan.
- The local browser portal invokes the shared application service in-process. A reverse-proxy/common-origin API gateway and end-to-end cross-host Identity cookie test remain deployment work.
- Dashboard import metadata is game source-retrieval metadata, not a batch-level audit record.
- Publishing, game/slate audit history, league creation/management UI, email/invitation operations, and administrator action audit persistence are outside this sprint.
