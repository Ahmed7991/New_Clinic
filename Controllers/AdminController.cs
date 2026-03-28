using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IConfiguration _config;

    public AdminController(IConfiguration config) => _config = config;

    // ── GET /api/admin/backup ─────────────────────────────────────────────────
    [Authorize(Roles = "Admin,Secretary,Doctor")]
    [HttpGet("backup")]
    public async Task<IActionResult> Backup()
    {
        var connStr = _config.GetConnectionString("Default");
        if (string.IsNullOrEmpty(connStr)) return StatusCode(500, "Connection string not configured.");

        var builder = new NpgsqlConnectionStringBuilder(connStr);
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
        var fileName = $"clinic-backup-{date}.sql";

        var psi = new ProcessStartInfo
        {
            FileName = "pg_dump",
            Arguments = $"-h {builder.Host} -p {builder.Port} -U {builder.Username} -d {builder.Database} --no-owner --no-acl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PGPASSWORD"] = builder.Password;

        using var process = Process.Start(psi);
        if (process is null) return StatusCode(500, "Failed to start pg_dump.");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            return StatusCode(500, $"pg_dump failed: {error}");

        var bytes = System.Text.Encoding.UTF8.GetBytes(output);
        return File(bytes, "application/sql", fileName);
    }

    // ── POST /api/admin/restore ───────────────────────────────────────────────
    [Authorize(Roles = "Admin")]
    [HttpPost("restore")]
    public async Task<IActionResult> Restore(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var connStr = _config.GetConnectionString("Default");
        if (string.IsNullOrEmpty(connStr)) return StatusCode(500, "Connection string not configured.");

        var builder = new NpgsqlConnectionStringBuilder(connStr);

        using var reader = new StreamReader(file.OpenReadStream());
        var sql = await reader.ReadToEndAsync();

        var psi = new ProcessStartInfo
        {
            FileName = "psql",
            Arguments = $"-h {builder.Host} -p {builder.Port} -U {builder.Username} -d {builder.Database}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PGPASSWORD"] = builder.Password;

        using var process = Process.Start(psi);
        if (process is null) return StatusCode(500, "Failed to start psql.");

        await process.StandardInput.WriteAsync(sql);
        process.StandardInput.Close();

        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            return StatusCode(500, $"Restore failed: {error}");

        return Ok(new { message = "تمت استعادة النسخة الاحتياطية بنجاح." });
    }
}
