using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MtnAttendance.Domain;
using Npgsql;

namespace MtnAttendance.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<HrAccount> HrAccounts => Set<HrAccount>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureEmployee(modelBuilder);
        ConfigureHrAccount(modelBuilder);
        ConfigureEvent(modelBuilder);
        ConfigureImportRun(modelBuilder);
        ConfigureAttendance(modelBuilder);

        ApplyUtcDateTimeConverters(modelBuilder);
    }

    private static void ConfigureEmployee(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Employee>(e =>
        {
            e.Property(x => x.PersonNumber)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(x => x.PersonNumberKey)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(x => x.FirstName)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(x => x.MiddleName)
                .HasMaxLength(128);

            e.Property(x => x.LastName)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(x => x.Gender)
                .HasMaxLength(32);

            e.Property(x => x.Phone)
                .HasMaxLength(64);

            e.Property(x => x.Email)
                .HasMaxLength(256);

            e.Property(x => x.Position)
                .HasMaxLength(256);

            e.Property(x => x.Subdivision)
                .HasMaxLength(128)
                .IsRequired();

            e.HasIndex(x => x.PersonNumberKey)
                .IsUnique();

            e.HasIndex(x => x.Subdivision);
        });

    private static void ConfigureHrAccount(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<HrAccount>(e =>
        {
            e.Property(x => x.PasswordHash)
                .HasMaxLength(512);

            e.HasIndex(x => x.EmployeeId)
                .IsUnique();

            e.HasOne(x => x.Employee)
                .WithOne(x => x.HrAccount)
                .HasForeignKey<HrAccount>(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    private static void ConfigureEvent(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Event>(e =>
        {
            e.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            e.Property(x => x.Location)
                .HasMaxLength(200);

            e.Property(x => x.QrToken)
                .HasMaxLength(64)
                .IsRequired();

            e.HasIndex(x => x.QrToken)
                .IsUnique();
        });

    private static void ConfigureImportRun(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ImportRun>(e =>
        {
            e.Property(x => x.Warnings)
                .HasMaxLength(4000);
        });

    private static void ConfigureAttendance(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Attendance>(e =>
        {
            e.Property(x => x.IpAddress)
                .HasMaxLength(64);

            // Source de vérité anti-doublon : la base, pas la logique UI.
            e.HasIndex(x => new
            {
                x.EventId,
                x.EmployeeId
            }).IsUnique();

            e.HasOne(x => x.Event)
                .WithMany(x => x.Attendances)
                .HasForeignKey(x => x.EventId);

            e.HasOne(x => x.Employee)
                .WithMany(x => x.Attendances)
                .HasForeignKey(x => x.EmployeeId);
        });

    /// <summary>
    /// Npgsql exige Kind=Utc pour « timestamp with time zone ». Abidjan est UTC+0
    /// toute l'année (pas d'heure d'été), donc aucune vraie conversion n'est
    /// nécessaire : on force simplement le « Kind » sans changer la valeur d'horloge.
    /// Aucune migration requise : le type de colonne (timestamptz) ne change pas.
    /// </summary>
    private static void ApplyUtcDateTimeConverters(ModelBuilder modelBuilder)
    {
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => AsUtc(v),
            v => AsUtc(v));

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? AsUtc(v.Value) : v,
            v => v.HasValue ? AsUtc(v.Value) : v);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType == typeof(DateTime))
                property.SetValueConverter(utcConverter);
            else if (property.ClrType == typeof(DateTime?))
                property.SetValueConverter(nullableUtcConverter);
        }
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// Vrai si l'échec vient d'une contrainte d'unicité PostgreSQL.
    /// SQLSTATE 23505 = unique_violation.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation;
}