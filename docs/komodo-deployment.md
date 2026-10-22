# Komodo deployment (HR LDAPS)

Use Komodo **Stack** with this repository, branch `feature/hr-ldaps-authentication`, and compose path `compose.komodo.yaml`. Komodo must build from the checked-out repository directory because `build.context: .`.

## Required secure Komodo variables

Set `POSTGRES_CONNECTION_STRING`, `LDAPS_PRIMARY_HOST`, `LDAPS_DR_HOST`, `LDAPS_USER_BASE`, `LDAPS_BIND_USERNAME`, `LDAPS_BIND_PASSWORD`, and `TRUSTED_PROXY_IP`. Optional: `LDAPS_PORT`, `LDAPS_GROUP_BASE`, `LDAPS_EMPLOYEE_NUMBER_ATTRIBUTE`, `ATTENDANCE_PORT`.

Do not use credentials leaked in chat or Git history; rotate first. Komodo and Docker may expose environment variables to Docker administrators; use approved secrets management if that does not meet your requirements. Keep Komodo's variable values outside version control.

## Infrastructure prerequisites

- Existing migrated PostgreSQL database accessible from the container. Compose deliberately does not start a new empty database or run migrations automatically.
- Container DNS + firewall connectivity to both corporate LDAPS endpoints; valid CA chain trusted by the running image.
- Reverse proxy on the same host terminating HTTPS and forwarding to `127.0.0.1:18080` (or configured port) with WebSocket support for Blazor.
- `TRUSTED_PROXY_IP` is the actual IP of the reverse proxy *as seen by the app*, not an arbitrary wildcard. For a reverse proxy on another host, replace the loopback port binding and use a private network / firewall.
- External DNS/domain certificate and reverse-proxy configuration, not provisioned by this Compose file.

No container healthcheck is configured until a dependable HTTP probe is available. Check `GET /healthz` from the host and verify DB/LDAPS workflows separately. A liveness endpoint does not mean all dependencies are ready.

## Pre-merge validation

Run `dotnet restore`, `dotnet build -c Release`, `docker compose -f compose.komodo.yaml config` with injected nonsecret placeholders, and exercise the critical workflows. The app does not start without configured directory secrets.

**Important:** This is direct LDAPS, not Entra ID. The employee QR check-in remains unauthenticated.


## Login troubleshooting

The HR login first queries the local PostgreSQL `"Employees"` and `"HrAccounts"` tables and rejects any inactive, non-HR or unprovisioned identity. A non-HR matricule returns the generic invalid-credentials/HR-access error **before LDAPS is called**. This is intentional.

In the **same database used by Komodo**, run this read-only SQL to inspect roster provisioning:

```sql
SELECT COUNT(*) AS employees FROM "Employees";
SELECT COUNT(*) AS hr_eligible
FROM "Employees" e
JOIN "HrAccounts" h ON h."EmployeeId" = e."Id"
WHERE e."IsActive" AND h."IsEnabled"
  AND lower(trim(e."Subdivision")) = 'human resources';
```

Zero HR-eligible rows means no one can sign in. Provision the authorized HR employee roster through an approved, access-controlled bootstrap/import method; **do not** disable the HR eligibility check or assign non-HR staff an HR role just to test LDAP. A correct password alone never grants HR access.

The Docker image includes `libgssapi-krb5-2`, required by some Linux LDAP/GSSAPI runtime paths. Rebuild the image (not merely restart the old container) after pulling the updated Dockerfile. Confirm `ldconfig -p | grep libgssapi_krb5` inside the image if needed.

For deployments with HTTPS terminated at a reverse proxy, `ReverseProxy__TlsTerminated=true` must be set and the container must run the updated branch/image. If `HttpsRedirectionMiddleware` still logs 'Failed to determine the https port', check the **effective** environment and image/version; do not disable HTTPS at the public reverse proxy.
