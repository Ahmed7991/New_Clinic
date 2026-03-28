using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Models;
using ClinicApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/patients")]
[Authorize]
public class PatientsController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly AuditService _audit;

    public PatientsController(ClinicDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    // ── GET /api/patients?page=1&pageSize=20&q= ─────────────────────────────
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? q = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _db.Patients.Include(p => p.Appointments).AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            int? parsedId = int.TryParse(term, out var pid) ? pid : null;
            query = query.Where(p => p.FullNameAr.Contains(term)
                                  || p.PhoneNumber.Contains(term)
                                  || (parsedId.HasValue && p.Id == parsedId.Value));
        }

        var totalCount = await query.CountAsync();
        var patients = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.FullNameAr,
                p.PhoneNumber,
                dateOfBirth = p.DateOfBirth.HasValue ? p.DateOfBirth.Value.ToString("yyyy-MM-dd") : null,
                gender = p.Gender.ToString(),
                totalVisits = p.Appointments.Count,
                p.CreatedAt
            })
            .ToListAsync();

        return Ok(new { totalCount, page, pageSize, patients });
    }

    // ── GET /api/patients/search?q= ─────────────────────────────────────────
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 1)
            return Ok(Array.Empty<object>());

        var term = q.Trim();
        int? parsedId = int.TryParse(term, out var id) ? id : null;

        var patients = await _db.Patients
            .Include(p => p.Appointments)
            .Where(p => p.FullNameAr.Contains(term)
                     || p.PhoneNumber.Contains(term)
                     || (parsedId.HasValue && p.Id == parsedId.Value))
            .Take(20)
            .Select(p => new
            {
                p.Id,
                p.FullNameAr,
                p.PhoneNumber,
                totalVisits = p.Appointments.Count
            })
            .ToListAsync();

        return Ok(patients);
    }

    // ── POST /api/patients ──────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePatientRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FullNameAr) || string.IsNullOrWhiteSpace(req.PhoneNumber))
            return BadRequest("Name and phone are required.");

        var exists = await _db.Patients.AnyAsync(p => p.PhoneNumber == req.PhoneNumber.Trim());
        if (exists) return Conflict("رقم الهاتف مسجل مسبقاً");

        var patient = new Patient
        {
            FullNameAr = req.FullNameAr.Trim(),
            PhoneNumber = req.PhoneNumber.Trim(),
            DateOfBirth = req.DateOfBirth,
            Gender = req.Gender ?? Gender.Unknown
        };
        _db.Patients.Add(patient);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("إضافة مريض", $"{patient.FullNameAr} ({patient.PhoneNumber})", HttpContext);
        return Ok(new { patient.Id });
    }

    // ── PUT /api/patients/{id} ──────────────────────────────────────────────
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdatePatientRequest req)
    {
        var patient = await _db.Patients.FindAsync(id);
        if (patient is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.FullNameAr)) patient.FullNameAr = req.FullNameAr.Trim();
        if (!string.IsNullOrWhiteSpace(req.PhoneNumber)) patient.PhoneNumber = req.PhoneNumber.Trim();
        patient.DateOfBirth = req.DateOfBirth;
        if (req.Gender.HasValue) patient.Gender = req.Gender.Value;

        await _db.SaveChangesAsync();
        await _audit.LogAsync("تعديل مريض", $"مريض #{id} ({patient.FullNameAr})", HttpContext);
        return Ok();
    }

    // ── GET /api/patients/{id} ──────────────────────────────────────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> GetPatient(int id)
    {
        var patient = await _db.Patients
            .Include(p => p.Appointments)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (patient is null) return NotFound();

        int? age = null;
        if (patient.DateOfBirth.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var dob = patient.DateOfBirth.Value;
            age = today.Year - dob.Year;
            if (today < dob.AddYears(age.Value)) age--;
        }

        return Ok(new
        {
            patient.Id,
            patient.FullNameAr,
            patient.PhoneNumber,
            dateOfBirth = patient.DateOfBirth?.ToString("yyyy-MM-dd"),
            age,
            gender = patient.Gender.ToString(),
            patient.CreatedAt,
            totalVisits = patient.Appointments.Count,
            appointments = patient.Appointments
                .OrderByDescending(a => a.AppointmentDate)
                .ThenBy(a => a.QueuePosition)
                .Select(a => new
                {
                    a.Id,
                    date = a.AppointmentDate.ToString("yyyy-MM-dd"),
                    a.QueuePosition,
                    estimatedTimeRange = a.EstimatedStart.ToString("hh:mm tt") + " - " + a.EstimatedEnd.ToString("hh:mm tt"),
                    status = a.Status.ToString(),
                    a.Notes
                })
        });
    }
}
