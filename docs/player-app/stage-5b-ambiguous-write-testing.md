# Stage 5B ambiguous-write testing

## Stage 5B acceptance closeout

**Manual acceptance: all 9 acceptance tests PASS, as reported by the operator.** The manual results below are operator-reported; they were not independently repeated during this closeout. Automated results are recorded separately at the end of this document.

| Test | Manual status | Evidence recorded |
| --- | --- | --- |
| 1–8 | PASS (operator-reported) | The operator reports all eight Stage 5B acceptance tests passed. Individual observation details were not included in the closeout request. |
| 9A — ambiguous Save Draft after server commit | PASS (operator-reported) | Full details below. |
| 9B — Save Draft blocked before server commit | PASS (operator-reported) | Full details below. |
| 9C — ambiguous Submit Picks after server commit | PASS (operator-reported) | Full details below. |

The Player BFF can simulate an unknown browser outcome in local Development after the real API confirms a QA write succeeded. The BFF returns HTTP 503 instead of the upstream success response. This exercises client reconciliation, but it does not simulate a dropped TCP connection.

The fault is disabled by default. If no target IDs are configured, the existing Week 13 defaults remain:

- QA league: `c350c497-5043-45cf-8ce6-a8bdcc4d6775`
- QA slate: `130f3cfd-de08-1d24-8957-4bd3e4837dd0`
- QA player: `0cc5eaa0-cb07-4017-9992-3e66d3f81b71`

For a separate QA scenario, all three target IDs can be supplied at startup with `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_PLAYER_ID`, `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_LEAGUE_ID`, and `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_SLATE_ID`. The three IDs must be supplied together, be non-empty GUIDs, and accompany both an operation and explicit approval. These settings are accepted only in Development. The BFF first calls the authorized API slate-read route as the Player and requires the exact configured slate and league plus CollegeFootball sport. It then forwards the mutation normally. The fault is consumed only if the API returns a valid successful `PickSheetDto` for the configured slate and expected status. Failed writes release the one-shot arm. No arming endpoint exists.

## Arm one operation

Start the API normally in Development. Start the Player host in a separate terminal with exactly one operation armed:

```sh
env ASPNETCORE_ENVIRONMENT=Development \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE=save-draft \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_APPROVED=yes \
  dotnet run --project src/SaturdayScout.Player --no-launch-profile --urls https://127.0.0.1:7276
```

For submission, stop the Player host and restart it with `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE=submit-picks`. The approval variable remains required. Only `save-draft` and `submit-picks` are accepted. Configuration outside Development is rejected at startup.

The startup log states that one operation is armed. After the matching API success, the Player log records that the simulation was consumed. Logs contain no cookies, tokens, credentials, or pick contents. The fault cannot be consumed twice in one process. After each test, stop the host and restart without both `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE` and `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_APPROVED`; this leaves the feature disarmed. Do not leave an armed host running unattended.

## Before testing

Use only Saturday Scout QA. Confirm the configured Player is a member, the configured slate is published and still unlocked, and the Player has no existing sheet that should be preserved. The BFF's authorized slate preflight verifies membership when the armed mutation is attempted. A normal Save Draft replaces the current draft picks for that player and slate. A successful Submit permanently makes that sheet immutable. No QA data was changed when implementing this mechanism.

Use the existing authorized read route to check the authoritative sheet:

```text
GET /bff/api/v1/slates/130f3cfd-de08-1d24-8957-4bd3e4837dd0/picks/me
```

HTTP 404 means no sheet exists. HTTP 200 returns the authoritative status, version, and picks. Do not retry a write just because the browser shows an ambiguous failure.

## Scenario A — Save committed, response obscured

**Manual acceptance status: PASS (operator-reported; not independently rerun during this closeout).**

Reported evidence: one PUT was sent, the API committed the draft, and the Development BFF returned HTTP 503. React reconciled the server-authoritative draft and confirmed the updated Game 1 winner persisted. No duplicate PUT occurred and no unresolved save warning remained.

1. Arm `save-draft` and restart the Player host.
2. Sign in as the configured QA Player and open the configured QA slate.
3. Make a valid partial or complete draft change and click **Save draft** once.
4. The API commits the draft and returns success. The BFF consumes the fault and returns 503 to React.
5. React performs a read-only sheet request. If the server draft matches the local picks, React confirms the saved draft and clears dirty state. There must be only one PUT.
6. Verify the returned server sheet through the read route above. Confirm one consumed-fault log entry.

## Scenario B — Save did not reach the server

**Manual acceptance status: PASS (operator-reported; not independently rerun during this closeout).**

Reported evidence: Chrome DevTools blocked the Save Draft request before commit. The server draft remained unchanged, the unsaved local selection remained visible, and no automatic PUT retry or unintended server overwrite occurred. In the earlier recovery check, **Check Server Version** detected the difference and **Load Server Version** succeeded.

This scenario uses Chrome DevTools without arming the BFF fault.

1. Use the same QA Player and slate only after confirming its current server draft. Make a different local selection so it differs from that saved draft.
2. In DevTools **Network request blocking**, block only the exact `PUT /bff/api/v1/slates/130f3cfd-de08-1d24-8957-4bd3e4837dd0/picks` URL. Do not block `GET .../picks/me`.
3. Click **Save draft** once. The browser blocks the PUT before it reaches the host; React's reconciliation GET still reaches the server and sees the prior draft.
4. React should preserve the local selection, show the conflict/recovery message, keep writes disabled, and send no automatic second PUT.
5. Verify the authoritative sheet is unchanged through the read route. Remove the DevTools block before any later operation.

## Scenario C — Submit committed, response obscured

**Manual acceptance status: PASS (operator-reported; not independently rerun during this closeout).**

### Week 14 QA fixture

The local Development database was checked read-only before provisioning: the Saturday Scout QA league and Player 3 membership existed, Week 14 had no QA slate and no imported Week 14 game rows, and Player 3 had no submitted sheet for this scenario. The existing Development scenario provisioner created ten synthetic scheduled college games from existing team records and published the slate through domain rules. Its 48-hour lock lead time places kickoff times after the lock. It creates no pick sheet or picks, makes no CFBD requests, and does not touch the Beta league.

- League: `Saturday Scout QA` (`c350c497-5043-45cf-8ce6-a8bdcc4d6775`)
- Player: `manual-player3@example.test` (`0cc5eaa0-cb07-4017-9992-3e66d3f81b71`)
- Slate: `f46b4039-5ed6-d48b-13b8-342a77590cba`
- Season/week: 2026 / 14
- Status: Published; 10 unique synthetic games
- Lock: `2026-11-22T14:06:45.550341Z`
- Earliest kickoff: `2026-11-22T18:06:45.550341Z`
- Player 3 sheet: none at preparation time; a complete draft was saved for this manual test

The fixture command is Development-only, requires the verified local `saturday_scout` database on port 55432 through `SATURDAY_SCOUT_DB_CONNECTION`, and is idempotent for this scenario key:

```sh
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project src/SaturdayScout.Import -- \
  dev-picks-scenario --apply \
  --league-id c350c497-5043-45cf-8ce6-a8bdcc4d6775 \
  --player-id 0cc5eaa0-cb07-4017-9992-3e66d3f81b71 \
  --scenario-key stage5b-test9c-w14 \
  --season 2026 --week 14
```

The command was rerun with the same key and reused the existing slate without changing it. Read-only verification confirmed ten distinct attached games, every kickoff later than the lock, and no Player 3 pick sheet. Do not rerun with another key for Week 14; that week is already occupied in this QA league.

### Arm and execute the manual test

Keep the API in its normal Development configuration. Stop the Player host, then start it from a shell where the existing `SATURDAY_SCOUT_DB_CONNECTION` and Player-to-API assertion configuration are available through the approved local configuration. The command only adds one-shot fault settings and does not alter or print credentials:

```sh
env ASPNETCORE_ENVIRONMENT=Development \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE=submit-picks \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_APPROVED=yes \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_PLAYER_ID=0cc5eaa0-cb07-4017-9992-3e66d3f81b71 \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_LEAGUE_ID=c350c497-5043-45cf-8ce6-a8bdcc4d6775 \
  SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE_SLATE_ID=f46b4039-5ed6-d48b-13b8-342a77590cba \
  dotnet run --project src/SaturdayScout.Player --no-launch-profile --urls https://127.0.0.1:7276
```

Expected startup confirmation:

```text
Development ambiguous-write simulation armed for one submit-picks request. Configured QA player, league, and slate restrictions are active.
```

Sign in as `manual-player3@example.test`, confirm Picks displays Week 14, and assign one participating winner to each of the ten games with confidence ranks 1–10 used exactly once. Save the draft, refresh, and confirm it restored. Review the complete saved picks, then click **Confirm submission exactly once**. In Network, expect one `POST /bff/api/v1/slates/f46b4039-5ed6-d48b-13b8-342a77590cba/submit` with HTTP 503. React should make a read-only `GET .../picks/me`, recognize the authoritative Submitted sheet, show the submission confirmation, and render all picks read-only. Verify all ten picks and ranks through the authorized GET and read-only database verification; there must be no second POST.

Expected one-shot consumption log:

```text
Development ambiguous-write simulation consumed after successful upstream SubmitPicks for the configured QA player and slate.
```

Only after capturing that evidence, stop Player and restart it disarmed, omitting every `SATURDAY_SCOUT_DEV_AMBIGUOUS_WRITE*` variable:

```sh
env ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/SaturdayScout.Player --no-launch-profile --urls https://127.0.0.1:7276
```

The operator reports that all ten picks and unique confidence ranks were saved before submission; exactly one submission POST was issued; the API committed it; the BFF returned HTTP 503; the fault-consumption log appeared; and React reconciled to Submitted. All ten picks and ranks persisted after refresh, the sheet was read-only, and no duplicate POST occurred. The operator then restarted Player with the fault-injection variables unset; the armed startup message no longer appeared.

## Scenario C preparation verification

- Full solution build: passed with 0 warnings and 0 errors.
- Full .NET solution tests: 117 passed, 0 failed, 0 skipped (Domain 11, Infrastructure 10, API integration 12, Admin integration 26, Player 58).
- React tests: 71 passed, 0 failed across 6 files; TypeScript and production build passed.
- PostgreSQL-backed Infrastructure, API, and Admin tests used uniquely named disposable databases. A post-test check found no leftover test databases.
- The operator's manual Scenario C submission is separate from these automated results.
- No migrations were created or applied.

The test does not bypass antiforgery, Player authorization, league membership, domain validation, slate locking, concurrency checks, or submitted-sheet immutability.
