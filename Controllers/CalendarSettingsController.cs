using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Models;
using ClinicApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Controllers;

// ═══════════════════════════════════════════════════════════════════════════════
// Calendar Controller
// ═══════════════════════════════════════════════════════════════════════════════
[ApiController]
[Route("api/calendar")]
[Authorize(Roles = "Secretary,Admin")]
public class CalendarController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly AuditService _audit;

    public CalendarController(ClinicDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>Get calendar overrides for a month.</summary>
    [HttpGet("{year}/{month}")]
    public async Task<ActionResult<List<CalendarDay>>> GetMonth(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1);

        var days = await _db.CalendarDays
            .Where(c => c.Date >= start && c.Date < end)
            .OrderBy(c => c.Date)
            .ToListAsync();

        return Ok(days);
    }

    /// <summary>Block a day — new bookings skip to the next available day.</summary>
    [HttpPost("block")]
    public async Task<IActionResult> BlockDay([FromBody] BlockDayRequest req)
    {
        var existing = await _db.CalendarDays
            .FirstOrDefaultAsync(c => c.Date == req.Date);

        if (existing is not null)
        {
            existing.Type = DayType.Blocked;
            existing.Note = req.Note;
        }
        else
        {
            _db.CalendarDays.Add(new CalendarDay
            {
                Date = req.Date,
                Type = DayType.Blocked,
                Note = req.Note
            });
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync("حظر يوم", $"التاريخ: {req.Date}", HttpContext);
        return Ok();
    }

    /// <summary>Unblock a day.</summary>
    [HttpPost("unblock")]
    public async Task<IActionResult> UnblockDay([FromBody] BlockDayRequest req)
    {
        var existing = await _db.CalendarDays
            .FirstOrDefaultAsync(c => c.Date == req.Date);

        if (existing is not null)
        {
            existing.Type = DayType.Normal;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("إلغاء حظر يوم", $"التاريخ: {req.Date}", HttpContext);
        }

        return Ok();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Settings Controller
// ═══════════════════════════════════════════════════════════════════════════════
[ApiController]
[Route("api/settings")]
[Authorize(Roles = "Secretary,Admin")]
public class SettingsController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly AuditService _audit;

    public SettingsController(ClinicDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ClinicSettings>> Get()
    {
        var settings = await _db.ClinicSettings.FindAsync(1);
        return settings is not null ? Ok(settings) : NotFound();
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest req)
    {
        var settings = await _db.ClinicSettings.FindAsync(1);
        if (settings is null) return NotFound();

        if (req.AvgConsultationMinutes.HasValue)
            settings.AvgConsultationMinutes = req.AvgConsultationMinutes.Value;
        if (req.DefaultStartTime.HasValue)
            settings.DefaultStartTime = req.DefaultStartTime.Value;
        if (req.DefaultEndTime.HasValue)
            settings.DefaultEndTime = req.DefaultEndTime.Value;
        if (req.WeeklyOffDays is not null)
            settings.WeeklyOffDays = req.WeeklyOffDays;
        if (req.ApproachingAlertOffset.HasValue)
            settings.ApproachingAlertOffset = req.ApproachingAlertOffset.Value;

        await _db.SaveChangesAsync();
        await _audit.LogAsync("تعديل الإعدادات", "تم تحديث إعدادات العيادة", HttpContext);
        return Ok(settings);
    }
}
