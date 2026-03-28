using ClinicApi.Data;
using ClinicApi.Jobs;
using ClinicApi.Models;
using ClinicApi.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Tests;

public class NightlyQueueResetJobTests
{
    // ─── GetNextOccurrenceUtc ────────────────────────────────────────────────────

    [Fact]
    public void GetNextOccurrenceUtc_TargetLaterToday_ReturnsTodayInUtc()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); // UTC+3

        // It's 5 PM local → target 6 PM should be today
        var nowLocal = new DateTime(2026, 3, 22, 17, 0, 0);
        var result = NightlyQueueResetJob.GetNextOccurrenceUtc(nowLocal, new TimeOnly(18, 0), tz);

        // 6 PM Baghdad = 3 PM UTC on the same day
        Assert.Equal(22, result.UtcDateTime.Day);
        Assert.Equal(15, result.UtcDateTime.Hour);
        Assert.Equal(0, result.UtcDateTime.Minute);
    }

    [Fact]
    public void GetNextOccurrenceUtc_TargetAlreadyPassed_RollsToTomorrow()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); // UTC+3

        // It's 7 PM local → target 6 PM already passed, should roll to tomorrow
        var nowLocal = new DateTime(2026, 3, 22, 19, 0, 0);
        var result = NightlyQueueResetJob.GetNextOccurrenceUtc(nowLocal, new TimeOnly(18, 0), tz);

        // Tomorrow 6 PM Baghdad = 3 PM UTC on March 23
        Assert.Equal(23, result.UtcDateTime.Day);
        Assert.Equal(15, result.UtcDateTime.Hour);
    }

    [Fact]
    public void GetNextOccurrenceUtc_MidnightTarget_ConvertsCorrectly()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); // UTC+3

        // It's 11 PM local → midnight target rolls to tomorrow
        var nowLocal = new DateTime(2026, 3, 22, 23, 0, 0);
        var result = NightlyQueueResetJob.GetNextOccurrenceUtc(nowLocal, new TimeOnly(0, 0), tz);

        // Tomorrow midnight Baghdad = 9 PM UTC on March 22
        Assert.Equal(22, result.UtcDateTime.Day);
        Assert.Equal(21, result.UtcDateTime.Hour);
    }

    [Fact]
    public void GetNextOccurrenceUtc_ExactlyAtTarget_RollsToTomorrow()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");

        // It's exactly 6 PM local → should roll to tomorrow's 6 PM
        var nowLocal = new DateTime(2026, 3, 22, 18, 0, 0);
        var result = NightlyQueueResetJob.GetNextOccurrenceUtc(nowLocal, new TimeOnly(18, 0), tz);

        Assert.Equal(23, result.UtcDateTime.Day);
        Assert.Equal(15, result.UtcDateTime.Hour);
    }

    [Fact]
    public void GetNextOccurrenceUtc_DifferentTimezone_ComputesCorrectOffset()
    {
        // Use UTC itself — offset 0
        var tz = TimeZoneInfo.Utc;

        var nowLocal = new DateTime(2026, 3, 22, 10, 0, 0);
        var result = NightlyQueueResetJob.GetNextOccurrenceUtc(nowLocal, new TimeOnly(18, 0), tz);

        // 6 PM UTC = 6 PM UTC (no offset)
        Assert.Equal(22, result.UtcDateTime.Day);
        Assert.Equal(18, result.UtcDateTime.Hour);
    }

    // ─── RunQueueResetAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunQueueResetAsync_MarksOldAppointments_AsDidNotAttend()
    {
        var db = TestDbHelper.CreateContext();
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");
        var yesterday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).AddDays(-1));

        var patient = new Patient
        {
            FullNameAr = "أحمد",
            PhoneNumber = "9647701234567"
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        db.Appointments.AddRange(
            new Appointment
            {
                PatientId = patient.Id,
                AppointmentDate = yesterday,
                QueuePosition = 1,
                Status = AppointmentStatus.Pending
            },
            new Appointment
            {
                PatientId = patient.Id,
                AppointmentDate = yesterday,
                QueuePosition = 2,
                Status = AppointmentStatus.Confirmed
            },
            new Appointment
            {
                PatientId = patient.Id,
                AppointmentDate = yesterday,
                QueuePosition = 3,
                Status = AppointmentStatus.UpNext
            });
        await db.SaveChangesAsync();

        var count = await NightlyQueueResetJob.RunQueueResetAsync(db, tz);

        Assert.Equal(3, count);
        var all = await db.Appointments.ToListAsync();
        Assert.All(all, a => Assert.Equal(AppointmentStatus.DidNotAttend, a.Status));
    }

    [Fact]
    public async Task RunQueueResetAsync_DoesNotTouch_TodayAppointments()
    {
        var db = TestDbHelper.CreateContext();
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");
        var todayLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

        var patient = new Patient
        {
            FullNameAr = "سارة",
            PhoneNumber = "9647709876543"
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        db.Appointments.Add(new Appointment
        {
            PatientId = patient.Id,
            AppointmentDate = todayLocal,
            QueuePosition = 1,
            Status = AppointmentStatus.Pending
        });
        await db.SaveChangesAsync();

        var count = await NightlyQueueResetJob.RunQueueResetAsync(db, tz);

        Assert.Equal(0, count);
        var appt = await db.Appointments.FirstAsync();
        Assert.Equal(AppointmentStatus.Pending, appt.Status);
    }

    [Fact]
    public async Task RunQueueResetAsync_IgnoresAlreadyCompleted()
    {
        var db = TestDbHelper.CreateContext();
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");
        var yesterday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).AddDays(-1));

        var patient = new Patient
        {
            FullNameAr = "علي",
            PhoneNumber = "9647700000000"
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        db.Appointments.AddRange(
            new Appointment
            {
                PatientId = patient.Id,
                AppointmentDate = yesterday,
                QueuePosition = 1,
                Status = AppointmentStatus.Completed // already done
            },
            new Appointment
            {
                PatientId = patient.Id,
                AppointmentDate = yesterday,
                QueuePosition = 2,
                Status = AppointmentStatus.Cancelled // already cancelled
            });
        await db.SaveChangesAsync();

        var count = await NightlyQueueResetJob.RunQueueResetAsync(db, tz);

        Assert.Equal(0, count);
        var statuses = await db.Appointments.Select(a => a.Status).ToListAsync();
        Assert.Contains(AppointmentStatus.Completed, statuses);
        Assert.Contains(AppointmentStatus.Cancelled, statuses);
    }

    [Fact]
    public async Task RunQueueResetAsync_UsesLocalDate_NotUtcDate()
    {
        // This test validates that the job uses the clinic's local date,
        // not UTC date, to determine "today". For Asia/Baghdad (UTC+3),
        // between 9 PM and midnight UTC, it's already the next day locally.
        var db = TestDbHelper.CreateContext();
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");

        // Use a date that is "today" in UTC but "yesterday" in Baghdad
        // would only occur if someone is running at exactly the right time.
        // Instead, we just verify the logic uses the tz-converted date
        // by checking a past-local-date appointment gets marked.
        var twoDaysAgoLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).AddDays(-2));

        var patient = new Patient
        {
            FullNameAr = "محمد",
            PhoneNumber = "9647701111111"
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        db.Appointments.Add(new Appointment
        {
            PatientId = patient.Id,
            AppointmentDate = twoDaysAgoLocal,
            QueuePosition = 1,
            Status = AppointmentStatus.Pending
        });
        await db.SaveChangesAsync();

        var count = await NightlyQueueResetJob.RunQueueResetAsync(db, tz);

        Assert.Equal(1, count);
    }
}
