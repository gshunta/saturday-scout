# Admin Sprint 2 — Weekly Slate Management

Sprint 2 extends the existing Sprint 1C league-specific slate drafts. It keeps the domain, PostgreSQL model, and commissioner API contracts intact.

## Draft selection

The editor loads the slate's season/week through the server-paginated college games query. Administrators can select, remove, and reorder up to ten unique games and save/reopen the draft. The server rejects games from a different week/season/sport and final, canceled, or no-contest games. Partial drafts remain supported for compatibility; exactly ten current eligible games are required to publish.

“Randomly select & save 10” samples ten distinct eligible games from the full imported slate week, not only the visible 25-row page. It uses the same `WeeklySlate.RandomizeGames` domain validation as user selection and saves with the current `xmin` version. Fewer than ten candidates produces a validation error without modifying the draft.

## Publication

The slate details view offers publication only for a draft with ten selected games. The operator enters a lock time labelled UTC. The server invokes `WeeklySlate.Publish`, which checks exact membership, game eligibility, future lock time, and lock time no later than earliest kickoff using the PostgreSQL clock. Publication is transactional and compares `xmin` under a row lock. A competing or stale publish attempt receives a conflict. A published slate is read-only; submitted pick sheets are not altered.

The admin-only API adds `POST /api/v1/admin/slates/{id}/randomize` and `POST /api/v1/admin/slates/{id}/publish`. Both require the existing SystemAdministrator MFA policy and cookie antiforgery token. Existing commissioner routes and mobile-facing contracts are unchanged.

## Schema and tests

No migration is needed. PostgreSQL's existing `xmin` system column provides optimistic versioning. Tests cover seeded random selection, insufficient candidates, exact-ten publication validation, invalid lock time, successful publication, stale and concurrent duplicate publication, administrator cookie/MFA and antiforgery requirements, and preservation of submitted picks. Persistence tests use disposable PostgreSQL databases.

Run the portal with the existing local instructions. Create or open a draft, use randomize to save ten choices, review the matchup order, then publish with a valid UTC deadline. For the 2026 Week 10 import, the random picker evaluates all 63 games and excludes ineligible statuses.
