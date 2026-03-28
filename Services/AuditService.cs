using System.Security.Claims;
using ClinicApi.Data;
using ClinicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Services;

public class AuditService
{
    private readonly ClinicDbContext _db;

    public AuditService(ClinicDbContext db) => _db = db;

    public async Task LogAsync(string action, string? details, HttpContext httpContext)
    {
        int? userId = null;
        var username = "anonymous";

        var idClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim is not null && int.TryParse(idClaim.Value, out var uid))
            userId = uid;

        var nameClaim = httpContext.User.FindFirst(ClaimTypes.Name);
        if (nameClaim is not null)
            username = nameClaim.Value;

        _db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            Username = username,
            Action = action,
            Details = details
        });
        await _db.SaveChangesAsync();

        // Auto-delete entries older than 30 days
        var cutoff = DateTime.UtcNow.AddDays(-30);
        await _db.AuditLogs.Where(a => a.Timestamp < cutoff).ExecuteDeleteAsync();
    }
}
