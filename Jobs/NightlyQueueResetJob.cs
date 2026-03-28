using ClinicApi.Data;
using ClinicApi.Models;
using ClinicApi.Services;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Jobs;

/// <summary>
/// Two-schedule nightly job adjusted for the clinic's local time zone:
///   1. Day-before reminders  — 6 PM local (e.g. 3 PM UTC for Asia/Baghdad)
///   2. Queue reset (DidNotAttend) — midnight local (e.g. 9 PM UTC for Asia/Baghdad)
/// </summary>
public class NightlyQueueResetJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NightlyQueueResetJob> _logger;

    public NightlyQueueResetJob(IServiceProvider services, ILogger<NightlyQueueResetJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var tz = await GetClinicTimeZoneAsync();
            var nowUtc = DateTime.UtcNow;
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

            // Compute next local targets for today or tomorrow
            var nextReminder = GetNextOccurrenceUtc(nowLocal, new TimeOnly(18, 0), tz); // 6 PM local
            var nextReset = GetNextOccurrenceUtc(nowLocal, new TimeOnly(0, 0), tz);     // midnight local

            // Pick whichever comes first
            var (nextRun, isReminder) = nextReminder <= nextReset
                ? (nextReminder, true)
                : (nextReset, false);

            var delay = nextRun - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            _logger.LogInformation(
                "Next job: {Type} in {Delay} (at {RunTime} UTC)",
                isReminder ? "DayBeforeReminders" : "QueueReset",
                delay, nextRun);

            await Task.Delay(delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                if (isReminder)
                    await RunRemindersAsync();
                else
                    await RunQueueResetAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly job failed ({Type})", isReminder ? "Reminders" : "QueueReset");
            }
        }
    }

    internal static DateTimeOffset GetNextOccurrenceUtc(DateTime nowLocal, TimeOnly targetLocal, TimeZoneInfo tz)
    {
        var todayTarget = nowLocal.Date + targetLocal.ToTimeSpan();
        if (todayTarget <= nowLocal)
            todayTarget = todayTarget.AddDays(1);

        return new DateTimeOffset(DateTime.SpecifyKind(todayTarget, DateTimeKind.Unspecified),
            tz.GetUtcOffset(todayTarget));
    }

    private async Task<TimeZoneInfo> GetClinicTimeZoneAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var settings = await db.ClinicSettings.FirstOrDefaultAsync();
        var tzId = settings?.TimeZoneId ?? "Asia/Baghdad";
        return TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    private async Task RunRemindersAsync()
    {
        using var scope = _services.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        await notifications.SendDayBeforeRemindersAsync();
        _logger.LogInformation("Day-before reminders sent");
    }

    private async Task RunQueueResetAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var tz = await GetClinicTimeZoneAsync();

        var count = await RunQueueResetAsync(db, tz);
        _logger.LogInformation("Marked {Count} appointments as DidNotAttend", count);
    }

    internal static async Task<int> RunQueueResetAsync(ClinicDbContext db, TimeZoneInfo tz)
    {
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(todayLocal);

        var leftovers = await db.Appointments
            .Where(a => a.AppointmentDate < today
                     && (a.Status == AppointmentStatus.Pending
                      || a.Status == AppointmentStatus.Confirmed
                      || a.Status == AppointmentStatus.UpNext
                      || a.Status == AppointmentStatus.SteppedOut))
            .ToListAsync();

        foreach (var appt in leftovers)
        {
            appt.Status = AppointmentStatus.DidNotAttend;
            appt.UpdatedAt = DateTime.UtcNow;
            appt.Version = Guid.NewGuid();
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // If another process modified it concurrently, we can skip and let it be handled later or log.
        }
        return leftovers.Count;
    }
}
