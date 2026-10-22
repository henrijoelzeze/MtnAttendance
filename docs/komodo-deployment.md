# Komodo deployment (HR LDAPS)

Use Komodo **Stack** with this repository, branch `feature/hr-ldaps-authentication`, and compose path `compose.komodo.yaml`. Komodo must build from the checked-out repository directory because `build.context: .`.

## Required secure Komodo variables

Set `POSTGRES_CONNECTION_STRING`, `LDAPS_PRIMARY_HOST`, `LDAPS_DR_HOST`, `LDAPS_USER_BASE`, `LDAPS_BIND_USERNAME`, `LDAPS_BIND_PASSWORD`, and `TRUSTED_PROXY_IP`. Optional: `LDAPS_PORT`, `LDAPS_GROUP_BASE`, `LDAPS_EMPLOYEE_NUMBER_ATTRIBUTE`, `LDAPS_HR_GROUP_DN`, `LDAPS_HR_DEPARTMENT_ATTRIBUTE`, `LDAPS_HR_DEPARTMENT_VALUE`, `ATTENDANCE_PORT`.

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
