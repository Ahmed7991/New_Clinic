using ClinicApi.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize(Roles = "Admin")]
public class AuditController : ControllerBase
{
    private readonly ClinicDbContext _db;

    public AuditController(ClinicDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date)
    {
        var d = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = d.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var logs = await _db.AuditLogs
            .Where(a => a.Timestamp >= start && a.Timestamp < end)
            .OrderByDescending(a => a.Timestamp)
            .Select(a => new
            {
                a.Id,
                timestamp = a.Timestamp,
                a.Username,
                a.Action,
                a.Details
            })
            .ToListAsync();

        return Ok(logs);
    }
}
