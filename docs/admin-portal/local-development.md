# Local Admin portal development

The local PostgreSQL database must already have the two approved Identity migrations. Never run the bootstrap command as part of normal startup or a build.

1. Start the existing Saturday Scout PostgreSQL container and set `SATURDAY_SCOUT_DB_CONNECTION` in the operator shell. Follow `docs/database.md` for local connection setup; keep the password out of files and terminal output.
2. Development defaults to `SATURDAY_SCOUT_DP_PROVIDER=FileSystem`; no Azure login or credentials are needed. Set `SATURDAY_SCOUT_DP_KEYS_PATH` to an absolute directory outside the repository if you want an explicit persistent path. Use the **same path** for API and Admin (`SaturdayScout.SharedIdentity.v1`) and a separate path for Player (`SaturdayScout.Player.v1`). If unset, the hosts use separate Player and shared Admin/API directories under the system temporary directory. Automated integration hosts may use the `Testing` environment only with an explicit filesystem key path; this does not register Development authentication handlers. For local HTTPS, trust a development certificate with `dotnet dev-certs https --trust` if needed.
3. Run `dotnet run --project src/SaturdayScout.Admin --launch-profile https`. Open the HTTPS URL printed by the app. `/login` is available, but `/dashboard` requires an enrolled administrator.
4. To run the API separately, use `dotnet run --project src/SaturdayScout.Api --launch-profile https`. A browser cookie is shared with the API only when both services are behind one public origin as described in `authentication-implementation.md`; separate `localhost` ports are distinct origins and require explicit gateway routing for browser API calls.

After completing MFA, the portal provides `/dashboard`, `/games`, and `/slates`. The Admin server calls the shared application service in-process and checks the MFA-backed administrator policy for every operation; it does not depend on cross-host cookie forwarding. Draft slates require an existing league and are managed under `/slates/new`, `/slates/{id}/edit`, and `/slates/{id}`. The editor can save manual selections or randomly select and save ten eligible games; slate details validate and publish with a UTC lock deadline. The game browser only reads imported records.

To provision the **first** local administrator later, obtain explicit operator approval, set `SATURDAY_SCOUT_BOOTSTRAP_APPROVED=yes` only for that command, and run:

```sh
dotnet run --project src/SaturdayScout.Admin -- bootstrap-admin --email YOUR_OWNED_EMAIL --confirm-owned-email
```

The command prompts for a display name and password in an interactive terminal. Do not put the password in the command line, source, logs, or shell history. The operator must verify ownership of the email before using the confirmation flag. The command refuses a second first-admin bootstrap. Afterward, sign in at `/login`, enroll a TOTP authenticator at `/enroll`, and store the one-time recovery codes offline before using `/dashboard`. No administrator was provisioned during Sprint 1B implementation.

For the full suite, set `SATURDAY_SCOUT_TEST_ADMIN_CONNECTION` to an administrator connection to the local PostgreSQL server's `postgres` database and run `dotnet build -m:1 /nodeReuse:false` followed by `dotnet test -m:1 /nodeReuse:false --no-build`. Integration tests create and drop separate disposable databases. They do not reset or modify the development `saturday_scout` database.

For a non-Development deployment, configure `SATURDAY_SCOUT_DP_PROVIDER=AzureBlob`, the host-specific Blob URI, versionless Key Vault key ID, and application name. Production does not accept a filesystem key path or fall back to ephemeral keys. Admin also requires `SATURDAY_SCOUT_PUBLIC_ORIGIN` as the HTTPS origin. See `docs/deployment.md` for managed identity permissions, ring separation, rotation, and operational validation.
