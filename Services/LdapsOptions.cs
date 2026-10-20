namespace MtnAttendance.Services;

/// <summary>Configure only through protected runtime settings, never through Git.</summary>
public sealed class LdapsOptions
{
    public const string SectionName = "Ldaps";
    public string PrimaryHost { get; set; } = string.Empty;
    public string? DisasterRecoveryHost { get; set; }
    public int Port { get; set; } = 636;
    public string UserSearchBase { get; set; } = string.Empty;
    public string? GroupSearchBase { get; set; }
    public string BindUsername { get; set; } = string.Empty;
    public string BindPassword { get; set; } = string.Empty;
    /// <summary>Confirm the real LDAP employee-number attribute with the directory team.</summary>
    public string EmployeeNumberAttribute { get; set; } = "employeeNumber";
}