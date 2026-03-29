using System.Collections.Concurrent;
using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Services;

public class BookingService
{
    private readonly ClinicDbContext _db;
    private static readonly ConcurrentDictionary<DateOnly, SemaphoreSlim> _dateLocks = new();

    public BookingService(ClinicDbContext db) => _db = db;

    private SemaphoreSlim GetLockForDate(DateOnly date)
    {
        return _dateLocks.GetOrAdd(date, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Finds or creates a patient, then assigns the very next available slot.
    /// Uses a date-specific semaphore to prevent race conditions on slot assignment.
    /// </summary>
    public virtual async Task<BookingResponse> BookNextSlotAsync(BookingRequest req)
    {
        // 1. Find or create patient (outside of the date-specific lock to minimize lock contention)
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.PhoneNumber == req.PhoneNumber);

        if (patient is null)
        {
            patient = new Patient
            {
                FullNameAr = req.FullNameAr,
                PhoneNumber = req.PhoneNumber,
                WhatsAppId = req.WaId
            };
            _db.Patients.Add(patient);
            await _db.SaveChangesAsync();
        }
        else if (req.WaId is not null)
        {
            patient.WhatsAppId = req.WaId;
            // update WhatsAppId optionally, skip save here until appointment to save DB trips if desired.
        }

        var settings = await GetSettingsAsync();
        DateOnly searchAfter = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        while (true) // Keep searching until we successfully book
        {
            var (targetDate, startTime, endTime) = await FindNextAvailableDateAsync(settings, searchAfter.AddDays(1));

            var lockObj = GetLockForDate(targetDate);
            await lockObj.WaitAsync();
            try
            {
                int lastPosition = await _db.Appointments
                    .Where(a => a.AppointmentDate == targetDate)
                    .MaxAsync(a => (int?)a.QueuePosition) ?? 0;

                int newPosition = lastPosition + 1;

                var estStart = startTime.AddMinutes((newPosition - 1) * settings.AvgConsultationMinutes);
                var estEnd = estStart.AddMinutes(settings.AvgConsultationMinutes);

                if (estStart >= endTime)
                {
                    // Day is full - release lock and search next day
                    searchAfter = targetDate;
                    continue;
                }

                var appt = new Appointment
                {
                    PatientId = patient.Id,
                    AppointmentDate = targetDate,
                    QueuePosition = newPosition,
                    EstimatedStart = estStart,
                    EstimatedEnd = estEnd,
                    Status = AppointmentStatus.Pending
                };
                _db.Appointments.Add(appt);
                await _db.SaveChangesAsync();

                return new BookingResponse(
                    appt.Id,
                    patient.FullNameAr,
                    targetDate.ToString("yyyy-MM-dd"),
                    $"{estStart.ToString("hh:mm tt")} - {estEnd.ToString("hh:mm tt")}",
                    newPosition
                );
            }
            finally
            {
                lockObj.Release();
            }
        }
    }

    /// <summary>Walk forward to find the next working day that isn't blocked/off.</summary>
    public async Task<(DateOnly Date, TimeOnly Start, TimeOnly End)> FindNextAvailableDateAsync(
        ClinicSettings settings, DateOnly? afterDate = null)
    {
        var offDays = settings.WeeklyOffDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => (DayOfWeek)int.Parse(d.Trim()))
            .ToHashSet();

        var date = afterDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        for (int i = 0; i < 365; i++) // safety cap
        {
            if (offDays.Contains(date.DayOfWeek))
            {
                date = date.AddDays(1);
                continue;
            }

            var calDay = await _db.CalendarDays
                .FirstOrDefaultAsync(c => c.Date == date);

            if (calDay?.Type is DayType.Blocked or DayType.OffDay)
            {
                date = date.AddDays(1);
                continue;
            }

            var start = calDay?.StartTime ?? settings.DefaultStartTime;
            var end = calDay?.EndTime ?? settings.DefaultEndTime;

            return (date, start, end);
        }

        throw new InvalidOperationException("No available date found within the next year.");
    }


    /// <summary>
    /// Appends a walk-in to today's queue regardless of capacity.
    /// Walk-ins are physically present, so they must always go on today.
    /// </summary>
    public async Task<BookingResponse> BookWalkInForTodayAsync(string fullNameAr, string phoneNumber, DateOnly? dateOfBirth = null, Gender? gender = null)
    {
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.PhoneNumber == phoneNumber);

        if (patient is null)
        {
            patient = new Patient
            {
                FullNameAr = fullNameAr,
                PhoneNumber = phoneNumber,
                DateOfBirth = dateOfBirth,
                Gender = gender ?? Gender.Unknown
            };
            _db.Patients.Add(patient);
            await _db.SaveChangesAsync();
        }
        else
        {
            bool changed = false;
            if (dateOfBirth.HasValue && !patient.DateOfBirth.HasValue)
            {
                patient.DateOfBirth = dateOfBirth;
                changed = true;
            }
            if (gender.HasValue && gender.Value != Gender.Unknown && patient.Gender == Gender.Unknown)
            {
                patient.Gender = gender.Value;
                changed = true;
            }
            if (changed) await _db.SaveChangesAsync();
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var settings = await GetSettingsAsync();

        var lockObj = GetLockForDate(today);
        await lockObj.WaitAsync();
        try
        {
            int lastPosition = await _db.Appointments
                .Where(a => a.AppointmentDate == today)
                .MaxAsync(a => (int?)a.QueuePosition) ?? 0;

            int newPosition = lastPosition + 1;

            var startTime = settings.DefaultStartTime;
            var calDay = await _db.CalendarDays
                .FirstOrDefaultAsync(c => c.Date == today);
            if (calDay?.StartTime is not null)
                startTime = calDay.StartTime.Value;

            var estStart = startTime.AddMinutes((newPosition - 1) * settings.AvgConsultationMinutes);
            var estEnd = estStart.AddMinutes(settings.AvgConsultationMinutes);

            var appt = new Appointment
            {
                PatientId = patient.Id,
                AppointmentDate = today,
                QueuePosition = newPosition,
                EstimatedStart = estStart,
                EstimatedEnd = estEnd,
                Status = AppointmentStatus.Pending,
                IsWalkIn = true
            };
            _db.Appointments.Add(appt);
            await _db.SaveChangesAsync();

            return new BookingResponse(
                appt.Id,
                patient.FullNameAr,
                today.ToString("yyyy-MM-dd"),
                $"{estStart.ToString("hh:mm tt")} - {estEnd.ToString("hh:mm tt")}",
                newPosition
            );
        }
        finally
        {
            lockObj.Release();
        }
    }

    public virtual async Task<Patient?> FindReturningPatientAsync(string phone)
        => await _db.Patients.FirstOrDefaultAsync(p => p.PhoneNumber == phone);

    public async Task<ClinicSettings> GetSettingsAsync()
        => await _db.ClinicSettings.FindAsync(1)
           ?? throw new InvalidOperationException("Clinic settings not seeded.");
}
