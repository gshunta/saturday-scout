# Azure App Service deployment

**Status: documentation only — no production resources have been provisioned yet.** This covers hosting `SaturdayScout.Api` and `SaturdayScout.Admin` on a single Basic `B1` Linux App Service Plan, the compute half of the ~$29/month total documented in [`database.md`](database.md#tier-and-cost). Provision the database first (`database.md`); the steps here need its server name to build a connection string, and the database's firewall rules need this App Service's outbound IPs.

Reuse the same resource group and location as the database:

```bash
export SATURDAY_SCOUT_AZ_RESOURCE_GROUP=saturday-scout-prod
export SATURDAY_SCOUT_AZ_LOCATION=eastus
```

## Create the plan and the two apps

One Basic plan hosts both sites — App Service bills the plan, not each app on it:

```bash
az appservice plan create \
  --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-plan \
  --sku B1 --is-linux

az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-api --runtime "DOTNETCORE:10.0"
az webapp create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --plan saturday-scout-plan \
  --name saturday-scout-admin --runtime "DOTNETCORE:10.0"

az webapp config set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api --always-on true
az webapp config set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin --always-on true
```

Run `az webapp list-runtimes --os linux` first and confirm a `DOTNETCORE:10.0` entry exists. If the built-in stack isn't published yet, deploy both apps as Linux containers from a Dockerfile instead — same plan, same cost, no dependency on when Azure publishes the stack. `--always-on true` is required on both apps: without it, App Service idles the process after 20 minutes with no requests, which would silently break the Admin portal's background MFA/session revalidation and any scheduled work added later.

## Hand off the outbound IPs to the database firewall

The Postgres server rejects all traffic until explicit firewall rules admit these apps. Capture the IPs here and add the rules in `database.md`:

```bash
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api \
  --query outboundIpAddresses --output tsv
az webapp show --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --query outboundIpAddresses --output tsv
```

Follow [`database.md`](database.md#allow-the-right-traffic-through-the-postgres-firewall) to add one `az postgres flexible-server firewall-rule create` per IP returned above. Outbound IPs on a Basic-tier plan are shared infrastructure and can change if the plan's tier changes — re-run this and update the firewall rules after any such change.

## Configure the apps

Never commit any of these values. Set them directly as App Service settings, using the server name and password chosen in `database.md`:

```bash
export SATURDAY_SCOUT_DB_SERVER=saturday-scout-db                        # must match database.md
export SATURDAY_SCOUT_DB_PASSWORD='the-password-chosen-in-database.md'   # set in your shell only; never commit it
export SATURDAY_SCOUT_DB_CONNECTION="Host=${SATURDAY_SCOUT_DB_SERVER}.postgres.database.azure.com;Port=5432;Database=saturday_scout;Username=saturday_scout_admin;Password=${SATURDAY_SCOUT_DB_PASSWORD};Ssl Mode=Require"

for app in saturday-scout-api saturday-scout-admin; do
  az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name "$app" --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    WEBSITES_ENABLE_APP_SERVICE_STORAGE=true \
    SATURDAY_SCOUT_DB_CONNECTION="$SATURDAY_SCOUT_DB_CONNECTION" \
    SATURDAY_SCOUT_DP_KEYS_PATH=/home/dpkeys
done
az webapp config appsettings set --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --settings SATURDAY_SCOUT_PUBLIC_ORIGIN=https://your-chosen-domain
```

`Ssl Mode=Require` is mandatory — Flexible Server rejects unencrypted connections. `saturday-scout-admin` additionally requires `SATURDAY_SCOUT_PUBLIC_ORIGIN` as an HTTPS origin, per the startup check in `src/SaturdayScout.Admin/Program.cs`; set it once a custom domain and managed certificate are attached to that app (`az webapp config hostname add` / `az webapp config ssl create --certificate-source Managed`), which isn't covered here.

### Data Protection keys

`SATURDAY_SCOUT_DP_KEYS_PATH` expects a filesystem directory, which is why both apps point at `/home/dpkeys` above: on App Service, `/home` is backed by an Azure Files share that's automatically provisioned per plan, persists across restarts, and is shared across every instance of every app on that plan — so both sites read and write the same key ring with no separate storage account needed for this single B1 plan. `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true` is the default for the native runtime stack above but is **not** the default if you fall back to a Linux container deployment, so it's set explicitly either way. Confirm the mapping is active after the first deploy (write a file under `/home` via the Kudu/SSH console, restart the app, and check it persisted) before relying on it for Data Protection keys — both apps must successfully validate the shared Identity cookie against the same key ring, or sign-in will silently break on whichever app didn't get a matching key.

If either app later moves to its own plan, one shared Azure Files share replaces this default mapping:

```bash
az storage account create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --name saturdayscoutdpkeys --sku Standard_LRS --kind StorageV2
az storage share-rm create --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --storage-account saturdayscoutdpkeys --name dpkeys --quota 1
az storage account keys list --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" \
  --account-name saturdayscoutdpkeys --query "[0].value" --output tsv

for app in saturday-scout-api saturday-scout-admin; do
  az webapp config storage-account add --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name "$app" \
    --custom-id dpkeys --storage-type AzureFiles --account-name saturdayscoutdpkeys \
    --share-name dpkeys --access-key <key-from-previous-command> --mount-path /mnt/dpkeys
done
```

Repoint `SATURDAY_SCOUT_DP_KEYS_PATH` to `/mnt/dpkeys` on both apps if you switch to this. A storage account of this size adds well under $1/month.

## Deploy the code

No CI pipeline exists yet (a gap already flagged in the architecture review — land one before relying on this manually). Until then, publish and zip-deploy each app from a trusted machine:

```bash
dotnet publish src/SaturdayScout.Api -c Release -o ./publish/api
(cd ./publish/api && zip -r ../api.zip .)
az webapp deploy --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-api \
  --src-path ./publish/api.zip --type zip

dotnet publish src/SaturdayScout.Admin -c Release -o ./publish/admin
(cd ./publish/admin && zip -r ../admin.zip .)
az webapp deploy --resource-group "$SATURDAY_SCOUT_AZ_RESOURCE_GROUP" --name saturday-scout-admin \
  --src-path ./publish/admin.zip --type zip
```

Apply pending migrations (`database.md`) before deploying code that depends on them, and never let the application apply migrations itself at startup. After the first deploy, confirm both apps start cleanly with `az webapp log tail` — the Admin app fails fast at startup if `SATURDAY_SCOUT_DB_CONNECTION`, `SATURDAY_SCOUT_DP_KEYS_PATH`, or `SATURDAY_SCOUT_PUBLIC_ORIGIN` is missing or invalid outside Development, so a clean failure here means a missing setting, not a code bug.
