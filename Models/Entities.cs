using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClinicApi.Models;

// ─── Patient ───────────────────────────────────────────────────────────────────
public class Patient
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string FullNameAr { get; set; } = null!;   // Arabic-only validated name

    [Required, MaxLength(20)]
    public string PhoneNumber { get; set; } = null!;   // Digits only, e.g. "9647701234567"

    [MaxLength(50)]
    public string? WhatsAppId { get; set; }            // wa_id from Meta webhook

    public DateOnly? DateOfBirth { get; set; }

    public Gender Gender { get; set; } = Gender.Unknown;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Appointment> Appointments { get; set; } = new List<Appointment>();
}

public enum Gender
{
    Unknown,
    Male,
    Female
}

// ─── Appointment / Queue Entry ─────────────────────────────────────────────────
public enum AppointmentStatus
{
    Pending,      // Booked, waiting
    Confirmed,    // Replied "Yes" to day-before reminder
    UpNext,       // Secretary marked as next
    InRoom,       // Currently with doctor
    Completed,    // Done
    Cancelled,    // Cancelled by secretary
    DidNotAttend  // Nightly job marks leftover Pending/Confirmed
}

public class Appointment
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int PatientId { get; set; }

    [ForeignKey(nameof(PatientId))]
    public Patient Patient { get; set; } = null!;

    [Required]
    public DateOnly AppointmentDate { get; set; }

    /// <summary>Position in the queue for that day (1-based).</summary>
    public int QueuePosition { get; set; }

    /// <summary>Estimated window start, computed at booking time.</summary>
    public TimeOnly EstimatedStart { get; set; }

    /// <summary>Estimated window end = EstimatedStart + AvgConsultationMinutes.</summary>
    public TimeOnly EstimatedEnd { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Pending;

    public bool IsWalkIn { get; set; } = false;

    /// <summary>Doctor/secretary notes for this visit.</summary>
    public string? Notes { get; set; }

    /// <summary>When the patient entered the room (for doctor timer).</summary>
    public DateTime? EnteredRoomAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ConcurrencyCheck]
    public Guid Version { get; set; } = Guid.NewGuid();
}

// ─── Calendar Day Overrides ────────────────────────────────────────────────────
public enum DayType
{
    Normal,   // Regular working day (uses default hours)
    Blocked,  // Secretary blocked this day — no bookings
    OffDay    // Pre-configured weekly off-day (e.g. Friday)
}

public class CalendarDay
{
    [Key]
    public int Id { get; set; }

    [Required]
    public DateOnly Date { get; set; }

    public DayType Type { get; set; } = DayType.Normal;

    /// <summary>Override start time for this specific day (nullable = use default).</summary>
    public TimeOnly? StartTime { get; set; }

    /// <summary>Override end time for this specific day (nullable = use default).</summary>
    public TimeOnly? EndTime { get; set; }

    public string? Note { get; set; }
}

// ─── Clinic Settings (singleton row) ───────────────────────────────────────────
public class ClinicSettings
{
    [Key]
    public int Id { get; set; }  // Always 1

    [Required, MaxLength(200)]
    public string ClinicName { get; set; } = "العيادة";

    /// <summary>Average consultation duration in minutes. Drives time-slot calc.</summary>
    public int AvgConsultationMinutes { get; set; } = 15;

    public TimeOnly DefaultStartTime { get; set; } = new(08, 30);
    public TimeOnly DefaultEndTime { get; set; } = new(16, 00);

    /// <summary>Comma-separated DayOfWeek integers for weekly off-days. e.g. "5" = Friday.</summary>
    [MaxLength(20)]
    public string WeeklyOffDays { get; set; } = "5";  // Friday

    /// <summary>How many spots ahead to send "approaching" alert.</summary>
    public int ApproachingAlertOffset { get; set; } = 3;

    /// <summary>IANA time-zone ID for the clinic. Drives job scheduling.</summary>
    [MaxLength(50)]
    public string TimeZoneId { get; set; } = "Asia/Baghdad";
}

// ─── WhatsApp Conversation State (tracks the AI funnel per user) ───────────────
public enum ConversationStep
{
    AwaitingName,
    AwaitingPhone,
    AwaitingReturningUserChoice,  // "Is this for [Name]?"
    Completed
}

public class WhatsAppSession
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string WaId { get; set; } = null!;  // WhatsApp sender ID

    public ConversationStep Step { get; set; } = ConversationStep.AwaitingName;

    public string? CollectedName { get; set; }
    public string? CollectedPhone { get; set; }

    /// <summary>If returning user matched, store patient ID here.</summary>
    public int? MatchedPatientId { get; set; }

    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Audit Log ────────────────────────────────────────────────────────────────
public class AuditLog
{
    [Key]
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    [MaxLength(100)]
    public string Username { get; set; } = "";
    [Required, MaxLength(200)]
    public string Action { get; set; } = "";
    public string? Details { get; set; }
}

// ─── User Accounts (JWT auth) ────────────────────────────────────────────────
public enum UserRole
{
    Secretary,
    Doctor,
    Admin
}

public class UserAccount
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Username { get; set; } = null!;

    [Required]
    public string PasswordHash { get; set; } = null!;

    public UserRole Role { get; set; } = UserRole.Secretary;
}
