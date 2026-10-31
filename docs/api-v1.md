# API v1 (Step 3)

Set `SATURDAY_SCOUT_DB_CONNECTION` as described in [database.md](database.md), apply migrations, and start the API with `dotnet run --project src/SaturdayScout.Api`. The Development OpenAPI document is `/openapi/v1.json`.

In Development, send `X-Dev-Subject: commissioner` to act as a local user. The handler creates a local user on first use. Send a different subject to act as another user. This handler is registered only in Development; Production rejects these headers and all `/api/v1` requests until real authentication is added.

| Method | Route | Access | Purpose |
|---|---|---|---|
| GET | `/api/v1/games?sport=nfl&season=2026&week=8&page=1&pageSize=50` | League member | Browse games, ordered by kickoff |
| POST | `/api/v1/leagues` | Authenticated | Create league and commissioner membership |
| GET | `/api/v1/leagues/{leagueId}` | Member | Read league |
| POST | `/api/v1/leagues/{leagueId}/members` | Commissioner | Add an existing user by external subject |
| POST | `/api/v1/leagues/{leagueId}/slates` | Commissioner | Create draft slate |
| PUT | `/api/v1/slates/{slateId}/games` | Commissioner | Replace ten draft game selections |
| POST | `/api/v1/slates/{slateId}/publish` | Commissioner | Publish with UTC lock deadline |
| GET | `/api/v1/slates/{slateId}` | Member | Read slate and ordered games |
| PUT | `/api/v1/slates/{slateId}/picks` | Member | Replace own draft picks before lock |
| POST | `/api/v1/slates/{slateId}/submit` | Member | Submit own complete sheet before lock |
| GET | `/api/v1/slates/{slateId}/picks/me` | Member | Read own sheet |
| GET | `/api/v1/leagues/{leagueId}/leaderboard?scope=nfl&season=2026&week=8` | Member | Read calculated standings; omit week for season |

`sport` is `college` or `nfl`; leaderboard `scope` also accepts `combined`. Games paging starts at 1 and is capped at 100 per page. All times include a UTC offset of zero. A successful pick replacement returns a new `version`; clients must send that version on later replacements and submission. Sending an old or missing version for an existing sheet returns 409. Pick writes use PostgreSQL row locking, optimistic concurrency, and the database clock at save and commit.

## Example requests

```json
POST /api/v1/leagues
{"name":"Saturday Friends"}
```

```json
POST /api/v1/leagues/11111111-1111-1111-1111-111111111111/members
{"externalSubjectId":"friend-1"}
```

The friend must have an existing user account. In Development, making any authenticated request with `X-Dev-Subject: friend-1` creates one.

```json
POST /api/v1/leagues/11111111-1111-1111-1111-111111111111/slates
{"sport":"nfl","season":2026,"week":8}
```

```json
PUT /api/v1/slates/22222222-2222-2222-2222-222222222222/games
{"gameIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002","00000000-0000-0000-0000-000000000003","00000000-0000-0000-0000-000000000004","00000000-0000-0000-0000-000000000005","00000000-0000-0000-0000-000000000006","00000000-0000-0000-0000-000000000007","00000000-0000-0000-0000-000000000008","00000000-0000-0000-0000-000000000009","00000000-0000-0000-0000-000000000010"]}
```

```json
POST /api/v1/slates/22222222-2222-2222-2222-222222222222/publish
{"lockUtc":"2026-11-01T16:00:00Z"}
```

```json
PUT /api/v1/slates/22222222-2222-2222-2222-222222222222/picks
{"expectedVersion":null,"picks":[{"gameId":"00000000-0000-0000-0000-000000000001","selectedTeamId":"33333333-3333-3333-3333-333333333333","confidenceRank":1}]}
```

Drafts may have fewer than ten picks. A full submission requires one valid pick for each slate game and each rank 1–10 exactly once.

```json
POST /api/v1/slates/22222222-2222-2222-2222-222222222222/submit
{"expectedVersion":1}
```

## Example responses

```json
201 Created
{"id":"11111111-1111-1111-1111-111111111111","name":"Saturday Friends","commissionerUserId":"44444444-4444-4444-4444-444444444444"}
```

```json
200 OK
{"id":"55555555-5555-5555-5555-555555555555","slateId":"22222222-2222-2222-2222-222222222222","status":"Draft","submittedUtc":null,"version":1,"picks":[{"gameId":"00000000-0000-0000-0000-000000000001","selectedTeamId":"33333333-3333-3333-3333-333333333333","confidenceRank":1}]}
```

```json
200 OK
{"leagueId":"11111111-1111-1111-1111-111111111111","scope":"nfl","season":2026,"week":8,"entries":[{"userId":"44444444-4444-4444-4444-444444444444","displayName":"Commissioner","points":55,"correctPicks":10,"pendingGames":0}]}
```

Errors use problem-details JSON: 400 for malformed business input, 401 for anonymous callers, 403 for insufficient league role, 404 for missing resources, 409 for stale versions, duplicate writes, immutable sheets, and expired deadlines, and 422 for invalid domain selections or incomplete submissions.

Game results can be updated only through the internal `IResultImportService`; no provider adapter or admin import route is exposed in Step 3. Standings are recalculated from submitted sheets and current results on each query. The persisted `Published` status may remain after the lock instant; pick writes still reject at the stored deadline. An automated status transition, audited scoring ledger, and tie policy are later-phase work.
