# HR-only LDAPS authentication

This feature authenticates HR staff using direct LDAPS directory password verification, **not** Microsoft Entra ID / OpenID Connect. Public employee QR check-in at `/c/{QrToken}` is unchanged.

## Authorization

Successful login requires a unique active employee in the approved local roster, subdivision `Human Resources`, enabled HR account, exactly one matching LDAP user, and a successful user-password bind. Access is rechecked when the cookie is validated. The old HR-local password and reset functionality is removed from the UI; the legacy database column is retained for compatibility.

## Protected configuration

Use environment variables or approved secrets management, **never source control**, for:

| Setting | Usage |
| --- | --- |
| `Ldaps__PrimaryHost` | Production hostname |
| `Ldaps__DisasterRecoveryHost` | Failover hostname |
| `Ldaps__Port` | Corporate LDAPS port |
| `Ldaps__UserSearchBase` | User subtree DN |
| `Ldaps__GroupSearchBase` | Informational/future group policy; not currently checked |
| `Ldaps__BindUsername` | Read-only service account bind identity |
| `Ldaps__BindPassword` | **Rotated** service-account password |
| `Ldaps__EmployeeNumberAttribute` | Employee-number LDAP attribute; defaults to `employeeNumber`, must be confirmed |
| `ConnectionStrings__Default` | PostgreSQL connection string with **rotated** password |

Directory server details and credentials supplied earlier are deliberately **not** stored in this public GitHub repository. The PostgreSQL password previously exposed in Git history must also be rotated; removing it from the current configuration does not remove historical exposure.

## Deployment and validation

1. Confirm bind DN/username format and the LDAP attribute matching HR matricules with the directory team. The group-search base alone does not establish an HR group authorization rule.
2. Provide TLS trust chain and corporate DNS/network access to primary and DR servers. Never bypass certificate or hostname validation.
3. Provide runtime secrets; do not put them in `appsettings.json`.
4. Install .NET 10 and run `dotnet restore` and `dotnet build`. No build or directory integration test has been performed in this environment.
5. Test valid HR sign-in, incorrect password, unknown employee, non-HR user, disabled account, duplicate directory matches, primary-server outage and DR failover, plus public QR check-in.
6. Test login throttling behind your actual reverse proxy. The current rate limiter uses source IP; shared NAT/proxy IP addresses can cause collective throttling.
7. Consider CSRF-protected POST logout, immediate authorization revalidation in long-running Blazor circuits, and automated tests before production deployment.

**Security incident:** Rotate both the directory bind account password previously shared in chat and the PostgreSQL database password previously committed publicly. Investigate prior exposure and access logs.

Note: the public employee QR check-in still relies on employee-number entry and does not prove the physical identity of the attendee.
