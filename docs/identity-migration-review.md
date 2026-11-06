# Identity migration execution review (local development)

Generated idempotent SQL: [identity-migration-review.sql](identity-migration-review.sql). Generated from the `ScoutIdentityDbContext` EF Core model without connecting to PostgreSQL. The migrations are `20261101165419_IdentityFoundation` and `20261101165901_IdentityRoleSeed`.

**Execution status (2026-11-01):** Both migrations were applied to the local `saturday_scout` development database after explicit approval and a successful disposable-database backup restore. No production database was changed.

## SQL inspection

- `Up` creates schema `auth`, its separate `__EFMigrationsHistory`, `AccountInvitations`, and the seven standard ASP.NET Core Identity tables. The second migration inserts the three fixed roles. It does not create public registration or any administrator credential.
- The only references to existing application data are new **restrictive** foreign keys from `auth."AspNetUsers"."Id"` and optional `auth."AccountInvitations"."IssuedByUserId"` to `public."Users"."Id"`. Identity user IDs therefore must match existing domain user IDs. No `public` table, column, or existing foreign key is changed.
- Searches of the generated `Up` SQL found no `DROP`, `RENAME`, or `ALTER TABLE` statements. `Down` methods delete the seeded roles and drop newly created `auth` tables; they are **not** a production rollback plan once accounts exist.
- Pre-migration read-only inspection of the local database found 682 teams, 56 games, and 0 users, leagues, memberships, pick sheets, or picks. No migration has been applied, so the imported rows remain untouched. Recheck these counts in the target database immediately before and after deployment.

## Backup, execution, and rollback procedure

A read-only `pg_dump -Fc` backup was taken on 2026-11-01 to `/private/tmp/saturday-scout-identity-preflight.dump` with mode `0600`. `pg_restore -l` read its 68 table-of-contents entries, including `Teams`, `Games`, `Users`, user-related foreign keys, and table data. SHA-256: `a2e3d7def2a746113a64ef4a25c3088f7607e334df1f1edc3105910a6626464d`. This verifies archive creation and readability, **not** that a full restore succeeds.

Before applying a migration, the operator must take a fresh full `pg_dump -Fc` backup of `saturday_scout` to a protected path outside the repository, record its checksum, and verify the archive with `pg_restore -l`. Restore it to an **isolated disposable database** and verify the domain table counts and FK constraints before authorizing the live migration. Do not put a dump, password, or connection string in source control. Stop application writes for the migration window and retain the backup until post-deployment checks pass.

The approved local execution used the equivalent of the following command, with `SATURDAY_SCOUT_DB_CONNECTION` supplied through the operator environment:

```sh
dotnet ef database update 20261101165901_IdentityRoleSeed --context ScoutIdentityDbContext --project src/SaturdayScout.Infrastructure --startup-project src/SaturdayScout.Api
```

Never use `EnsureCreated` or run migrations on API startup. Post-execution verification found both `auth` migration IDs, nine `auth` tables, three seeded roles, the required user link FK, and unchanged counts: 682 teams, 56 games, 3 provider ledger rows, and zero users, leagues, memberships, pick sheets, picks, Identity users, or invitations. All 13 pre-existing public foreign keys remain.

For this execution, a fresh 54 KB backup at `/private/tmp/saturday-scout-identity-approved-preflight.dump` was readable by `pg_restore` and was restored with `--exit-on-error --single-transaction` into a separate disposable database. That database had 11 expected public tables, 13 foreign keys, 682 teams, 56 games, 3 ledger rows, and the two original domain migration records. It was dropped after verification. The backup file has mode `0600`; SHA-256: `082f227c17b9d1a543b389e292617e899ce94142bedd025d0d8aa137e3869212`.

If deployment fails before any accounts are provisioned, stop the new application and restore the verified backup in an isolated recovery operation, then cut traffic back to the previous application. If new accounts or invitations exist, do **not** run `Down` or drop `auth`; preserve those rows and plan a forward fix or reviewed point-in-time restore. A prior application binary can be redeployed only after checking its compatibility with the additive schema.

## Risks and limits

- The new FK guarantees that every Identity user has a domain user with the same GUID; it does not force every existing domain user to have an Identity row. The application must reject unlinked domain-only users in Production. Any user data in another deployment requires ownership review before account linking.
- The two-context provisioning transaction and actual migrations passed against disposable PostgreSQL test databases. The full build passed with zero warnings/errors; the full test suite passed 20, failed 0, skipped 0 with `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION` set for isolated databases.
- Sprint 1B subsequently added shared Data Protection key storage, cookie login, antiforgery, rate limits, MFA enrollment, and a minimal Blazor portal without changing this schema. Invitation issuing, email delivery, password reset, privileged audit persistence, administrator API routes, and production key protection remain before Production administration can open.
