using ClinicApi.Data;
using ClinicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Services;

public class NotificationService
{
    private readonly ClinicDbContext _db;
    private readonly WhatsAppSender _whatsApp;

    public NotificationService(ClinicDbContext db, WhatsAppSender whatsApp)
    {
        _db = db;
        _whatsApp = whatsApp;
    }

    /// <summary>
    /// Send day-before confirmation requests.
    /// This re-opens Meta's 24-hour messaging window.
    /// Called by a scheduled job at ~6 PM daily.
    /// </summary>
    public async Task SendDayBeforeRemindersAsync()
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

        var appointments = await _db.Appointments
            .Include(a => a.Patient)
            .Where(a => a.AppointmentDate == tomorrow
                     && a.Status == AppointmentStatus.Pending)
            .ToListAsync();

        foreach (var appt in appointments)
        {
            if (string.IsNullOrEmpty(appt.Patient.WhatsAppId)) continue;

            await _whatsApp.SendAsync(appt.Patient.WhatsAppId,
                $"مرحباً {appt.Patient.FullNameAr}!\n" +
                $"لديك موعد غداً ({tomorrow:yyyy-MM-dd}) " +
                $"الوقت المتوقع: {appt.EstimatedStart.ToString("hh:mm tt")}.\n\n" +
                "اكتب \"نعم\" لتأكيد حضورك.");
        }
    }

    /// <summary>
    /// When a patient enters the room, alert those 2-3 spots away.
    /// Called from the status-update endpoint.
    /// </summary>
    public async Task SendApproachingAlertsAsync(Appointment currentAppt)
    {
        var settings = await _db.ClinicSettings.FindAsync(1);
        int offset = settings?.ApproachingAlertOffset ?? 3;

        var approaching = await _db.Appointments
            .Include(a => a.Patient)
            .Where(a => a.AppointmentDate == currentAppt.AppointmentDate
                     && a.QueuePosition >= currentAppt.QueuePosition + (offset - 1)
                     && a.QueuePosition <= currentAppt.QueuePosition + offset
                     && (a.Status == AppointmentStatus.Pending
                      || a.Status == AppointmentStatus.Confirmed))
            .ToListAsync();

        foreach (var appt in approaching)
        {
            if (string.IsNullOrEmpty(appt.Patient.WhatsAppId)) continue;

            await _whatsApp.SendAsync(appt.Patient.WhatsAppId,
                $"مرحباً {appt.Patient.FullNameAr}!\n" +
                "دورك اقترب، يرجى التوجه إلى العيادة الآن. 🏥");
        }
    }
}
