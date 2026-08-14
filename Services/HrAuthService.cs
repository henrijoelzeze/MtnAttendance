using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MtnAttendance.Data;
using MtnAttendance.Domain;

namespace MtnAttendance.Services;

public static class HrAuth
{
    public const string Scheme = "MtnAttendance.Hr";
    public const string EmployeeIdClaim = "employee_id";
    public const string PersonNumberClaim = "person_number";

    public static int EmployeeId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(EmployeeIdClaim), out var id) ? id : 0;
}

public enum LoginStep
{
    /// <summary>Matricule inconnu ou employé inactif.</summary>
    Denied,
    /// <summary>Employé valide mais pas dans la subdivision Human Resources.</summary>
    NotHr,
    /// <summary>Accès RH désactivé : doit demander une réinitialisation à un collègue.</summary>
    Disabled,
    /// <summary>Première connexion : écran « Créer un mot de passe ».</summary>
    CreatePassword,
    /// <summary>Écran mot de passe classique.</summary>
    EnterPassword
}

public record LoginResolution(LoginStep Step, int EmployeeId = 0, string FirstName = "", string FullName = "");

public record HrColleague(int EmployeeId, string PersonNumber, string FullName, bool HasPassword, bool IsEnabled, DateTime? LastLoginAt);

public class HrAuthService(IDbContextFactory<AppDbContext> factory, IPasswordHasher<HrAccount> hasher, TimeProvider clock)
{
    /// <summary>Étape 1 : le matricule seul détermine l'écran suivant.</summary>
    public async Task<LoginResolution> ResolveAsync(string rawPersonNumber, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = PersonNumber.Normalize(rawPersonNumber);
        if (key.Length == 0) return new(LoginStep.Denied);

        var employee = await db.Employees
            .Include(e => e.HrAccount)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.PersonNumberKey == key, ct);

        if (employee is null || !employee.IsActive) return new(LoginStep.Denied);

        var isHr = string.Equals(employee.Subdivision?.Trim(), HrAccount.HrSubdivision,
            StringComparison.OrdinalIgnoreCase);
        if (!isHr || employee.HrAccount is null) return new(LoginStep.NotHr);

        if (!employee.HrAccount.IsEnabled)
            return new(LoginStep.Disabled, employee.Id, employee.FirstName, employee.FullName);

        var step = employee.HrAccount.HasPassword ? LoginStep.EnterPassword : LoginStep.CreatePassword;
        return new(step, employee.Id, employee.FirstName, employee.FullName);
    }

    /// <summary>Définition du mot de passe à la première connexion.</summary>
    public async Task<bool> SetPasswordAsync(int employeeId, string password, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var account = await db.HrAccounts.FirstOrDefaultAsync(a => a.EmployeeId == employeeId, ct);
        if (account is null || !account.IsEnabled || account.HasPassword) return false;

        account.PasswordHash = hasher.HashPassword(account, password);
        account.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> VerifyPasswordAsync(int employeeId, string password, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var account = await db.HrAccounts.FirstOrDefaultAsync(a => a.EmployeeId == employeeId, ct);
        if (account is null || !account.IsEnabled || !account.HasPassword) return false;

        var result = hasher.VerifyHashedPassword(account, account.PasswordHash!, password);
        if (result == PasswordVerificationResult.Failed) return false;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            account.PasswordHash = hasher.HashPassword(account, password);

        account.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Liste des collègues RH (écran Comptes RH).</summary>
    public async Task<List<HrColleague>> GetHrAccountsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.HrAccounts
            .AsNoTracking()
            .OrderBy(a => a.Employee.LastName)
            .Select(a => new HrColleague(
                a.EmployeeId,
                a.Employee.PersonNumber,
                a.Employee.FirstName + " " + a.Employee.LastName,
                a.PasswordHash != null,
                a.IsEnabled,
                a.LastLoginAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Réinitialisation par un collègue : on efface le mot de passe, la personne repasse
    /// par « Créer un mot de passe ». Impossible sur son propre compte (auto-blocage).
    /// </summary>
    public async Task<bool> ResetPasswordAsync(int actorEmployeeId, int targetEmployeeId, CancellationToken ct = default)
    {
        if (actorEmployeeId == targetEmployeeId) return false;

        await using var db = await factory.CreateDbContextAsync(ct);
        var target = await db.HrAccounts.FirstOrDefaultAsync(a => a.EmployeeId == targetEmployeeId, ct);
        if (target is null) return false;

        target.PasswordHash = null;
        target.IsEnabled = true;   // une réinitialisation débloque aussi un accès désactivé
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static ClaimsPrincipal BuildPrincipal(int employeeId, string personNumber, string fullName)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(HrAuth.EmployeeIdClaim, employeeId.ToString()),
            new Claim(HrAuth.PersonNumberClaim, personNumber),
            new Claim(ClaimTypes.Name, fullName),
            new Claim(ClaimTypes.Role, "HR")
        ], HrAuth.Scheme);

        return new ClaimsPrincipal(identity);
    }

    public async Task<(string PersonNumber, string FullName)> GetIdentityAsync(int employeeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.Employees.AsNoTracking()
            .Where(x => x.Id == employeeId)
            .Select(x => new { x.PersonNumber, x.FirstName, x.LastName })
            .FirstAsync(ct);
        return (e.PersonNumber, $"{e.FirstName} {e.LastName}");
    }
}
