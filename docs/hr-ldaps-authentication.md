# MTN Attendance — HR directory authentication

> This implementation uses **LDAPS password verification**, not Microsoft Entra ID single sign-on (OpenID Connect). To implement true Microsoft SSO, register the application in Entra ID and supply Tenant ID / Client ID, then use OIDC and Entra app roles/groups.

## Corrected login flow (no Excel roster prerequisite)

1. The user enters their matricule and corporate directory password.
2. The application connects over certificate-validated LDAPS using a read-only service identity and searches for exactly one user by the configured matricule attribute.
3. The application verifies the **user password** via a second LDAPS bind.
4. It verifies **HR membership from the LDAP entry**, not PostgreSQL:
   - If `Ldaps__HrGroupDn` is configured, the user's direct `memberOf` entries must include that exact distinguished name.
   - Otherwise, the configured `Ldaps__HrDepartmentAttribute` must equal `Ldaps__HrDepartmentValue` (default value `Human Resources`, default attribute `department`).
5. Only after both checks succeed, the app automatically creates/updates its **local** `Employee` and `HrAccount` rows to support existing event creator IDs and audit fields, then issues the HR login cookie.

**Important:** The local `Employees` table may start empty after migrations. A spreadsheet upload is **not required** for initial HR login. It is still used separately for employee attendance headcounts and QR check-in. PostgreSQL tables still need to be migrated.

**Fail closed:** A missing or mismatched HR directory attribute or group denies access. If the primary and DR directory are both unreachable, no new login is allowed. Existing cookies periodically revalidate HR membership against the directory (successful checks are cached for up to two minutes per app instance).

## Required Komodo configuration

Use the existing secret-managed variables `LDAPS_PRIMARY_HOST`, `LDAPS_DR_HOST`, `LDAPS_PORT`, `LDAPS_USER_BASE`, `LDAPS_BIND_USERNAME`, `LDAPS_BIND_PASSWORD`, `POSTGRES_CONNECTION_STRING`, and `TRUSTED_PROXY_IP`. The secret values must be rotated after disclosure.

The compose stack maps:

| Komodo variable | .NET setting | Purpose |
| --- | --- | --- |
| `LDAPS_EMPLOYEE_NUMBER_ATTRIBUTE` | `Ldaps__EmployeeNumberAttribute` | LDAP matricule attribute (default `employeeNumber`) |
| `LDAPS_HR_GROUP_DN` | `Ldaps__HrGroupDn` | Optional **specific** HR group DN; only direct `memberOf` supported |
| `LDAPS_HR_DEPARTMENT_ATTRIBUTE` | `Ldaps__HrDepartmentAttribute` | LDAP organizational attribute (default `department`) |
| `LDAPS_HR_DEPARTMENT_VALUE` | `Ldaps__HrDepartmentValue` | Required exact department value (default `Human Resources`) |
| `LDAPS_GROUP_BASE` | `Ldaps__GroupSearchBase` | Group search base supplied by IT (does not itself identify HR group) |

**The IT directory team must confirm the real matricule attribute, the exact HR field/value or a specific HR group DN, and the bind-name format.** A user-search base and a group-search base alone do not identify authorized HR personnel. If the directory uses indirect or nested groups, adjust the membership implementation after confirming its actual schema.

## Operational verification

- Rebuild the updated image from the feature branch in Komodo (a restart of the old image is not enough).
- Verify corporate DNS, both LDAPS endpoints, the CA chain, and required Linux LDAP/GSSAPI libraries.
- Test a genuine HR member and a non-HR member using their own corporate credentials. Never change someone's HR role just to test.
- For a successful HR sign-in, confirm the `Employees` row is created automatically.
- For a non-HR sign-in, confirm HTTP login form remains visible with a generic rejection and **no local account is created**.
- Test incorrect password, unknown matricule, duplicate directory matches, unreachable primary and DR.
- Verify login and logout via HTTPS reverse proxy; HR events and public QR check-in require a migrated database.
- Confirm that the app can create events after first HR login, and that the roster import can subsequently populate attendance headcounts.

## Security and limitations

- LDAPS is **not** browser SSO. The user enters their password into the application; if true corporate SSO/MFA is required, use Entra ID OIDC.
- The local HR accounts list is not the authorization source; it reflects successful directory sign-ins.
- Membership revalidation is bounded by a short cache and existing Blazor interactive circuit lifetimes should be tested in your security review.
- The service password and the previously committed PostgreSQL password must be rotated, and public Git history still contains the old PostgreSQL secret.
- No internal directory server names, credentials, or tokens are committed here.
