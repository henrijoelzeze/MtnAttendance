using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
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

public sealed class HrAuthService(
    IDbContextFactory<AppDbContext> factory,
    IHrDirectoryAuthenticator directory,
    TimeProvider clock)
{
    public async Task<HrSignInResult> AuthenticateAsync(string? rawPersonNumber, string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawPersonNumber) || string.IsNullOrEmpty(password))
            return new(HrSignInStatus.InvalidCredentials);

        var key = PersonNumber.Normalize(rawPersonNumber);
        if (key.Length == 0) return new(HrSignInStatus.InvalidCredentials);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var employee = await db.Employees.Include(e => e.HrAccount)
            .SingleOrDefaultAsync(e => e.PersonNumberKey == key, cancellationToken);
        if (!IsEligible(employee)) return new(HrSignInStatus.InvalidCredentials);

        var result = await directory.VerifyAsync(employee!.PersonNumber, password, cancellationToken);
        if (result == DirectoryAuthenticationResult.InvalidCredentials)
            return new(HrSignInStatus.InvalidCredentials);
        if (result != DirectoryAuthenticationResult.Success)
            return new(HrSignInStatus.DirectoryUnavailable);

        employee.HrAccount!.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return new(HrSignInStatus.Success,
            new HrIdentity(employee.Id, employee.PersonNumber, employee.FullName));
    }

    public async Task<bool> IsAllowedAsync(int employeeId, CancellationToken cancellationToken = default)
    {
        if (employeeId <= 0) return false;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var employee = await db.Employees.Include(e => e.HrAccount).AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        return IsEligible(employee);
    }

    public async Task<List<HrColleague>> GetHrAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.HrAccounts.AsNoTracking().OrderBy(a => a.Employee.LastName)
            .Select(a => new HrColleague(a.EmployeeId, a.Employee.PersonNumber,
                a.Employee.FirstName + " " + a.Employee.LastName,
                a.IsEnabled && a.Employee.IsActive && a.Employee.Subdivision == HrAccount.HrSubdivision,
                a.LastLoginAt)).ToListAsync(cancellationToken);
    }

    private static bool IsEligible(Employee? employee) =>
        employee is { IsActive: true, HrAccount: { IsEnabled: true } } &&
        string.Equals(employee.Subdivision?.Trim(), HrAccount.HrSubdivision,
            StringComparison.OrdinalIgnoreCase);

    public static ClaimsPrincipal BuildPrincipal(HrIdentity employee) =>
        new(new ClaimsIdentity(
        [
            new Claim(HrAuth.EmployeeIdClaim, employee.EmployeeId.ToString()),
            new Claim(HrAuth.PersonNumberClaim, employee.PersonNumber),
            new Claim(ClaimTypes.Name, employee.FullName),
            new Claim(ClaimTypes.Role, "HR")
        ], HrAuth.Scheme));
}