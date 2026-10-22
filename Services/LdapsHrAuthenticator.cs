using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;
using MtnAttendance.Domain;

namespace MtnAttendance.Services;

public enum DirectoryAuthenticationStatus { Success, InvalidCredentials, NotHr, Unavailable }
public enum DirectoryAuthorizationStatus { Authorized, Denied, Unavailable }

public sealed record DirectoryEmployee(
    string PersonNumber, string FirstName, string LastName, string? Email);

public sealed record DirectoryAuthenticationResult(
    DirectoryAuthenticationStatus Status, DirectoryEmployee? Employee = null);

public interface IHrDirectoryAuthenticator
{
    Task<DirectoryAuthenticationResult> AuthenticateAsync(
        string personNumber, string password, CancellationToken cancellationToken = default);
    Task<DirectoryAuthorizationStatus> HasHrAccessAsync(
        string personNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Corporate directory is the source of truth for identity and HR membership.
/// The read-only service bind locates users; a second bind authenticates their
/// password. No local employee roster is needed for initial login.
/// </summary>
public sealed class LdapsHrAuthenticator(
    IOptions<LdapsOptions> options,
    ILogger<LdapsHrAuthenticator> logger) : IHrDirectoryAuthenticator
{
    private readonly LdapsOptions _options = options.Value;

    public Task<DirectoryAuthenticationResult> AuthenticateAsync(
        string personNumber, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personNumber) || string.IsNullOrEmpty(password))
            return Task.FromResult(new DirectoryAuthenticationResult(DirectoryAuthenticationStatus.InvalidCredentials));

        // LdapConnection is synchronous and timeout-bounded. Cancellation can
        // prevent starting work but cannot interrupt an in-flight native call.
        return Task.Run(() => Authenticate(personNumber, password), cancellationToken);
    }

    public Task<DirectoryAuthorizationStatus> HasHrAccessAsync(
        string personNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personNumber))
            return Task.FromResult(DirectoryAuthorizationStatus.Denied);

        return Task.Run(() => CheckHrAccess(personNumber), cancellationToken);
    }

    private DirectoryAuthenticationResult Authenticate(string personNumber, string password)
    {
        foreach (var host in Hosts())
        {
            try
            {
                using var service = Connect(host);
                try { service.Bind(); }
                catch (LdapException e) when (e.ErrorCode == 49)
                {
                    logger.LogWarning("Corporate directory service bind rejected.");
                    return new(DirectoryAuthenticationStatus.Unavailable);
                }

                var entry = SearchEmployee(service, personNumber);
                if (entry is null)
                    return new(DirectoryAuthenticationStatus.InvalidCredentials);

                // Always validate the employee's actual password before evaluating
                // HR attributes or exposing any user-specific outcome.
                using var employee = Connect(host, entry.DistinguishedName, password);
                try { employee.Bind(); }
                catch (LdapException e) when (e.ErrorCode == 49)
                {
                    return new(DirectoryAuthenticationStatus.InvalidCredentials);
                }

                if (!IsHr(entry))
                {
                    logger.LogInformation("Directory sign-in denied: employee is not in the configured HR scope.");
                    return new(DirectoryAuthenticationStatus.NotHr);
                }

                var number = Attribute(entry, _options.EmployeeNumberAttribute);
                if (string.IsNullOrWhiteSpace(number) ||
                    PersonNumber.Normalize(number) != PersonNumber.Normalize(personNumber))
                    return new(DirectoryAuthenticationStatus.InvalidCredentials);

                var firstName = Attribute(entry, "givenName");
                var lastName = Attribute(entry, "sn");
                var displayName = Attribute(entry, "displayName") ?? number;
                return new(DirectoryAuthenticationStatus.Success,
                    new DirectoryEmployee(
                        number,
                        string.IsNullOrWhiteSpace(firstName) ? displayName : firstName,
                        string.IsNullOrWhiteSpace(lastName) ? "-" : lastName,
                        Attribute(entry, "mail")));
            }
            catch (LdapException e)
            {
                logger.LogWarning("Corporate directory endpoint failed (LDAP code {Code}).", e.ErrorCode);
            }
            catch (DirectoryOperationException)
            {
                logger.LogWarning("Corporate directory search failed.");
                return new(DirectoryAuthenticationStatus.Unavailable);
            }
        }
        return new(DirectoryAuthenticationStatus.Unavailable);
    }

    private DirectoryAuthorizationStatus CheckHrAccess(string personNumber)
    {
        foreach (var host in Hosts())
        {
            try
            {
                using var service = Connect(host);
                service.Bind();
                var entry = SearchEmployee(service, personNumber);
                return entry is not null && IsHr(entry)
                    ? DirectoryAuthorizationStatus.Authorized
                    : DirectoryAuthorizationStatus.Denied;
            }
            catch (LdapException e) when (e.ErrorCode == 49)
            {
                logger.LogWarning("Corporate directory service bind rejected during HR revalidation.");
                return DirectoryAuthorizationStatus.Unavailable;
            }
            catch (LdapException e)
            {
                logger.LogWarning("Corporate directory unavailable for HR revalidation (LDAP {Code}).", e.ErrorCode);
            }
            catch (DirectoryOperationException)
            {
                return DirectoryAuthorizationStatus.Unavailable;
            }
        }
        return DirectoryAuthorizationStatus.Unavailable;
    }

    private SearchResultEntry? SearchEmployee(LdapConnection connection, string number)
    {
        var original = EscapeFilter(number.Trim());
        var canonical = EscapeFilter(PersonNumber.Normalize(number));
        var attr = _options.EmployeeNumberAttribute;
        var filter = original == canonical
            ? $"({attr}={original})"
            : $"(|({attr}={original})({attr}={canonical}))";

        var attributes = new[]
        {
            attr, _options.HrDepartmentAttribute, "memberOf",
            "givenName", "sn", "displayName", "mail"
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var request = new SearchRequest(_options.UserSearchBase, filter,
            SearchScope.Subtree, attributes)
        {
            SizeLimit = 2, TimeLimit = TimeSpan.FromSeconds(8)
        };

        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count != 1)
            return null;

        return response.Entries[0];
    }

    private bool IsHr(SearchResultEntry entry)
    {
        // If IT supplies an explicit HR group DN, require direct group membership.
        if (!string.IsNullOrWhiteSpace(_options.HrGroupDn))
        {
            if (!entry.Attributes.Contains("memberOf")) return false;
            return entry.Attributes["memberOf"]!
                .GetValues(typeof(string)).Cast<string>()
                .Any(dn => string.Equals(dn.Trim(), _options.HrGroupDn.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        // Otherwise use the authoritative LDAP department attribute, never the
        // Excel roster's subdivision or a hard-coded employee-number allowlist.
        var department = Attribute(entry, _options.HrDepartmentAttribute);
        return string.Equals(department?.Trim(), _options.HrDepartmentValue.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? Attribute(SearchResultEntry entry, string name)
    {
        if (!entry.Attributes.Contains(name)) return null;
        var values = entry.Attributes[name]!.GetValues(typeof(string));
        return values.Length > 0 ? values.GetValue(0)?.ToString() : null;
    }

    private IEnumerable<string> Hosts() =>
        new[] { _options.PrimaryHost, _options.DisasterRecoveryHost }
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private LdapConnection Connect(string host, string? username = null, string? password = null)
    {
        var connection = new LdapConnection(
            new LdapDirectoryIdentifier(host, _options.Port),
            new NetworkCredential(username ?? _options.BindUsername, password ?? _options.BindPassword),
            AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        // Do not disable certificate validation or fall back to plaintext LDAP.
        return connection;
    }

    private static string EscapeFilter(string value) => value
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);
}
