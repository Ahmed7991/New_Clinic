using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Hubs;
using ClinicApi.Models;
using ClinicApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/queue")]
[Authorize(Roles = "Secretary,Admin,Doctor")]
public class QueueController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly BookingService _booking;
    private readonly NotificationService _notifications;
    private readonly IHubContext<QueueHub> _hub;
    private readonly AuditService _audit;

    public QueueController(
        ClinicDbContext db,
        BookingService booking,
        NotificationService notifications,
        IHubContext<QueueHub> hub,
        AuditService audit)
    {
        _db = db;
        _booking = booking;
        _notifications = notifications;
        _hub = hub;
        _audit = audit;
    }

    // ── GET /api/queue/today ───────────────────────────────────────────────────
    [HttpGet("today")]
    public async Task<ActionResult<List<QueueItemDto>>> GetTodayQueue()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var queue = await GetQueueForDateAsync(today);
        return Ok(queue);
    }

    // ── GET /api/queue/{date} ──────────────────────────────────────────────────
    [HttpGet("{date}")]
    public async Task<ActionResult<List<QueueItemDto>>> GetQueueByDate(DateOnly date)
    {
        var queue = await GetQueueForDateAsync(date);
        return Ok(queue);
    }

    // ── PUT /api/queue/{id}/status ─────────────────────────────────────────────
    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] StatusUpdateRequest req)
    {
        var appt = await _db.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (appt is null) return NotFound();

        bool validTransition = req.NewStatus == AppointmentStatus.Cancelled || (appt.Status, req.NewStatus) switch
        {
            (AppointmentStatus.Pending, AppointmentStatus.Confirmed) => true,
            (AppointmentStatus.Pending, AppointmentStatus.UpNext) => true,
            (AppointmentStatus.Confirmed, AppointmentStatus.UpNext) => true,
            (AppointmentStatus.UpNext, AppointmentStatus.InRoom) => true,
            (AppointmentStatus.InRoom, AppointmentStatus.Completed) => true,
            _ => false
        };

        if (!validTransition)
            return BadRequest($"Cannot transition from {appt.Status} to {req.NewStatus}.");

        appt.Status = req.NewStatus;
        appt.UpdatedAt = DateTime.UtcNow;
        appt.Version = Guid.NewGuid();

        if (req.NewStatus == AppointmentStatus.InRoom)
            appt.EnteredRoomAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("The appointment was modified by another user. Please refresh and try again.");
        }

        await _audit.LogAsync("تغيير حالة موعد", $"موعد #{appt.Id} ({appt.Patient.FullNameAr}): {appt.Status}", HttpContext);

        // Broadcast real-time update
        await _hub.BroadcastStatusChange(appt.Id, req.NewStatus.ToString());

        // Send doctor view update
        var doctorView = await BuildDoctorViewAsync(appt.AppointmentDate);
        await _hub.BroadcastDoctorView(doctorView);

        // Trigger approaching alerts when patient enters room
        if (req.NewStatus == AppointmentStatus.InRoom)
            await _notifications.SendApproachingAlertsAsync(appt);

        return Ok();
    }

    // ── POST /api/queue/walkin ─────────────────────────────────────────────────
    [HttpPost("walkin")]
    public async Task<ActionResult<BookingResponse>> AddWalkIn([FromBody] WalkInRequest req)
    {
        var result = await _booking.BookWalkInForTodayAsync(req.FullNameAr, req.PhoneNumber, req.DateOfBirth, req.Gender);

        await _audit.LogAsync("إضافة حضور مباشر", $"{req.FullNameAr} ({req.PhoneNumber})", HttpContext);
        await _hub.BroadcastNewBooking(result);

        return Ok(result);
    }

    // ── PUT /api/queue/{id}/notes ──────────────────────────────────────────────
    [HttpPut("{id}/notes")]
    public async Task<IActionResult> UpdateNotes(int id, [FromBody] UpdateNotesRequest req)
    {
        var appt = await _db.Appointments.FindAsync(id);
        if (appt is null) return NotFound();

        appt.Notes = req.Notes;
        appt.UpdatedAt = DateTime.UtcNow;
        appt.Version = Guid.NewGuid();

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("The appointment was modified by another user. Please refresh and try again.");
        }
        return Ok();
    }

    // ── GET /api/queue/summary ──────────────────────────────────────────────
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] DateOnly? date)
    {
        var d = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var appts = await _db.Appointments
            .Include(a => a.Patient)
            .Where(a => a.AppointmentDate == d)
            .OrderBy(a => a.QueuePosition)
            .ToListAsync();

        // Calculate average consultation minutes for completed appointments
        double? avgMinutes = null;
        var completed = appts
            .Where(a => a.Status == AppointmentStatus.Completed && a.EnteredRoomAt.HasValue)
            .Select(a => (a.UpdatedAt - a.EnteredRoomAt!.Value).TotalMinutes)
            .Where(m => m > 0 && m < 300)
            .ToList();
        if (completed.Count > 0)
            avgMinutes = Math.Round(completed.Average(), 1);

        return Ok(new
        {
            date = d.ToString("yyyy-MM-dd"),
            totalBooked = appts.Count,
            completed = appts.Count(a => a.Status == AppointmentStatus.Completed),
            didNotAttend = appts.Count(a => a.Status == AppointmentStatus.DidNotAttend),
            inProgress = appts.Count(a => a.Status is AppointmentStatus.Pending
                or AppointmentStatus.Confirmed or AppointmentStatus.UpNext
                or AppointmentStatus.InRoom),
            cancelled = appts.Count(a => a.Status == AppointmentStatus.Cancelled),
            averageConsultationMinutes = avgMinutes,
            appointments = appts.Select(a => new
            {
                appointmentId = a.Id,
                patientId = a.PatientId,
                patientName = a.Patient.FullNameAr,
                status = a.Status.ToString(),
                estimatedTimeRange = a.EstimatedStart.ToString("hh:mm tt") + " - " + a.EstimatedEnd.ToString("hh:mm tt"),
                notes = a.Notes
            })
        });
    }

    // ── DELETE /api/queue/{id} ─────────────────────────────────────────────────
    [HttpDelete("{id}")]
    public async Task<IActionResult> CancelAppointment(int id)
    {
        var appt = await _db.Appointments.FindAsync(id);
        if (appt is null) return NotFound();

        appt.Status = AppointmentStatus.Cancelled;
        appt.UpdatedAt = DateTime.UtcNow;
        appt.Version = Guid.NewGuid();

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("The appointment was modified by another user. Please refresh and try again.");
        }

        await _hub.BroadcastStatusChange(id, AppointmentStatus.Cancelled.ToString());

        return Ok();
    }

    // ── PUT /api/queue/reorder ────────────────────────────────────────────────
    [HttpPut("reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest req)
    {
        if (req.OrderedIds is null || req.OrderedIds.Length == 0)
            return BadRequest("orderedIds is required.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var settings = await _db.ClinicSettings.FindAsync(1);
        if (settings is null) return StatusCode(500);

        var calDay = await _db.CalendarDays.FirstOrDefaultAsync(c => c.Date == today);
        var startTime = calDay?.StartTime ?? settings.DefaultStartTime;

        var appts = await _db.Appointments
            .Where(a => a.AppointmentDate == today && req.OrderedIds.Contains(a.Id))
            .ToListAsync();

        for (int i = 0; i < req.OrderedIds.Length; i++)
        {
            var appt = appts.FirstOrDefault(a => a.Id == req.OrderedIds[i]);
            if (appt is null) continue;
            if (appt.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed or AppointmentStatus.UpNext))
                continue;

            appt.QueuePosition = i + 1;
            appt.EstimatedStart = startTime.AddMinutes(i * settings.AvgConsultationMinutes);
            appt.EstimatedEnd = appt.EstimatedStart.AddMinutes(settings.AvgConsultationMinutes);
            appt.UpdatedAt = DateTime.UtcNow;
            appt.Version = Guid.NewGuid();
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("The queue was modified by another user. Please refresh and try again.");
        }

        await _audit.LogAsync("إعادة ترتيب القائمة", $"عدد المواعيد: {req.OrderedIds.Length}", HttpContext);

        var queue = await GetQueueForDateAsync(today);
        await _hub.BroadcastQueueUpdate(queue);

        var doctorView = await BuildDoctorViewAsync(today);
        await _hub.BroadcastDoctorView(doctorView);

        return Ok();
    }

    // ── GET /api/queue/doctor-view ─────────────────────────────────────────────
    [HttpGet("doctor-view")]
    public async Task<ActionResult<DoctorViewDto>> GetDoctorView()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var view = await BuildDoctorViewAsync(today);
        return Ok(view);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private async Task<List<QueueItemDto>> GetQueueForDateAsync(DateOnly date)
    {
        return await _db.Appointments
            .Include(a => a.Patient)
            .Where(a => a.AppointmentDate == date
                     && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.QueuePosition)
            .Select(a => new QueueItemDto(
                a.Id,
                a.PatientId,
                a.Patient.FullNameAr,
                a.Patient.PhoneNumber,
                a.QueuePosition,
                a.EstimatedStart.ToString("hh:mm tt"),
                a.EstimatedEnd.ToString("hh:mm tt"),
                a.Status.ToString(),
                a.IsWalkIn,
                a.EnteredRoomAt,
                a.Notes
            ))
            .ToListAsync();
    }

    private async Task<DoctorViewDto> BuildDoctorViewAsync(DateOnly date)
    {
        var appts = await _db.Appointments
            .Include(a => a.Patient)
            .Where(a => a.AppointmentDate == date
                     && (a.Status == AppointmentStatus.InRoom
                      || a.Status == AppointmentStatus.UpNext
                      || a.Status == AppointmentStatus.Pending
                      || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.QueuePosition)
            .ToListAsync();

        var current = appts.FirstOrDefault(a => a.Status == AppointmentStatus.InRoom);
        var upNext  = appts.FirstOrDefault(a => a.Status == AppointmentStatus.UpNext)
                   ?? appts.FirstOrDefault(a => a.Status is AppointmentStatus.Pending
                                                         or AppointmentStatus.Confirmed);

        return new DoctorViewDto(
            current is not null ? MapToDto(current) : null,
            upNext  is not null ? MapToDto(upNext)  : null
        );
    }

    private static QueueItemDto MapToDto(Appointment a) => new(
        a.Id,
        a.PatientId,
        a.Patient.FullNameAr,
        a.Patient.PhoneNumber,
        a.QueuePosition,
        a.EstimatedStart.ToString("hh:mm tt"),
        a.EstimatedEnd.ToString("hh:mm tt"),
        a.Status.ToString(),
        a.IsWalkIn,
        a.EnteredRoomAt,
        a.Notes
    );
}
