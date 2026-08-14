using MtnAttendance.Domain;

namespace MtnAttendance.Services;

/// <summary>Formats d'affichage repris des maquettes.</summary>
public static class Ui
{
    public static string DateTimeLabel(DateTime value) => value.ToString("dd MMM yyyy, HH:mm",
        System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

    public static string LastLogin(DateTime? value, DateTime now)
    {
        if (value is null) return "—";
        var d = value.Value;
        if (d.Date == now.Date) return $"Today, {d:HH:mm}";
        if (d.Date == now.Date.AddDays(-1)) return $"Yesterday, {d:HH:mm}";
        return DateTimeLabel(d);
    }

    public static string StatusLabel(EventStatus status) => status switch
    {
        EventStatus.Open => "Open",
        EventStatus.Scheduled => "Scheduled",
        _ => "Closed"
    };

    public static string StatusClass(EventStatus status) => status.ToString().ToLowerInvariant();

    public static string Percent(double rate, int decimals = 0) =>
        (rate * 100).ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture) + "%";
}
