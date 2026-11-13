# React Player Picks acceptance scenario

## Development-only provisioning

The import CLI has a local-only `dev-picks-scenario` command. It requires all of:

- `ASPNETCORE_ENVIRONMENT=Development`
- `SATURDAY_SCOUT_DB_CONNECTION` configured for database `saturday_scout` on local loopback port `55432`
- Explicit `--apply`, existing `--league-id`, existing `--player-id`, and `--scenario-key`
- An existing domain league membership, matching Identity/domain GUID, and Identity `Player` role
- Both EF migration sets already applied; the command refuses to run migrations

The command never creates users, memberships, or pick sheets. It selects ten eligible imported games only if an unused league/season/week has ten games kicking off after the 48-hour lock. Otherwise it creates ten deterministic synthetic scheduled college games, tagged with provider `saturday-scout-dev`, using twenty existing college team records. It uses the existing `WeeklySlate.ReplaceGames` and `WeeklySlate.Publish` domain rules and saves the games and slate in one transaction. It does not call CFBD or modify any existing row.

The scenario key is the idempotency key with the league ID. Repeating the same command returns the same active published slate without modifying it. If that slate is expired or closed, the command refuses to reset it; use a new scenario key to create a separate scenario in an unused week. A PostgreSQL advisory transaction lock serializes scenario creation for the league.

Run from the repository root on macOS after configuring the existing local environment variable `SATURDAY_SCOUT_DB_CONNECTION` (do not place the connection string in source control):

```sh
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project src/SaturdayScout.Import -- \
  dev-picks-scenario --apply \
  --league-id 37770620-bb3b-4efd-b149-3a4e6737a26b \
  --player-id 30b1a805-93d5-4ee2-a97b-aee47ca1404a \
  --scenario-key react-e2e-2026-11
```

The command reports the slate ID, season/week, UTC lock time, and whether games were synthetic. It does not print the connection string. The verified development League is `Saturday Scout Beta`; the Player is the existing `Milestone 4 QA Player` member. No user or membership provisioning is performed.

## Provisioned local scenario

The command was run against the verified local Development database and rerun with the same key to verify idempotency:

- League ID: `37770620-bb3b-4efd-b149-3a4e6737a26b`
- Player ID: `30b1a805-93d5-4ee2-a97b-aee47ca1404a`
- Test slate ID: `521bd9d8-97b5-ffea-2d5a-262608c48e8e`
- Season/week: 2026 / 12
- Status: Published, exactly 10 distinct synthetic games
- Lock: `2026-11-16T01:54:29.610541Z`
- Pick sheets created by provisioning: 0

Read-only postflight confirmed Week 10 and Week 11 slate IDs/statuses/deadlines remained unchanged and their picks were not modified. Week 10's deadline was already past. Week 11 remains published until `2026-11-14T04:59:00Z` and was the active-slate result at provisioning time; it already had one submitted sheet. The test slate's later, valid lock means the existing active-slate selection continues to serve Week 11 until that lock passes. Afterward, the API falls back to the latest published season/week, which is test Week 12. No existing lock was changed to make the test scenario appear earlier.

The React `/react/picks` route now implements native draft, review, and submission workflows. The existing Blazor `/picks` page remains available as a fallback.

## React picks BFF contract

The React page uses same-origin BFF calls only; it does not call the API directly. Existing Player-cookie authorization, antiforgery, API assertion forwarding, and backend validation remain in place. No backend endpoint or database change was needed.

| React request | Purpose | Response behavior |
| --- | --- | --- |
| `GET /bff/api/v1/leagues/{leagueId}/slates/active` | Load the authorized league's active/fallback published slate | `200` with `PlayerSlateDto`, or `204` with no slate |
| `GET /bff/api/v1/slates/{slateId}/picks/me` | Restore the player's sheet | `200` with `PickSheetDto`, or `404` when no sheet exists (React treats this as an empty draft) |
| `PUT /bff/api/v1/slates/{slateId}/picks` | Save a valid partial or complete draft | `PickSheetDto`; sends `picks` and `expectedVersion` with `X-CSRF-TOKEN` |
| `POST /bff/api/v1/slates/{slateId}/submit` | Submit a saved complete sheet | Authoritative `PickSheetDto`; sends `expectedVersion` with `X-CSRF-TOKEN` |

The read models are `PlayerSlateDto` (season/week/status/lock and ordered `PlayerSlateGameDto` games), `PickDto` (game/team/rank), and `PickSheetDto` (sheet/slate/status/submission time/version/picks). Writes preserve API validation and `409` concurrency/lock semantics. React does not persist private query data or retry ambiguous writes automatically.

## Local startup

Build React assets first so the Player host can serve them at `/react/`:

```sh
cd src/SaturdayScout.Player.Web
VITE_PLAYER_ORIGIN=https://127.0.0.1:7276 npm run build
cd ../..
```

Keep the Player origin hostname consistent with the hostname used for login. The Player cookie is host-only; `127.0.0.1` and `localhost` do not share it.

With the existing local Development/User Secrets configuration available, start the API and Player host in separate terminals:

```sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SaturdayScout.Api --launch-profile https
```

```sh
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SaturdayScout.Player --no-launch-profile --urls https://127.0.0.1:7276
```

The existing local settings must still provide `SATURDAY_SCOUT_DB_CONNECTION`, the Player API origin and RS256 private-key configuration, API RS256 public-key configuration, and Data Protection storage. Values remain in existing local secrets/configuration; do not add them to this document. Open `https://127.0.0.1:7276/react/` and sign in with the already-provisioned Player account.

## Manual acceptance procedure

1. Open `https://127.0.0.1:7276/react/login`, sign in with the existing Player account, and navigate to Picks. Use the same hostname for login and subsequent navigation; cookies are host-only.
2. Until Week 11 locks at `2026-11-14T04:59:00Z`, the API may continue to return Week 11 as the active slate. Do not edit or submit that existing sheet for this test. After its lock, confirm the React Picks page displays the published Week 12 slate and ten games.
3. Select one participating team on each matchup card. Assign each confidence rank from 1 through 10 exactly once; a rank already used on another game is disabled.
4. Change one game's selection and rank, then click **Save draft**. Confirm the saved feedback, refresh the browser, and verify the winner and rank are restored from the server.
5. Try **Review submission** with missing picks. Confirm the review lists missing winners/ranks and that **Confirm submission** remains disabled. Confirm duplicate ranks are prevented in the selector and explained if an invalid server draft is returned.
6. Complete ten picks with unique ranks, save the draft, then choose **Review submission**. Confirm the review lists each game, winner, and rank, and warns that submission is final.
7. Click **Confirm submission** once. Confirm the server-authoritative Submitted state appears and all game controls and save/submit actions are read-only. Refresh and confirm the same submitted sheet remains read-only.
8. In the browser Network panel, inspect the same-origin BFF response from `GET /bff/api/v1/slates/{slateId}/picks/me` after submission. It should show Submitted and ten picks. Do not copy cookies, antiforgery values, or tokens into logs.
9. Run the read-only SQL below in the existing secured database client to confirm the sheet and rank rows. Replace the IDs only if using a different existing test scenario/player:

   ```sql
   SELECT ps."Id", ps."Status", ps."SubmittedUtc", ps."Version",
          count(p."GameId") AS pick_count,
          count(DISTINCT p."ConfidenceRank") AS unique_ranks,
          min(p."ConfidenceRank") AS min_rank,
          max(p."ConfidenceRank") AS max_rank
   FROM public."PickSheets" ps
   LEFT JOIN public."Picks" p ON p."SheetId" = ps."Id"
   WHERE ps."SlateId" = '521bd9d8-97b5-ffea-2d5a-262608c48e8e'
     AND ps."UserId" = '30b1a805-93d5-4ee2-a97b-aee47ca1404a'
   GROUP BY ps."Id";
   ```

   A submitted sheet should have `Status = Submitted`, `pick_count = 10`, `unique_ranks = 10`, `min_rank = 1`, and `max_rank = 10`.

10. Use the Player navigation **Sign out** action and confirm the React login route appears. Then attempt to open `/react/picks` directly and verify the established session-expired/login flow runs.

The API is authoritative for membership, slate status/deadline, confidence ranks, concurrency, and submission immutability. Never test a late submission by altering the lock or database clock. For lock handling, use an expired test scenario or wait for the configured deadline; do not alter existing slates.

## Stage 4 automated verification

From `src/SaturdayScout.Player.Web`, run `npm test` and `npm run build`. The React suite covers active and missing slates, restoration, partial draft save, confidence validation, review, successful submission, double-submit prevention, submitted immutability, locked state, concurrency conflict resolution, session expiry, and forbidden access. Backend business rules and authorization continue to be exercised by the existing .NET tests; this stage adds no backend changes or migrations.
