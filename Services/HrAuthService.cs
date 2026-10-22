using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MtnAttendance.Data;
using MtnAttendance.Domain;

namespace MtnAttendance.Services;

public static class HrAuth
{
    public const string Scheme = "MtnAttendance.Hr";
    public const string EmployeeIdClaim = "employee_id";
    public const string PersonNumberClaim = "person_number";

    public static int EmployeeId(this ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirstValue(EmployeeIdClaim), out var id) ? id : 0;
}

public enum HrSignInStatus { Success, InvalidCredentials, DirectoryUnavailable }
public record HrIdentity(int EmployeeId, string PersonNumber, string FullName);
public record HrSignInResult(HrSignInStatus Status, HrIdentity? Identity = null);
public record HrColleague(int EmployeeId, string PersonNumber, string FullName,
    bool IsEnabled, DateTime? LastLoginAt);

/// <summary>
/// Identity and HR authorization come from LDAPS, not the imported Excel roster.
/// The local Employee/HrAccount records are created after successful authentication
/// only to support existing event, import and audit foreign keys.
/// </summary>
public sealed class HrAuthService(
    IDbContextFactory<AppDbContext> factory,
    IHrDirectoryAuthenticator directory,
    IMemoryCache cache,
    TimeProvider clock)
{
    public async Task<HrSignInResult> AuthenticateAsync(
        string? rawPersonNumber, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawPersonNumber) || string.IsNullOrEmpty(password))
            return new(HrSignInStatus.InvalidCredentials);

        var key = PersonNumber.Normalize(rawPersonNumber);
        if (key.Length == 0 || key.Length > 32)
            return new(HrSignInStatus.InvalidCredentials);

        // Authenticate against the corporate directory FIRST.
        // An empty local Employees table must not prevent HR login.
        var auth = await directory.AuthenticateAsync(rawPersonNumber, password, cancellationToken);
        if (auth.Status == DirectoryAuthenticationStatus.Unavailable)
            return new(HrSignInStatus.DirectoryUnavailable);
        if (auth.Status != DirectoryAuthenticationStatus.Success || auth.Employee is null)
            return new(HrSignInStatus.InvalidCredentials);

        if (PersonNumber.Normalize(auth.Employee.PersonNumber) != key)
            return new(HrSignInStatus.InvalidCredentials);

        var employee = await UpsertAuthenticatedEmployeeAsync(auth.Employee, cancellationToken);
        cache.Set(CacheKey(employee.PersonNumber), true, TimeSpan.FromMinutes(2));

        return new(HrSignInStatus.Success,
            new HrIdentity(employee.Id, employee.PersonNumber, employee.FullName));
    }

    public async Task<bool> IsAllowedAsync(int employeeId, CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0) return false;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var personNumber = await db.Employees.AsNoTracking()
            .Where(e => e.Id == employeeId)
            .Select(e => e.PersonNumber)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(personNumber)) return false;
        if (cache.TryGetValue(CacheKey(personNumber), out bool allowed) && allowed)
            return true;

        var authorization = await directory.HasHrAccessAsync(personNumber, cancellationToken);
        if (authorization != DirectoryAuthorizationStatus.Authorized) return false;

        cache.Set(CacheKey(personNumber), true, TimeSpan.FromMinutes(2));
        return true;
    }

    private async Task<Employee> UpsertAuthenticatedEmployeeAsync(
        DirectoryEmployee identity, CancellationToken cancellationToken)
    {
        var key = PersonNumber.Normalize(identity.PersonNumber);
        var now = clock.GetUtcNow().UtcDateTime;

        // Handle two near-simultaneous first logins through the unique matricule
        // index. A losing insert retries once against the newly inserted row.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var employee = await db.Employees.Include(e => e.HrAccount)
                .SingleOrDefaultAsync(e => e.PersonNumberKey == key, cancellationToken);
            if (employee is null)
            {
                employee = new Employee { PersonNumberKey = key };
                db.Employees.Add(employee);
            }

            employee.PersonNumber = Limit(identity.PersonNumber, 32);
            employee.FirstName = Limit(identity.FirstName, 128);
            employee.LastName = Limit(identity.LastName, 128);
            employee.Email = identity.Email is null ? null : Limit(identity.Email, 256);
            employee.Subdivision = HrAccount.HrSubdivision;
            employee.IsActive = true;
            employee.HrAccount ??= new HrAccount { CreatedAt = now };
            employee.HrAccount.IsEnabled = true;
            employee.HrAccount.LastLoginAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return employee;
            }
            catch (DbUpdateException e) when (attempt == 0 && AppDbContext.IsUniqueViolation(e))
            {
                // Concurrent first login created the row. Retry from fresh context.
            }
        }

        throw new InvalidOperationException("Could not provision authenticated HR user.");
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string CacheKey(string personNumber) =>
        "hr-ldap:" + PersonNumber.Normalize(personNumber);

    public async Task<List<HrColleague>> GetHrAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.HrAccounts.AsNoTracking()
            .OrderBy(a => a.Employee.LastName)
            .Select(a => new HrColleague(
                a.EmployeeId,
                a.Employee.PersonNumber,
                a.Employee.FirstName + " " + a.Employee.LastName,
                a.IsEnabled,
                a.LastLoginAt)).ToListAsync(cancellationToken);
    }

    public static ClaimsPrincipal BuildPrincipal(HrIdentity employee) =>
        new(new ClaimsIdentity(
        [
            new Claim(HrAuth.EmployeeIdClaim, employee.EmployeeId.ToString()),
            new Claim(HrAuth.PersonNumberClaim, employee.PersonNumber),
            new Claim(ClaimTypes.Name, employee.FullName),
            new Claim(ClaimTypes.Role, "HR")
        ], HrAuth.Scheme));
}
