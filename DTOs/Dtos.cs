using ClinicApi.Models;

namespace ClinicApi.DTOs;

// ─── WhatsApp Webhook (simplified Meta payload) ────────────────────────────────
public record WhatsAppIncoming(string WaId, string MessageBody);

// ─── Booking ───────────────────────────────────────────────────────────────────
public record BookingRequest(string FullNameAr, string PhoneNumber, string? WaId);

public record BookingResponse(
    int AppointmentId,
    string PatientName,
    string Date,
    string EstimatedTimeRange,
    int QueuePosition
);

// ─── Queue / Status ────────────────────────────────────────────────────────────
public record StatusUpdateRequest(AppointmentStatus NewStatus);
public record ReorderRequest(int[] OrderedIds);

public record QueueItemDto(
    int AppointmentId,
    int PatientId,
    string PatientName,
    string Phone,
    int QueuePosition,
    string EstimatedStart,
    string EstimatedEnd,
    string Status,
    bool IsWalkIn,
    DateTime? EnteredRoomAt,
    string? Notes
);

public record UpdateNotesRequest(string? Notes);

// ─── Walk-in ───────────────────────────────────────────────────────────────────
public record WalkInRequest(string FullNameAr, string PhoneNumber, DateOnly? DateOfBirth = null, Gender? Gender = null);

// ─── Calendar ──────────────────────────────────────────────────────────────────
public record BlockDayRequest(DateOnly Date, string? Note);

// ─── Settings ──────────────────────────────────────────────────────────────────
public record UpdateSettingsRequest(
    int? AvgConsultationMinutes,
    TimeOnly? DefaultStartTime,
    TimeOnly? DefaultEndTime,
    string? WeeklyOffDays,
    int? ApproachingAlertOffset
);

// ─── Patient CRUD ─────────────────────────────────────────────────────────────
public record CreatePatientRequest(string FullNameAr, string PhoneNumber, DateOnly? DateOfBirth = null, Gender? Gender = null);
public record UpdatePatientRequest(string? FullNameAr, string? PhoneNumber, DateOnly? DateOfBirth = null, Gender? Gender = null);

// ─── Doctor Dashboard ──────────────────────────────────────────────────────────
public record DoctorViewDto(QueueItemDto? CurrentPatient, QueueItemDto? UpNext);
