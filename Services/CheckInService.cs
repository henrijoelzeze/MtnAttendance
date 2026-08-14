using Microsoft.EntityFrameworkCore;
using MtnAttendance.Data;
using MtnAttendance.Domain;

namespace MtnAttendance.Services;

public enum CheckInOutcome
{
    EventNotFound,
    EventClosed,
    UnknownPerson,
    AlreadyCheckedIn,
    Success
}

public record CheckInResult(CheckInOutcome Outcome, string? FirstName = null);

public record CheckInEventInfo(int Id, string Name, string? Location, EventStatus Status);

public class CheckInService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
{
    /// <summary>Chargement de l'événement pour l'affichage de la page /c/{token}.</summary>
    public async Task<CheckInEventInfo?> GetEventAsync(string qrToken, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.QrToken == qrToken, ct);
        return ev is null
            ? null
            : new CheckInEventInfo(ev.Id, ev.Name, ev.Location, ev.StatusAt(clock.GetLocalNow().DateTime));
    }

    public async Task<CheckInResult> CheckInAsync(
        string qrToken, string rawPersonNumber, string? ipAddress, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetLocalNow().DateTime;

        // 1. L'événement existe et est ouvert
        var ev = await db.Events.FirstOrDefaultAsync(e => e.QrToken == qrToken, ct);
        if (ev is null) return new(CheckInOutcome.EventNotFound);
        if (ev.StatusAt(now) != EventStatus.Open) return new(CheckInOutcome.EventClosed);

        // 2. Le matricule correspond à un employé réel et actif
        var key = PersonNumber.Normalize(rawPersonNumber);
        if (key.Length == 0) return new(CheckInOutcome.UnknownPerson);

        var employee = await db.Employees
            .Where(e => e.PersonNumberKey == key && e.IsActive)
            .Select(e => new { e.Id, e.FirstName })
            .FirstOrDefaultAsync(ct);

        if (employee is null) return new(CheckInOutcome.UnknownPerson);

        // 3. Déjà pointé ? (chemin rapide, non fiable seul)
        var already = await db.Attendances
            .AnyAsync(a => a.EventId == ev.Id && a.EmployeeId == employee.Id, ct);
        if (already) return new(CheckInOutcome.AlreadyCheckedIn, employee.FirstName);

        // 4. Insertion — l'index unique tranche en cas de double soumission simultanée
        db.Attendances.Add(new Attendance
        {
            EventId = ev.Id,
            EmployeeId = employee.Id,
            CheckedInAt = now,
            IpAddress = ipAddress
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return new(CheckInOutcome.Success, employee.FirstName);
        }
        catch (DbUpdateException ex) when (AppDbContext.IsUniqueViolation(ex))
        {
            return new(CheckInOutcome.AlreadyCheckedIn, employee.FirstName);
        }
    }
}
