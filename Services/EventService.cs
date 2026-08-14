using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtnAttendance.Data;
using MtnAttendance.Domain;
using QRCoder;

namespace MtnAttendance.Services;

public record EventListItem(int Id, string Name, string? Location, DateTime StartsAt, DateTime EndsAt,
    EventStatus Status, int CheckedIn);

/// <summary>
/// Champs saisis dans le formulaire, communs à la création et à la modification.
/// La fermeture manuelle n'en fait pas partie : c'est une action d'exploitation,
/// pas une caractéristique de l'événement.
/// </summary>
public record EventDraft(string Name, string? Location, DateTime StartsAt, DateTime EndsAt, double? QuorumPercent);

public record DepartmentAttendance(string Department, int Headcount, int Present)
{
    public double Rate => Headcount == 0 ? 0 : (double)Present / Headcount;
}

public record EventDashboard(Event Event, EventStatus Status, string CheckInUrl,
    int TotalHeadcount, int TotalPresent, IReadOnlyList<DepartmentAttendance> ByDepartment)
{
    public double TotalRate => TotalHeadcount == 0 ? 0 : (double)TotalPresent / TotalHeadcount;
}

public class EventService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
{
    public async Task<List<EventListItem>> GetEventsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetLocalNow().DateTime;

        var rows = await db.Events.AsNoTracking()
            .OrderByDescending(e => e.StartsAt)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Location,
                e.StartsAt,
                e.EndsAt,
                e.IsManuallyClosed,
                CheckedIn = e.Attendances.Count
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new EventListItem(
                r.Id, r.Name, r.Location, r.StartsAt, r.EndsAt,
                new Event { StartsAt = r.StartsAt, EndsAt = r.EndsAt, IsManuallyClosed = r.IsManuallyClosed }
                    .StatusAt(now),
                r.CheckedIn))
        ];
    }

    public async Task<Event?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<bool> UpdateAsync(int id, EventDraft draft, bool isManuallyClosed,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = await db.Events.FindAsync([id], ct);
        if (ev is null) return false;

        Apply(ev, draft);
        ev.IsManuallyClosed = isManuallyClosed;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Event> CreateAsync(EventDraft draft, int createdByEmployeeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = new Event
        {
            QrToken = NewToken(),
            CreatedAt = clock.GetLocalNow().DateTime,
            CreatedByEmployeeId = createdByEmployeeId
        };
        Apply(ev, draft);

        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);
        return ev;
    }

    private static void Apply(Event ev, EventDraft draft)
    {
        ev.Name = draft.Name.Trim();
        ev.Location = string.IsNullOrWhiteSpace(draft.Location) ? null : draft.Location.Trim();
        ev.StartsAt = draft.StartsAt;
        ev.EndsAt = draft.EndsAt;
        ev.QuorumPercent = draft.QuorumPercent;
    }

    /// <summary>Export des présents (bouton « Export Attendees »).</summary>
    public async Task<(string FileName, byte[] Content)?> ExportAttendeesCsvAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return null;

        var rows = await db.Attendances.AsNoTracking()
            .Where(a => a.EventId == eventId)
            .OrderBy(a => a.CheckedInAt)
            .Select(a => new
            {
                a.Employee.PersonNumber,
                a.Employee.FirstName,
                a.Employee.LastName,
                a.Employee.Subdivision,
                a.CheckedInAt
            })
            .ToListAsync(ct);

        var csv = new StringBuilder("Person Number,First Name,Last Name,Subdivision,Checked In At\n");
        foreach (var r in rows)
            csv.Append($"{Csv(r.PersonNumber)},{Csv(r.FirstName)},{Csv(r.LastName)},{Csv(r.Subdivision)},{r.CheckedInAt:yyyy-MM-dd HH:mm:ss}\n");

        var slug = string.Concat(ev.Name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));

        // BOM : sans lui, Excel casse les accents des noms ivoiriens.
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(csv.ToString())];
        return ($"attendees-{slug}.csv", bytes);
    }

    private static string Csv(string? value)
    {
        if (value is null) return "";

        return value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    public async Task<bool> CloseAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = await db.Events.FindAsync([eventId], ct);
        if (ev is null) return false;
        ev.IsManuallyClosed = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Taux par département calculé sur l'effectif actif complet du département,
    /// pas seulement sur les personnes présentes.
    /// </summary>
    public async Task<EventDashboard?> GetDashboardAsync(int eventId, string baseUrl, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return null;

        // Récupéré séparément : EF Core ne sait pas traduire un Count(predicate)
        // corrélé par élément à l'intérieur d'un GroupBy...Select en SQL.
        var presentIds = await db.Attendances.AsNoTracking()
            .Where(a => a.EventId == eventId)
            .Select(a => a.EmployeeId)
            .ToListAsync(ct);
        var presentSet = presentIds.ToHashSet();

        var byDeptRaw = await db.Employees.AsNoTracking()
            .Where(e => e.IsActive)
            .GroupBy(e => e.Subdivision)
            .Select(g => new
            {
                Department = g.Key,
                Headcount = g.Count(),
                EmployeeIds = g.Select(e => e.Id)
            })
            .ToListAsync(ct);

        var byDept = byDeptRaw
            .Select(d => new DepartmentAttendance(
                d.Department,
                d.Headcount,
                d.EmployeeIds.Count(id => presentSet.Contains(id))))
            .OrderByDescending(d => d.Headcount)
            .ToList();

        return new EventDashboard(
            ev,
            ev.StatusAt(clock.GetLocalNow().DateTime),
            $"{baseUrl.TrimEnd('/')}/c/{ev.QrToken}",
            byDept.Sum(d => d.Headcount),
            byDept.Sum(d => d.Present),
            byDept);
    }

    public static byte[] QrPng(string url, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    public static string QrDataUri(string url) => "data:image/png;base64," + Convert.ToBase64String(QrPng(url));

    public async Task<string?> GetQrTokenAsync(int eventId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Events.Where(e => e.Id == eventId).Select(e => e.QrToken).FirstOrDefaultAsync(ct);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}