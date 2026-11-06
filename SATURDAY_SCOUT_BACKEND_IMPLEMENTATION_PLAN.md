# Saturday Scout — Backend Implementation Plan

**Status:** Phases 1–4, Admin Sprint 1B/1C, and Admin Sprint 2 implemented. The Admin portal includes Identity/MFA, dashboard metrics, a college football games browser, and league-specific slate draft/randomize/publish workflows. No Sprint 1C or 2 migration was generated or applied. See `docs/admin-portal/` and `docs/api-v1.md`.
**Scope:** MVP backend, starting at Step 2 (domain model)  
**Stack:** .NET 10, ASP.NET Core, EF Core 10, PostgreSQL 17, xUnit, Docker Compose  
**Repository:** Existing `saturday-scout` repository  
**Last updated:** 2026-11-02

## 0. Objective and MVP boundaries

Build a testable, secure REST backend for a private friends pick'em league. Support college football and NFL, 10 commissioner-curated games per sport per week, **straight-up winner picks only**, unique confidence rankings 1–10, and college/NFL/combined standings. Each complete sheet has 55 possible points. **ATS, odds, paid subscriptions, wagering, and advanced analytics are explicitly out of scope**.

### Non-negotiable engineering rules

1. Work **one phase at a time**. Stop after validation and summarize changed files, tests, and remaining concerns. Wait for approval before the next phase.
2. Inspect the existing repository and `git status` before editing. Preserve existing work; do not recreate the solution, overwrite files, reset branches, or force-push.
3. Use the existing .NET solution/project layout; add missing components only after inspection. Pin SDK via `global.json` to an installed .NET 10 SDK and keep package versions on the EF Core 10 major line.
4. Keep Domain independent of EF Core, HTTP, and provider SDKs. Application depends on Domain. Infrastructure implements Application interfaces. API composes Application and Infrastructure.
5. All externally supplied IDs, times, and team selections must be validated server-side. Do not trust user IDs or commissioner status from request bodies in production.
6. Store all instants in UTC; retain the source season/week and sport as explicit fields. Use database constraints and transactional operations for invariants.
7. Do not put API tokens or credentials in source control. Use environment variables or .NET user-secrets locally.
8. Do not call live external APIs in unit tests. Use fixtures, fakes, or mocked HTTP responses. Do not perform a live provider import without explicit approval.
9. Use migrations for schema changes; never use `EnsureCreated()` for the application database.
10. Each phase ends with `dotnet build`, relevant `dotnet test`, and a review of `git diff`. Commit only after review/approval; do not push without approval.

## 1. Repository baseline — preflight (no feature changes)

Run from the existing repository:

```bash
cd ~/repos/saturday-scout
pwd
git status --short
git branch --show-current
git remote -v
dotnet --list-sdks
dotnet sln list
dotnet build
dotnet test
```

**Codex task:** Inspect the actual repository structure, project references, existing entities, tests, README, and configuration. Identify any differences from the expected layout. Do not assume Step 1 is incomplete. If an entity or service already exists, extend it instead of duplicating it. Report pre-existing failures separately from new failures.

Expected logical layout (adapt to actual project names):

```text
src/
  SaturdayScout.Api/
  SaturdayScout.Application/
  SaturdayScout.Domain/
  SaturdayScout.Infrastructure/
tests/
  SaturdayScout.Domain.Tests/
  SaturdayScout.Application.Tests/     # add when needed
  SaturdayScout.Api.IntegrationTests/ # add when needed
```

**Gate:** Baseline state recorded and no unintended edits.

---

## Phase 1 — Domain model and rules (original Step 2)

### Deliverables

Create or complete the following entities/value types:

| Type | Key properties / behavior |
|---|---|
| `Sport` | `CollegeFootball`, `Nfl` enum |
| `GameStatus` | `Scheduled`, `InProgress`, `Final`, `Postponed`, `Canceled`, `NoContest` |
| `Team` | ID, sport, name, abbreviation; provider IDs mapped separately or as unique source mappings |
| `Game` | ID, sport, season, week, home/away team IDs, kickoff UTC, status, nullable final scores, provider identity |
| `User` | ID, display name, external subject ID; email optional |
| `League` | ID, name, commissioner user ID, invite code metadata |
| `LeagueMember` | league ID, user ID, membership role and join time |
| `WeeklySlate` | ID, league ID, sport, season, week, status, lock UTC, published UTC |
| `SlateGame` | slate ID, game ID, display order 1–10 |
| `PickSheet` | ID, slate ID, user ID, draft/submitted state, submitted UTC, row version/concurrency token |
| `Pick` | sheet ID, game ID, selected team ID, confidence rank 1–10 |

### Domain invariants

- Each slate contains exactly **10 distinct games** when published; games must match its sport and intended season/week, unless an explicit cross-week policy is later approved.
- Every sheet has exactly one pick for each slate game and uses ranks 1–10 **exactly once** at submission; draft sheets may be incomplete.
- Selected team must be the game's home or away team.
- A member has at most one sheet per slate.
- The slate lock time is at or before the earliest selected game's kickoff; a submitted sheet cannot be edited after lock.
- A final game must have valid final scores; canceled/no-contest games are voided. Define tie handling explicitly (recommended: void the pick, award zero).
- Correct picks score their rank; incorrect or voided picks score zero. Never award points for non-final games.
- Domain methods guard illegal transitions (draft → submitted, slate draft → published → locked → finalized). Avoid public setters that bypass invariants.

### Implementation tasks

1. Add enums and entities in `SaturdayScout.Domain` with explicit constructors/factories and domain methods.
2. Implement a `PickSheet` aggregate or equivalent domain service for replacing draft picks and validating a complete submission.
3. Implement pure `ConfidenceScoringService` with a typed result (`Correct`, `Incorrect`, `Void`, `Pending`) and awarded points; avoid inferring a winner from an unfinished game.
4. Add domain tests covering valid/invalid ranks, duplicate ranks, missing games, invalid team, repeated game, 55-point maximum, ties, cancellations, and scoring determinism.
5. Document rule decisions and edge cases in `docs/domain-rules.md`.

### Acceptance criteria

- Domain compiles without EF Core references.
- Valid 10-pick sheet submits; any duplicate/missing rank or game is rejected.
- Scoring is deterministic and never scores unfinished games.
- Unit tests cover both happy paths and failure cases.

**Validation:**

```bash
dotnet test tests/SaturdayScout.Domain.Tests

dotnet build
```

**Codex stopping point:** Summarize entity API, invariants, test counts, and unresolved rule decisions. Await approval.

---

## Phase 2 — PostgreSQL, EF Core, and migrations

### Deliverables

- Root `compose.yaml` with PostgreSQL 17, a named volume, health check, and a development-only localhost port mapping.
- `SaturdayScoutDbContext` and entity configurations in Infrastructure.
- EF Core 10/Npgsql 10 packages aligned with .NET 10; design-time factory or startup-based migrations.
- First migration and documented commands.
- No secrets committed.

### Suggested schema constraints/indexes

| Table | Constraint |
|---|---|
| `Teams` | unique `(Sport, Provider, ExternalId)` if provider IDs stored here |
| `Games` | unique `(Provider, ExternalId)`; indexes `(Sport, Season, Week)` and kickoff |
| `LeagueMembers` | unique `(LeagueId, UserId)` |
| `WeeklySlates` | unique `(LeagueId, Sport, Season, Week)` |
| `SlateGames` | unique `(SlateId, GameId)` and `(SlateId, DisplayOrder)` |
| `PickSheets` | unique `(SlateId, UserId)` |
| `Picks` | unique `(PickSheetId, GameId)` and `(PickSheetId, ConfidenceRank)`; rank CHECK 1–10 |

Configure FK restrictions deliberately; don't cascade-delete completed historical picks when a team or game is removed. Use UTC timestamps (`timestamptz`) and optimistic concurrency on mutable sheets. A provider-specific ID should never be the application's primary key.

### Commands (adjust project paths after inspection)

```bash
docker compose up -d postgres
docker compose ps

dotnet tool install --global dotnet-ef  # only if missing; use a 10.x version

dotnet ef migrations add InitialCreate \
  --project src/SaturdayScout.Infrastructure \
  --startup-project src/SaturdayScout.Api \
  --output-dir Persistence/Migrations

dotnet ef database update \
  --project src/SaturdayScout.Infrastructure \
  --startup-project src/SaturdayScout.Api

dotnet build
dotnet test
```

**Acceptance criteria:** Fresh database initializes from migrations; a second migration run is harmless; DB constraints reject duplicate picks/ranks; repository has no secrets or local DB files. Add at least one database integration test using a disposable test database (do not use EF InMemory to validate relational constraints).

**Gate:** Inspect migration SQL and `git diff`; stop for approval.

---

## Phase 3 — Application services and REST API

**Implementation status (2026-10-31):** Complete, pending review. The application contract, PostgreSQL-backed services, Development-only fake identity, production deny scheme, `/api/v1` routes, and integration tests are in place. The internal result service has no HTTP route. See `docs/api-v1.md` for requests, responses, and limits. Remaining work in later phases: provider ingestion, real authentication, automated slate-status advancement, audited scoring/finalization, and a formal leaderboard tie policy.

### Application use cases

- Browse games by sport/season/week.
- Create a league and manage membership (commissioner only).
- Create draft weekly slate; select 10 games; publish and lock.
- Read a slate and current member's sheet.
- Save/replace draft picks atomically; submit after full validation.
- Read weekly/season standings.
- Import/update game results through an internal application service.

Use request/response DTOs, dependency injection, cancellation tokens, explicit validation, problem-details errors, and consistent pagination where applicable.

### API contract (proposed v1)

| Method | Route | Access | Result |
|---|---|---|---|
| GET | `/api/v1/games?sport=college&season=2026&week=9` | Member | Filtered games |
| POST | `/api/v1/leagues` | Authenticated | Created league |
| GET | `/api/v1/leagues/{leagueId}` | Member | League summary |
| POST | `/api/v1/leagues/{leagueId}/members` | Commissioner | Add/invite member |
| POST | `/api/v1/leagues/{leagueId}/slates` | Commissioner | Draft slate |
| PUT | `/api/v1/slates/{slateId}/games` | Commissioner | Set 10 games (draft only) |
| POST | `/api/v1/slates/{slateId}/publish` | Commissioner | Published slate |
| GET | `/api/v1/slates/{slateId}` | Member | Slate and games |
| PUT | `/api/v1/slates/{slateId}/picks` | Member | Save own draft sheet |
| POST | `/api/v1/slates/{slateId}/submit` | Member | Submitted own sheet |
| GET | `/api/v1/slates/{slateId}/picks/me` | Member | Own picks |
| GET | `/api/v1/leagues/{leagueId}/leaderboard?scope=college&season=2026` | Member | Standings |
| POST | `/api/v1/admin/imports/{sport}` | Admin | Trigger controlled import (later phase) |

**Identity design (revised 2026-11-01):** Do not accept a `userId` in normal pick-write requests. Resolve it from an authenticated GUID. The current development-only fake auth handler is transitional and must remain unavailable in Production. ASP.NET Core Identity with PostgreSQL is the approved target; see `docs/authentication-architecture.md` before changing the schema or user relationships.

**Concurrency:** Use transactions and optimistic concurrency. A pick save arriving after lock must fail even if the client had loaded the page before lock. Return `409 Conflict` for concurrent edits and appropriate `400/403/404/422` responses for invalid requests.

**OpenAPI:** Enable an OpenAPI document and an appropriate development-only interactive UI if desired. Do not assume the .NET template includes Swagger UI by default.

**Acceptance criteria:** Endpoints work against PostgreSQL; API integration tests cover authorization, slate publishing, pick submission, duplicate submissions, deadline enforcement, and invalid team IDs.

**Validation:**

```bash
dotnet build
dotnet test
dotnet run --project src/SaturdayScout.Api
# In another terminal, inspect the development OpenAPI endpoint and test with curl.
```

**Gate:** Share endpoint inventory, example request/response JSON, and test results. Await approval.

---

## Phase 4 — CollegeFootballData ingestion

**Implementation status (2026-10-31):** Complete, pending review. The operator-only CLI imports regular-season college football through provider-neutral application contracts and a CFBD-specific Infrastructure adapter. A narrow migration adds game source trace fields and a durable provider request usage ledger. Imports are transactional and idempotent, preserve local IDs, report skipped records, and protect final results. Fixture parser and PostgreSQL integration tests pass. Live authenticated CFBD calls and imports have not been run and require separate explicit approval. See `docs/data-sources.md` for attribution, terms, budget controls, configuration, and commands. The approved operator CLI replaces the originally proposed commissioner/admin HTTP trigger; game browsing continues to require league membership. Remaining limitations: the provider's actual account tier and shared remaining quota must be confirmed by the operator; unsupported provider statuses are skipped; final score conflicts need manual review.

### Provider contract

```csharp
public interface IFootballScheduleProvider
{
    Task<IReadOnlyList<ImportedGame>> GetGamesAsync(
        Sport sport, int season, int week, CancellationToken cancellationToken);
}

public interface IFootballResultsProvider
{
    Task<IReadOnlyList<ImportedResult>> GetResultsAsync(
        Sport sport, int season, int week, CancellationToken cancellationToken);
}
```

Use provider-neutral DTOs. Infrastructure adapters map provider responses into these DTOs. Keep external IDs, source name, retrieval time, and raw-status mapping for traceability.

### College implementation

1. Read current CollegeFootballData API documentation and confirm the account's actual limits, endpoint paths, and license before implementation.
2. Add an `HttpClient`-based adapter with bearer token supplied by environment/user-secrets.
3. Import teams and games by season/week, upsert by `(Provider, ExternalId)`, and preserve app-local IDs.
4. Map `scheduled/in-progress/final/postponed/canceled` states carefully; do not infer final from the presence of a score.
5. Implement timeouts, retry with bounded backoff for transient errors, and rate-limit handling. Log counts and metadata, not secrets.
6. Add fixture-driven parser and idempotency tests.
7. Provide a commissioner/admin-triggered import first; scheduled imports can follow.

**Acceptance criteria:** Re-importing a fixture produces no duplicates; updated kickoff/scores are reflected; finalization never occurs for incomplete games; live API call requires explicit approval.

**Gate:** Report request budget, provider terms, sample mapped games, and tests. Await approval.

---

## Phase 5 — NFL schedule/results ingestion (nflverse)

1. Verify the current nflverse dataset URL, schema, update cadence, and applicable license/attribution before writing a downloader.
2. Prefer a versioned downloadable schedule/results artifact, not an unofficial undocumented API.
3. Build an adapter for CSV or other stable documented format; validate column names and types, reject malformed rows with actionable errors.
4. Normalize team abbreviations through a documented mapping table and preserve original provider values.
5. Upsert by provider game ID and reconcile status, kickoff changes, and scores.
6. Use local fixture files in tests; do not depend on GitHub availability in CI.
7. Add attribution/terms notes to `docs/data-sources.md`.

**Acceptance criteria:** A complete NFL week imports deterministically; repeated imports are idempotent; malformed data cannot corrupt an existing slate.

**Gate:** Demonstrate local fixture import and mapping tests. Await approval.

---

## Phase 6 — Scoring, finalization, and leaderboards

### Rules

- Score only games with authoritative `Final` status.
- Winner determined by home/away final scores. Tie, canceled, or no-contest => zero points for that game, marked void.
- A valid 10-game sheet uses all ranks 1–10 and can earn at most 55 points.
- Standings: `college`, `nfl`, `combined`; combined is the sum of both sports for the same season/league.
- Distinguish **pending** games from incorrect picks; don't prematurely report a final weekly total.
- Score calculation must be repeatable and idempotent; corrections to final scores trigger safe recalculation with an audit record.
- No pick visibility to other members before the slate lock. After lock, show submitted picks according to league rules.

### Deliverables

- Application scoring service and result/leaderboard queries.
- Persisted result audit entries or a reproducible scoring ledger (choose and document one approach).
- Background finalization job (simple hosted service or scheduled process) with no duplicate scoring.
- Tests for perfect 55, zero, partial final games, voids, tie-break policy, late results, corrected scores, and repeated finalization.

**Acceptance criteria:** Seed 4 users and 2 weekly slates (college/NFL), enter final scores, and verify each sport and combined standings. Re-run scoring without changing totals.

**Gate:** Show seeded scoreboard and test summary. Await approval.

---

## Admin Sprint 1 — Identity and secure administration foundation

**Migration gate completed locally (2026-11-01):** The owner approved the additive plan and later approved execution. A fresh backup was restored to a disposable database before both Identity migrations were applied to local development. See `docs/identity-migration-review.md`. Any other environment needs its own backup, review, and approval.

**Admin Sprint 1B (2026-11-01):** Implemented the .NET 10 Interactive Server Admin foundation in `src/SaturdayScout.Admin`, shared Identity cookie and Data Protection configuration, administrator password/TOTP/recovery login, protected dashboard, restricted first-admin bootstrap command, and disposable PostgreSQL authentication tests. No administrator was created and no new migration was generated or applied. See `docs/admin-portal/authentication-implementation.md` and `docs/admin-portal/local-development.md`.

**Admin Sprint 1C (2026-11-02):** Implemented the live dashboard metrics, server-filtered/paginated college games browser, league-specific college football slate draft workflow, MFA-protected `/api/v1/admin` endpoints, and per-operation MFA authorization in the Blazor server application-service facade. Draft saves preserve the existing 0–10 partial-draft rule and use PostgreSQL `xmin` optimistic concurrency; 10 eligible games are marked ready for later publishing review. No migration was generated/applied, imported games were not modified, and publication was not added. Conference filtering is unavailable because the current Team schema has no conference field. See `docs/admin-portal/sprint-1c.md` and `docs/api-v1.md`.

**Admin Sprint 2 (2026-11-02):** Added ten-game randomized selection, admin MFA-protected slate publication using the existing domain `Publish` rules, and UTC lock-time UI. Draft updates and publication serialize with row locks and compare PostgreSQL `xmin`; stale and duplicate publish attempts conflict. Submitted picks and published slates remain immutable, and the commissioner API remains unchanged. Tests cover random eligibility/count, publication validation, auth/antiforgery, and concurrent duplicate publication. No schema migration or imported-data update was needed. See `docs/admin-portal/sprint-2.md`.

**Admin Sprint 1D next:** Add invitations/email and password reset, audited privileged actions, league management, and the same-origin API gateway/interoperability test. Protect and validate production Data Protection storage and administrator recovery before deployment.

1. Add ASP.NET Core Identity persistence in the existing PostgreSQL database with GUID Identity IDs equal to domain `Users.Id`. Use a separate `auth` schema, a reviewed EF Core migration, and a one-to-one FK; preserve every existing domain FK and imported game row.
2. Introduce `SystemAdministrator`, `LeagueCommissioner`, and `Player` Identity roles. Keep league-scoped membership checks as the authority for league actions. Add explicit admin and resource-based policies; require completed MFA for Production administrator actions.
3. Implement invitation-only account creation, confirmed email, password reset, administrator TOTP enrollment and recovery, one-time operator bootstrap, secure cookie sessions, antiforgery, endpoint rate limits, and account lockout. No public registration or hardcoded bootstrap credential.
4. Establish the separately hosted Blazor Server portal behind a common-origin proxy with the API. Configure a shared cookie scheme and persistent Data Protection keys; propagate the user's cookie and antiforgery proof on portal-to-API calls. Keep mobile bearer authentication out of this sprint.
5. Add append-only audit records for privileged changes and tests for migration preservation, invitation lifecycle, lockout, MFA, cookie/antiforgery behavior, cross-league authorization, and Production rejection of the Development header.
6. Deploy only after email delivery, public origin, key storage, backup, and admin recovery are configured. Review migration SQL and pre/post counts before touching the live database.

**Acceptance criteria:** All security criteria in `docs/authentication-architecture.md` pass; no user ID or foreign key changes unexpectedly; no anonymous admin write, public signup, or mobile token endpoint; secrets absent from source; Production fails closed.

## Later authentication work — SwiftUI

Design and implement an explicit native bearer and refresh flow against the same Identity GUID and domain user. Review token format, revocation, device storage, and route scheme selection separately. Do not enable Identity's built-in token endpoint as an incidental Sprint 1 feature.

---

## Phase 8 — SwiftUI readiness and deployment preparation

1. Stabilize `/api/v1` contracts and OpenAPI schema; document examples for the SwiftUI client.
2. Provide local API startup and device/simulator networking instructions (never embed API secrets in iOS app).
3. Add a reproducible seed/demo mode with fictional users and deterministic results.
4. Create CI for restore/build/test and migration validation.
5. Prepare Azure App Service and Azure Database for PostgreSQL configuration; use managed secrets, TLS, backup, and observability.
6. Document release and rollback procedure. No deployment without explicit approval.

**Acceptance criteria:** A new developer can clone, configure secrets, start Docker, apply migrations, run tests, and query the API from a clean machine.

---

## 9. Working with Codex from the command line

### Recommended workflow for every phase

```bash
cd ~/repos/saturday-scout
git status --short
# Create a working branch if not already on one:
git switch -c feature/domain-model  # example; use a new name per phase
codex
```

Paste this instruction at the beginning of a Codex session:

> Read `SATURDAY_SCOUT_BACKEND_IMPLEMENTATION_PLAN.md`, inspect the existing repository and git status, and implement **only Phase 1**. Preserve existing work. Before coding, summarize your plan and identify assumptions. Implement the smallest coherent changes, add automated tests, run `dotnet build` and `dotnet test`, and report changed files, test results, and open questions. Do not commit, push, invoke live external APIs, or begin another phase without my approval.

For subsequent sessions replace **Phase 1** with the approved phase number. If your Codex CLI supports one-shot execution, you may use a prompt like:

```bash
codex exec "Read SATURDAY_SCOUT_BACKEND_IMPLEMENTATION_PLAN.md. Inspect the repo and implement only Phase 1. Add tests, run build and tests, report results. Do not commit, push, or proceed to Phase 2."
```

**Note:** CLI syntax and approval modes can vary by installed Codex version. Confirm with `codex --help` and `codex exec --help`. Avoid unrestricted filesystem or network permissions.

### Review before every commit

```bash
git status --short
git diff --check
git diff --stat
git diff
dotnet build
dotnet test
```

After review, commit deliberately:

```bash
git add src tests docs
# Include new root configuration files explicitly when introduced.
git commit -m "feat(domain): implement Saturday Scout core rules"
# Push only when ready:
git push -u origin HEAD
```

For Mac/Windows synchronization, commit and push a phase on one computer, then pull on the other. Do not work simultaneously on the same branch without coordinating merges.

## 10. Definition of done — backend MVP

- [ ] Domain rules and all 10-rank validation are tested.
- [ ] PostgreSQL schema is migration-managed and constraint-tested.
- [ ] League/slate/pick endpoints have documented contracts and authorization.
- [ ] College and NFL imports work with fixtures and approved live sources.
- [ ] Scoring is deterministic, idempotent, and auditable.
- [ ] College, NFL, and combined leaderboards are correct.
- [ ] Lock times and privacy rules are enforced on the server.
- [ ] No secrets or development bypasses are exposed in production.
- [ ] Fresh clone setup is documented for macOS and Windows.
- [ ] CI builds and tests pass.

## 11. Decisions to resolve as implementation progresses

- **MVP sport launch:** Backend supports both college and NFL; college-only initial UI launch is permissible without changing the schema.
- **Lock policy:** Recommend one slate-level lock at the earliest selected kickoff. Confirm before implementing submission.
- **Missed entry:** Recommend an unsubmitted/incomplete sheet earns zero; no partial credit.
- **Tiebreakers:** Recommend cumulative points first, then correct picks, then a documented deterministic tie policy. Do not invent a game-total prediction feature in Sprint 1.
- **Cross-week games:** Prefer disallowing them in a published slate for MVP.
- **Data licenses:** Recheck provider commercial use and redistribution terms before App Store release.
- **Season semantics:** NFL and college season-year/week calendars differ; normalize per sport, not by assuming calendar-week equality.

---

**Start here:** Complete the preflight, then instruct Codex to implement **Phase 1 only**. This plan deliberately separates domain modeling, persistence, API, ingestion, and scoring so failures are easy to isolate and each Git checkpoint is reviewable.
