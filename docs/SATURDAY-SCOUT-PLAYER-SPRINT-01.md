# Saturday Scout — Player PWA Sprint 01

**Status:** Approved implementation handoff for Codex  
**Starting branch:** `feature/player-pwa-sprint-1` (already created and checked out by owner)  
**Baseline:** `main` committed and pushed before branch creation  
**Stack:** .NET 10, Blazor Web App (Interactive Server), ASP.NET Core Identity, PostgreSQL  
**Budget target:** $50/month total Azure operational spend (target, not a guaranteed cap)

## 1. Mission

Build a separately deployable, mobile-first `SaturdayScout.Player` Blazor Progressive Web App for an invitation-only beta. Reuse existing Saturday Scout API, domain rules, Identity store, and PostgreSQL. The beta is online-first: **do not queue or submit picks offline**. Keep the architecture compatible with a future native iOS App Store client and possible paid entitlements or advertising, but **do not implement monetization or native iOS in this sprint**.

## 2. Mandatory repository reconnaissance — before editing

1. Read `AGENTS.md`, architecture/ADR files, solution and project files, and current tests.
2. Confirm the active Git branch is `feature/player-pwa-sprint-1`; do not switch branches, commit, merge, push, or alter Git history.
3. Inspect `SaturdayScout.Admin` Identity registration, login/logout, MFA, authorization policies, cookies, Data Protection and local startup conventions.
4. Inspect `SaturdayScout.Api` authentication handlers, player endpoints, DTOs, and production-vs-development authentication behavior.
5. Inspect existing domain invariants: league membership, published slate visibility, editable draft, unique confidence 1–10, lock deadlines, immutable submitted sheet, concurrency, and transactions.
6. Inspect existing PostgreSQL connection configuration, migration ownership, launch profiles, tests, and Docker configuration.
7. Report discovered facts and conflicts before making substantial architecture changes. Do not assume endpoint names, Identity schemas, project references, or local ports.

## 3. Non-negotiable architectural rules

- New project: `src/SaturdayScout.Player` in the existing solution/repository. No new repository.
- Blazor Web App, Interactive Server for selected components; server-render other pages where practical.
- Separate Player and Admin deployable applications and **separate authentication cookies/schemes**; do not share browser sessions accidentally.
- Reuse the existing ASP.NET Core Identity users, password policies, lockout and user/domain mapping; no second user database or duplicated migrations.
- API remains authoritative for all picks, slates, deadlines, league membership, and submission rules. No duplicate business logic in Player.
- Browser communicates with Player's server for authenticated operations. Player-to-API calls require an explicitly authenticated, user-bound server-to-server mechanism. Never trust a client-supplied user ID, arbitrary forwarded header, or unsigned claims.
- Do not embed API bearer credentials, signing keys, or DB secrets in browser code.
- Keep API contracts usable by a future native iOS client; do not bind API domain semantics to Blazor circuits or cookies.
- Keep Admin MFA requirements and Admin functionality unchanged.
- Do not create/modify Azure resources, deploy, change subscription settings, or assume free-tier eligibility.
- No commits or pushes: the owner uses GitHub Desktop.

## 4. Implementation milestones

### M1 — Project foundation and configuration

- Add `SaturdayScout.Player` to solution using .NET 10 Blazor Web App and Interactive Server.
- Follow repository naming, DI, configuration, logging and test conventions.
- Create mobile-first layout with accessible bottom navigation for Picks, Standings, League and Profile; use safe-area insets and responsive sizing.
- Separate configuration for Player origin, API origin, DB/Identity, cookie and API assertion settings. Use user secrets/environment variables for sensitive local configuration.
- Reuse existing shared contracts or extract a small contracts project only if justified; do not reference Admin executable. Reference Infrastructure only as necessary for server-hosted Identity.
- Build the full solution and run existing tests; document baseline failures separately.

**Acceptance:** Player starts independently; Admin/API still build and function.

### M2 — Identity and session boundary

- Inspect existing Identity registration first; integrate the same user store and password/lockout logic.
- Add dedicated Player cookie and scheme. Proposed production cookie: `__Host-SaturdayScout.Player`, `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, no Domain.
- Proposed idle timeout: 8 hours with sliding expiration; proposed absolute maximum: 24 hours **enforced explicitly**. Confirm these values against existing security conventions before locking them in.
- Implement login/logout through normal HTTPS HTTP requests (not attempts to set response cookies from an established Blazor interactive circuit).
- Restrict access to eligible players; preserve legitimate users with both commissioner and player memberships. Enforce league membership separately from role checks.
- Apply anti-forgery protection to state-changing browser requests, prevent open redirects, and handle session expiry.
- Keep Admin cookies and MFA untouched. Confirm Data Protection key persistence requirements for production/multi-instance hosting.

**Acceptance:** Invited player signs in/out; bad passwords and locked accounts fail; Admin cookie does not grant a Player session; unauthorized league access fails.

### M3 — Player-to-API authentication and authorization

**This is a security gate. Do not build a production flow on development header authentication.**

- Inventory existing API auth and choose a production-safe, verifiable service-to-service scheme with user-bound identity. Preferred candidate: short-lived, signed assertion from Player server, with API verification of issuer, audience, signature, expiry, authorized caller and subject-to-domain-user mapping. Use established token libraries and a documented key rotation strategy; avoid inventing cryptographic primitives.
- Ensure the API independently checks authenticated player identity, league membership, ownership and operation authorization on every request.
- If an existing robust API bearer scheme is available, reuse it rather than introduce a competing custom protocol. Explain the choice and security implications.
- Use a typed server-side `HttpClient`/`PlayerApiClient`; handle unauthorized, forbidden, validation, conflict, and transient errors explicitly.
- Verify cross-user/cross-league isolation and forged/expired token rejection with integration tests.

**Acceptance:** API rejects anonymous, forged, expired and unauthorized requests; a Player session cannot impersonate another user by changing IDs.

### M4 — Core player workflows

Use actual existing endpoints/contracts, not guessed routes. Implement:

1. Player dashboard and league selection (for multiple memberships).
2. View current published college football slate and its ten games.
3. Load or create the player's draft pick sheet.
4. Select one winner per game and assign unique confidence values 1–10.
5. Save/reload an editable draft; show clear save status and validation.
6. Submit before the lock deadline; show immutable read-only submitted state.
7. Basic league standings using existing API.
8. Handle reconnect, expired session, conflicts and uncertain submission outcomes: re-query authoritative server state before retrying.

Keep server validation authoritative; never assume client-side checks make a submission valid. Preserve transaction/concurrency semantics already implemented in the backend.

**Acceptance:** A valid invited player can complete the entire ten-game flow on a phone. Invalid rankings, late submissions, edits to submitted sheets and cross-league operations fail safely.

### M5 — PWA foundation (online-first)

- Add `manifest.webmanifest`, actual 192px and 512px icons, theme metadata, and appropriate iOS home-screen icon metadata.
- Add a narrowly scoped service worker that caches only versioned **public static assets**. Never cache authenticated HTML, login/logout, API responses, user data, pick sheets, or submission requests; do not intercept Blazor SignalR/WebSocket traffic.
- Ensure installation/standalone launch works on supported mobile browsers; test iPhone Safari Home Screen behavior on a physical device when available.
- Show a reconnect banner and disable submission while disconnected; preserve local in-memory form state where possible, but reconcile with server after reconnection.
- No offline pick submission, background sync, push notifications or offline-first claim.

**Acceptance:** App is installable; online pick submission works; offline attempts do not get queued; stale private data is not served from a cache.

### M6 — Tests, documentation and handoff

- Add `SaturdayScout.Player.Tests` following existing test framework conventions; use bUnit if appropriate for components.
- Add/extend API integration tests using actual PostgreSQL for transaction-sensitive behavior.
- Test matrix: successful/failed/locked login, Player/Admin cookie isolation, anonymous/forged/expired assertion, wrong league, slate visibility, draft save/reload, duplicate confidence, missing picks, early/late submission, immutable sheet, concurrency, expired session, uncertain network result, logout, and offline no-queue behavior.
- Add Playwright smoke tests where practical; document physical iPhone Safari checks as manual acceptance tests if unavailable.
- Document local startup commands, required nonsecret environment variables, test commands, security boundary, production configuration checklist, and remaining risks.
- Report all changed files and tests run with results; clearly distinguish unrun tests from passing tests.

## 5. Hosting and cost guardrails

- Keep Player, Admin and API independently buildable/deployable; evaluate sharing an Azure App Service Plan or equivalent compute allocation rather than assuming each requires a dedicated plan.
- Validate Blazor Interactive Server WebSocket support, concurrent circuit capacity, Data Protection persistence, API network access, and health checks before production hosting.
- Target **$50/month total Azure operating cost**, including application compute, PostgreSQL, monitoring and incidental usage, assessed **after trial credits expire**. This is a financial target, **not an enforced cap**. Do not assert current prices or provision resources without explicit owner approval.
- Provide a deployment options/cost checklist, not an Azure deployment, in this sprint.

## 6. Future compatibility (design only; not sprint scope)

- Future iOS App Store client should consume the same protected API with an appropriate native OAuth/OIDC authorization-code + PKCE flow or equivalent established identity approach; do not require a Blazor cookie to use the API.
- Keep player authorization roles separate from commercial entitlements. `Player` is not synonymous with `Premium`.
- Leave room for server-validated Free/Premium/ad-free entitlements and an auditable subscription lifecycle, without creating speculative payment tables or integrating billing now.
- Potential monetization: premium subscriptions with advanced features, ad-supported free tier, ad-free tier. Review App Store in-app purchase rules, privacy disclosures, ad tracking consent and platform policies before implementation.
- Sports picks remain recreational/non-wagering; no real-money wagering or prize mechanics in this sprint.
- NFL and March Madness are future roadmap items, not Sprint 1 features.

## 7. Explicit exclusions

No native iOS/MAUI app, App Store submission, payments, advertising SDK, public signup, push notifications, offline submission, Azure deployment, new DB instance, redesign of Admin, or unrelated refactoring.

## 8. Codex execution protocol

1. Start with a concise repository gap analysis and proposed file-by-file implementation sequence.
2. Stop for owner decision if actual repository authentication architecture materially conflicts with this plan, especially at the Player/API trust boundary.
3. Implement milestones in order. After each milestone, build and run relevant tests, report results and unresolved issues.
4. Make only scoped code changes. Do not perform Git commits, pushes, branch switches or history rewrites.
5. At completion, provide a concise handoff with changed files, startup instructions, test results, manual iPhone verification steps, Azure deployment prerequisites, and open decisions.

## 9. Definition of done

- New Player application runs separately and preserves Admin/API behavior.
- Existing invited Identity user can authenticate through Player-specific cookie.
- API receives verifiable user-bound identity and enforces authorization independently.
- Ten-game pick flow, unique confidence assignment, draft persistence and immutable submission work through existing backend.
- Mobile-first PWA installs and behaves safely on reconnect; no offline submission.
- Security and integration tests pass, or documented blockers are explicitly surfaced.
- No Azure changes, no secrets committed, and no Git operations performed by Codex.
