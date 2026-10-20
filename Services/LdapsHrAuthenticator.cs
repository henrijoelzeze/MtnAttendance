using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;
using MtnAttendance.Domain;

namespace MtnAttendance.Services;

public enum DirectoryAuthenticationResult { Success, InvalidCredentials, Unavailable }

public interface IHrDirectoryAuthenticator
{
    Task<DirectoryAuthenticationResult> VerifyAsync(string personNumber, string password,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Searches the directory with a read-only service bind, then validates the user's
/// own password with a distinct certificate-validated LDAPS bind.
/// Directory passwords and distinguished names are never persisted or logged.
/// </summary>
public sealed class LdapsHrAuthenticator(
    IOptions<LdapsOptions> options,
    ILogger<LdapsHrAuthenticator> logger) : IHrDirectoryAuthenticator
{
    private readonly LdapsOptions _options = options.Value;

    public Task<DirectoryAuthenticationResult> VerifyAsync(string personNumber, string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(personNumber) || string.IsNullOrEmpty(password))
            return Task.FromResult(DirectoryAuthenticationResult.InvalidCredentials);

        // The LDAP client is synchronous and timeout-limited; cancellation cannot
        // interrupt an already-running native LDAP operation.
        return Task.Run(() => Verify(personNumber, password), cancellationToken);
    }

    private DirectoryAuthenticationResult Verify(string personNumber, string password)
    {
        var hosts = new[] { _options.PrimaryHost, _options.DisasterRecoveryHost }
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var host in hosts)
        {
            try
            {
                using var service = Connect(host!, _options.BindUsername, _options.BindPassword);
                service.Bind();

                // LDAP filter escaping prevents injection.
                // Match raw roster value or its canonical person-number variant.
                var original = EscapeFilter(personNumber.Trim());
                var normalized = EscapeFilter(PersonNumber.Normalize(personNumber));
                var filter = original == normalized
                    ? $"({_options.EmployeeNumberAttribute}={original})"
                    : $"(|({_options.EmployeeNumberAttribute}={original})({_options.EmployeeNumberAttribute}={normalized}))";

                var search = new SearchRequest(_options.UserSearchBase, filter,
                    SearchScope.Subtree, _options.EmployeeNumberAttribute)
                {
                    SizeLimit = 2,
                    TimeLimit = TimeSpan.FromSeconds(8)
                };

                var response = (SearchResponse)service.SendRequest(search);
                if (response.Entries.Count != 1)
                    return DirectoryAuthenticationResult.InvalidCredentials;

                var userDn = response.Entries[0].DistinguishedName;
                if (string.IsNullOrWhiteSpace(userDn))
                    return DirectoryAuthenticationResult.InvalidCredentials;

                using var employee = Connect(host!, userDn, password);
                try
                {
                    employee.Bind();
                    return DirectoryAuthenticationResult.Success;
                }
                catch (LdapException ex) when (ex.ErrorCode == 49)
                {
                    return DirectoryAuthenticationResult.InvalidCredentials;
                }
            }
            catch (LdapException ex) when (ex.ErrorCode == 49)
            {
                logger.LogWarning("LDAPS service bind denied; check service-account provisioning.");
                return DirectoryAuthenticationResult.Unavailable;
            }
            catch (LdapException ex)
            {
                // Retry the DR server on transport/service failure only.
                logger.LogWarning("LDAPS endpoint unavailable (LDAP error {Code}).", ex.ErrorCode);
            }
            catch (DirectoryOperationException)
            {
                logger.LogWarning("LDAPS directory search failed; refusing authentication.");
                return DirectoryAuthenticationResult.Unavailable;
            }
        }

        return DirectoryAuthenticationResult.Unavailable;
    }

    private LdapConnection Connect(string host, string username, string password)
    {
        var endpoint = new LdapDirectoryIdentifier(host, _options.Port);
        var connection = new LdapConnection(endpoint,
            new NetworkCredential(username, password), AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;
        // Never bypass TLS certificate/hostname validation or fall back to plaintext LDAP.
        return connection;
    }

    private static string EscapeFilter(string value) => value
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);
}