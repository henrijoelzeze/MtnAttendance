# MTN Attendance — security review (2026-10-22)

## Scope
Reviewed the direct NuGet dependencies in `MtnAttendance.csproj`, updated supported stable packages, configured NuGet Audit to include **direct and transitive** dependencies, and added an automated runtime Docker image vulnerability scan. This is a dependency/vulnerability review, **not** a penetration test, an LDAP integration test, or a guarantee of zero vulnerabilities.

## Native Linux LDAPS dependency
The Dockerfile installs the Debian `libgssapi-krb5-2` package alongside `libldap2` and trusted CA certificates. The CI workflow now builds the image and verifies that `libgssapi_krb5.so.2` exists inside the **runtime image**, which is stronger evidence than merely compiling the project. Rebuild the image in Komodo; restarting an older image does not add the library.

## Package upgrades
| NuGet package | Prior | Updated |
| --- | --- | --- |
| Microsoft.FluentUI.AspNetCore.Components | 4.14.1 | 4.14.5 |
| Microsoft.FluentUI.AspNetCore.Components.Icons | 4.14.1 | 4.14.5 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | 10.0.3 |
| Radzen.Blazor | 10.4.4 | 11.2.2 |
| Microsoft.EntityFrameworkCore.Design | 10.0.0 | 10.0.13 |
| ClosedXML | 0.104.2 | 0.105.1 |
| QRCoder | 1.6.0 | 1.8.0 |
| System.DirectoryServices.Protocols | 10.0.0 | 10.0.13 |

The package upgrades are intended to reduce unpatched dependency exposure, not to assert that the old versions each have a known vulnerability. UI/library major and minor version updates require functional regression tests.

## Continuous checks
- `Directory.Build.props` enables `NuGetAudit=true`, `NuGetAuditMode=all`, and `NuGetAuditLevel=low`.
- In CI, warnings `NU1900` through `NU1905` are treated as errors, including inability to retrieve advisory data.
- CI lists known vulnerable packages including transitive dependencies.
- CI builds a freshly pulled .NET 10 image and scans OS and application library dependencies using Trivy. Findings at any reported severity (including low and moderate) now fail the CI job. Check the job logs for the detected vulnerabilities and tool/database versions.
- Dependabot monitors NuGet, Docker and GitHub Actions dependencies weekly (once its configuration is merged to the default branch).

## Unresolved security risks and validation gaps
1. **Credentials exposure:** The LDAP bind password appeared in a chat, and an earlier PostgreSQL password was in public Git history. Both must be rotated. Editing the latest tracked file does not erase Git history.
2. **LDAPS integration:** Real production/DR endpoint availability, TLS CA trust, employee-number attribute and authoritative HR group/department mapping require IT confirmation. The implementation authenticates passwords via LDAPS; it is *not* Entra ID SSO/MFA.
3. **Public check-in:** QR check-in still relies on an entered matricule, which does not establish the physical attendee's identity. Add stronger proof if needed by the threat model.
4. **Access control:** Test directory-based HR authorizations including non-HR denial, disabled users, group changes, session cookie lifetime and Blazor interactive circuits.
5. **Operations:** Perform external TLS scans, environment secret-management review, PostgreSQL privilege review and log redaction; a clean dependency scan cannot establish safety of infrastructure or business logic.
6. **Final runtime:** The GitHub Actions image scan covers the CI-built image only. Verify the final green scan result and rebuild the identical reviewed branch in Komodo before deployment. Rebuild and redeploy only after review.

**Release gate:** Do not deploy unreviewed updates to production. Require successful CI, LDAP functional tests, secret rotation and corporate security review.
