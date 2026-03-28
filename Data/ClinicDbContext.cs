using ClinicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Data;

public class ClinicDbContext : DbContext
{
    public ClinicDbContext(DbContextOptions<ClinicDbContext> options) : base(options) { }

    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<CalendarDay> CalendarDays => Set<CalendarDay>();
    public DbSet<ClinicSettings> ClinicSettings => Set<ClinicSettings>();
    public DbSet<WhatsAppSession> WhatsAppSessions => Set<WhatsAppSession>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Patient ────────────────────────────────────────────────────────
        mb.Entity<Patient>(e =>
        {
            e.HasIndex(p => p.PhoneNumber).IsUnique();
            e.HasIndex(p => p.WhatsAppId);
        });

        // ── Appointment ────────────────────────────────────────────────────
        mb.Entity<Appointment>(e =>
        {
            e.HasIndex(a => new { a.AppointmentDate, a.QueuePosition }).IsUnique();
            e.HasIndex(a => a.Status);
            e.HasOne(a => a.Patient)
             .WithMany(p => p.Appointments)
             .HasForeignKey(a => a.PatientId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── CalendarDay ────────────────────────────────────────────────────
        mb.Entity<CalendarDay>(e =>
        {
            e.HasIndex(c => c.Date).IsUnique();
        });

        // ── WhatsAppSession ────────────────────────────────────────────────
        mb.Entity<WhatsAppSession>(e =>
        {
            e.HasIndex(s => s.WaId).IsUnique();
        });

        // ── UserAccount ───────────────────────────────────────────────────
        mb.Entity<UserAccount>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
        });

        // ── AuditLog ──────────────────────────────────────────────────────
        mb.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.Timestamp);
        });

        // ── Seed default settings ──────────────────────────────────────────
        mb.Entity<ClinicSettings>().HasData(new ClinicSettings
        {
            Id = 1,
            ClinicName = "العيادة",
            AvgConsultationMinutes = 15,
            DefaultStartTime = new TimeOnly(08, 30),
            DefaultEndTime = new TimeOnly(16, 00),
            WeeklyOffDays = "5",
            ApproachingAlertOffset = 3
        });

        // ── Seed default admin account (password: admin123) ──────────────
        mb.Entity<UserAccount>().HasData(new UserAccount
        {
            Id = 1,
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
            Role = UserRole.Admin
        });
    }
}
