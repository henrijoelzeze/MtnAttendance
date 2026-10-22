namespace MtnAttendance.Services;

/// <summary>Runtime-only configuration. Never include bind passwords in Git.</summary>
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

    /// <summary>Verify actual directory attribute name with the directory administrator.</summary>
    public string EmployeeNumberAttribute { get; set; } = "employeeNumber";
    public string HrDepartmentAttribute { get; set; } = "department";
    public string HrDepartmentValue { get; set; } = "Human Resources";

    /// <summary>
    /// Optional exact LDAP group DN. If set, HR authorization is based on direct
    /// group membership instead of HrDepartmentAttribute. GroupSearchBase alone
    /// is not enough to identify the allowed group.
    /// </summary>
    public string? HrGroupDn { get; set; }
}
