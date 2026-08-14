namespace MtnAttendance.Domain;

public enum EventStatus { Scheduled, Open, Closed }

/// <summary>
/// Normalisation du Numéro de Matricule : Excel transforme "0468089" en 468089,
/// et les employés saisissent avec des espaces. On stocke une forme canonique
/// utilisée pour toutes les recherches.
/// </summary>
public static class PersonNumber
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var cleaned = string.Concat(raw.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        var trimmed = cleaned.TrimStart('0');
        return trimmed.Length == 0 ? cleaned : trimmed;
    }
}

public class Employee
{
    public int Id { get; set; }

    /// <summary>Valeur telle qu'affichée dans le fichier RH.</summary>
    public string PersonNumber { get; set; } = default!;

    /// <summary>Forme canonique, unique, utilisée pour le pointage et la connexion.</summary>
    public string PersonNumberKey { get; set; } = default!;

    public string FirstName { get; set; } = default!;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = default!;
    public string? Gender { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Position { get; set; }

    /// <summary>Subdivision = département utilisé dans le tableau de bord.</summary>
    public string Subdivision { get; set; } = default!;

    public bool IsActive { get; set; } = true;
    public DateTime? LastSeenInImportAt { get; set; }

    public HrAccount? HrAccount { get; set; }
    public List<Attendance> Attendances { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class HrAccount
{
    public const string HrSubdivision = "Human Resources";

    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = default!;

    /// <summary>null =&gt; l'utilisateur passe par l'écran « Créer un mot de passe ».</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Désactivé automatiquement à l'import si la personne n'est plus RH ou plus active.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
}

public class Event
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Location { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }

    /// <summary>Fermeture manuelle : prime toujours sur les horaires.</summary>
    public bool IsManuallyClosed { get; set; }

    /// <summary>Jeton non devinable de l'URL de pointage (/c/{token}).</summary>
    public string QrToken { get; set; } = default!;

    /// <summary>Seuil de quorum affiché sur le tableau de bord (null = pas de seuil).</summary>
    public double? QuorumPercent { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedByEmployeeId { get; set; }

    public List<Attendance> Attendances { get; set; } = [];

    public EventStatus StatusAt(DateTime now)
    {
        if (IsManuallyClosed || now > EndsAt) return EventStatus.Closed;

        return now < StartsAt ? EventStatus.Scheduled : EventStatus.Open;
    }
}

public class Attendance
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = default!;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = default!;
    public DateTime CheckedInAt { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Trace d'un import de la liste du personnel : alimente « Last Sync Results ».</summary>
public class ImportRun
{
    public int Id { get; set; }
    public DateTime RunAt { get; set; }
    public int RunByEmployeeId { get; set; }
    public int RowsRead { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }
    public int HrAccountsCreated { get; set; }
    public int HrAccountsDisabled { get; set; }
    public string? Warnings { get; set; }
}
