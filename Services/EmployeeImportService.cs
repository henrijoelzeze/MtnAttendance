using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using MtnAttendance.Data;
using MtnAttendance.Domain;

namespace MtnAttendance.Services;

public class ImportReport
{
    public int RowsRead { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }
    public int HrAccountsCreated { get; set; }
    public int HrAccountsAutoDisabled { get; set; }
    public List<string> Warnings { get; } = [];
    public bool Failed { get; set; }
    public string? Error { get; set; }
}

public class EmployeeImportService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
{
    // Colonnes attendues, tolérantes à la casse et aux espaces.
    private static readonly string[] Required = ["person number", "first name", "last name", "subdivision"];

    /// <summary>Ligne du fichier déjà validée : prête à être projetée sur un Employee.</summary>
    private sealed record EmployeeRow(
        string Key,
        string RawNumber,
        string FirstName,
        string? MiddleName,
        string LastName,
        string? Gender,
        string? Phone,
        string? Email,
        string? Position,
        string Subdivision);

    /// <summary>Dernier import, pour la carte « Last Sync Results ».</summary>
    public async Task<ImportRun?> GetLastRunAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ImportRuns.AsNoTracking().OrderByDescending(r => r.RunAt).FirstOrDefaultAsync(ct);
    }

    public async Task<int> GetActiveHeadcountAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Employees.CountAsync(e => e.IsActive, ct);
    }

    public async Task<ImportReport> ImportAsync(Stream excel, int runByEmployeeId, CancellationToken ct = default)
    {
        var report = new ImportReport();
        var now = clock.GetLocalNow().DateTime;

        XLWorkbook workbook;
        IXLWorksheet sheet;

        try
        {
            workbook = new XLWorkbook(excel);
            sheet = workbook.Worksheets.First();
        }
        catch (Exception ex)
        {
            report.Failed = true;
            report.Error = $"Fichier illisible : {ex.Message}";
            return report;
        }

        using (workbook)
        {
            var cols = TryBuildColumnMap(sheet, report);
            if (cols is null) return report;

            await SynchronizeAsync(sheet, cols, runByEmployeeId, now, report, ct);
        }

        return report;
    }

    /// <summary>En-tête : renvoie null et remplit le rapport si le fichier est inexploitable.</summary>
    private static Dictionary<string, int>? TryBuildColumnMap(IXLWorksheet sheet, ImportReport report)
    {
        var headerRow = sheet.FirstRowUsed();
        if (headerRow is null)
        {
            report.Failed = true;
            report.Error = "Le fichier est vide.";
            return null;
        }

        var cols = headerRow.Cells()
            .Where(c => !string.IsNullOrWhiteSpace(c.GetString()))
            .GroupBy(c => c.GetString().Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Address.ColumnNumber);

        var missing = Required.Where(r => !cols.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            report.Failed = true;
            report.Error = $"Colonnes manquantes : {string.Join(", ", missing)}";
            return null;
        }

        return cols;
    }

    /// <summary>Une seule transaction : insertions/mises à jour, désactivations, puis journal d'import.</summary>
    private async Task SynchronizeAsync(
        IXLWorksheet sheet,
        Dictionary<string, int> cols,
        int runByEmployeeId,
        DateTime now,
        ImportReport report,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.Employees
            .Include(e => e.HrAccount)
            .ToDictionaryAsync(e => e.PersonNumberKey, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            report.RowsRead++;

            var parsed = TryParseRow(row, cols, report);
            if (parsed is null) continue;

            if (!seen.Add(parsed.Key))
            {
                report.Warnings.Add(
                    $"Ligne {row.RowNumber()} : matricule {parsed.RawNumber} en doublon dans le fichier — ignorée.");
                continue;
            }

            var employee = GetOrCreate(db, existing, parsed.Key, report);
            Apply(employee, parsed, now);
            SyncHrAccess(employee, now, report);
        }

        // A partial or invalid roster must not silently revoke employee and HR access.
        if (report.RowsRead == 0 || report.Warnings.Count > 0 ||
            (existing.Count > 0 && seen.Count < Math.Ceiling(existing.Count * 0.8)))
        {
            report.Failed = true;
            report.Error = "Import refused: empty, incomplete or invalid roster. No changes were saved.";
            await tx.RollbackAsync(ct);
            return;
        }

        DeactivateMissing(existing, seen, report);

        db.ImportRuns.Add(BuildRun(runByEmployeeId, now, report));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Validation d'une ligne. Renvoie null (avec avertissement) au lieu de planter.</summary>
    private static EmployeeRow? TryParseRow(IXLRow row, Dictionary<string, int> cols, ImportReport report)
    {
        var line = row.RowNumber();

        string Cell(string col) =>
            cols.TryGetValue(col, out var i) ? row.Cell(i).GetString().Trim() : string.Empty;

        var rawNumber = Cell("person number");
        var key = PersonNumber.Normalize(rawNumber);
        var firstName = Cell("first name");
        var lastName = Cell("last name");
        var subdivision = Cell("subdivision");

        if (key.Length == 0)
        {
            report.Warnings.Add($"Ligne {line} : Numéro de Matricule vide — ignorée.");
            return null;
        }
        if (firstName.Length == 0 || lastName.Length == 0)
        {
            report.Warnings.Add($"Ligne {line} ({rawNumber}) : nom ou prénom manquant — ignorée.");
            return null;
        }
        if (subdivision.Length == 0)
        {
            report.Warnings.Add($"Ligne {line} ({rawNumber}) : subdivision manquante — ignorée.");
            return null;
        }

        return new EmployeeRow(
            key,
            rawNumber,
            firstName,
            NullIfEmpty(Cell("middle name")),
            lastName,
            NullIfEmpty(Cell("gender")),
            NullIfEmpty(Cell("phone")),
            NullIfEmpty(Cell("primary email")),
            NullIfEmpty(Cell("position")),
            subdivision);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static Employee GetOrCreate(
        AppDbContext db,
        Dictionary<string, Employee> existing,
        string key,
        ImportReport report)
    {
        if (existing.TryGetValue(key, out var employee))
        {
            report.Updated++;
            return employee;
        }

        employee = new Employee { PersonNumberKey = key };
        db.Employees.Add(employee);
        existing[key] = employee;
        report.Inserted++;
        return employee;
    }

    private static void Apply(Employee employee, EmployeeRow row, DateTime now)
    {
        employee.PersonNumber = row.RawNumber;
        employee.FirstName = row.FirstName;
        employee.MiddleName = row.MiddleName;
        employee.LastName = row.LastName;
        employee.Gender = row.Gender;
        employee.Phone = row.Phone;
        employee.Email = row.Email;
        employee.Position = row.Position;
        employee.Subdivision = row.Subdivision;
        employee.IsActive = true;
        employee.LastSeenInImportAt = now;
    }

    /// <summary>Absents du fichier => sortis de l'entreprise : désactivation + coupure d'accès RH.</summary>
    private static void DeactivateMissing(
        Dictionary<string, Employee> existing,
        HashSet<string> seen,
        ImportReport report)
    {
        foreach (var employee in existing.Values.Where(x => x.IsActive && !seen.Contains(x.PersonNumberKey)))
        {
            employee.IsActive = false;
            report.Deactivated++;

            if (employee.HrAccount is { IsEnabled: true } account)
            {
                account.IsEnabled = false;
                report.HrAccountsAutoDisabled++;
            }
        }
    }

    private static ImportRun BuildRun(int runByEmployeeId, DateTime now, ImportReport report) => new()
    {
        RunAt = now,
        RunByEmployeeId = runByEmployeeId,
        RowsRead = report.RowsRead,
        Inserted = report.Inserted,
        Updated = report.Updated,
        Deactivated = report.Deactivated,
        HrAccountsCreated = report.HrAccountsCreated,
        HrAccountsDisabled = report.HrAccountsAutoDisabled,
        Warnings = report.Warnings.Count == 0
            ? null
            : string.Join('\n', report.Warnings).Truncate(4000)
    };

    /// <summary>Recalcule l'accès back-office à chaque import, dans les deux sens.</summary>
    private static void SyncHrAccess(Employee employee, DateTime now, ImportReport report)
    {
        var isHr = string.Equals(employee.Subdivision.Trim(), HrAccount.HrSubdivision,
            StringComparison.OrdinalIgnoreCase);

        if (isHr)
        {
            if (employee.HrAccount is null)
            {
                // Rattaché à l'employé suivi : EF détecte le nouvel HrAccount au SaveChanges.
                employee.HrAccount = new HrAccount { CreatedAt = now, IsEnabled = true };
                report.HrAccountsCreated++;
            }
            else if (!employee.HrAccount.IsEnabled)
            {
                // Retour dans la subdivision RH : on réactive sans toucher au mot de passe.
                employee.HrAccount.IsEnabled = true;
            }
        }
        else if (employee.HrAccount is { IsEnabled: true } account)
        {
            account.IsEnabled = false;
            report.HrAccountsAutoDisabled++;
        }
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..max];
}