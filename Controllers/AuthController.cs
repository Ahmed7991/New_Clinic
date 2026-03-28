using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClinicApi.Data;
using ClinicApi.Models;
using ClinicApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ClinicApi.Controllers;

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token);
public record CreateUserRequest(string Username, string Password, UserRole Role);
public record ChangePasswordRequest(string NewPassword);
public record ChangeMyPasswordRequest(string CurrentPassword, string NewPassword);
public record UserDto(int Id, string Username, string Role);

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ClinicDbContext _db;
    private readonly IConfiguration _config;
    private readonly AuditService _audit;

    public AuthController(ClinicDbContext db, IConfiguration config, AuditService audit)
    {
        _db = db;
        _config = config;
        _audit = audit;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req)
    {
        var user = await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Username == req.Username);

        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            await _audit.LogAsync("فشل تسجيل الدخول", $"المستخدم: {req.Username}", HttpContext);
            return Unauthorized(new { message = "Invalid username or password." });
        }

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Key"]
                ?? throw new InvalidOperationException("Jwt:Key not configured")));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        var token = new JwtSecurityToken(
            expires: DateTime.UtcNow.AddHours(8),
            claims: claims,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        await _audit.LogAsync("تسجيل دخول", $"المستخدم: {user.Username} ({user.Role})", HttpContext);
        return Ok(new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token)));
    }

    // ── Change own password (any authenticated user) ──────────────────────────────

    [Authorize]
    [HttpPut("me/password")]
    public async Task<IActionResult> ChangeMyPassword([FromBody] ChangeMyPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { message = "كلمة المرور الجديدة مطلوبة." });

        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.UserAccounts.FindAsync(userId);
        if (user is null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "كلمة المرور الحالية غير صحيحة." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await _db.SaveChangesAsync();
        return Ok(new { message = "تم تغيير كلمة المرور بنجاح." });
    }

    // ── Admin-only user management ───────────────────────────────────────────────

    [Authorize(Roles = "Admin")]
    [HttpGet("users")]
    public async Task<ActionResult<List<UserDto>>> ListUsers()
    {
        var users = await _db.UserAccounts
            .Select(u => new UserDto(u.Id, u.Username, u.Role.ToString()))
            .ToListAsync();
        return Ok(users);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("users")]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Username and password are required." });

        if (await _db.UserAccounts.AnyAsync(u => u.Username == req.Username))
            return Conflict(new { message = "Username already exists." });

        var user = new UserAccount
        {
            Username = req.Username.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Role = req.Role
        };

        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync();

        return Created($"/api/auth/users/{user.Id}",
            new UserDto(user.Id, user.Username, user.Role.ToString()));
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var callerId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (id == callerId)
            return BadRequest(new { message = "Cannot delete your own account." });

        var user = await _db.UserAccounts.FindAsync(id);
        if (user is null) return NotFound();

        _db.UserAccounts.Remove(user);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("users/{id}/password")]
    public async Task<IActionResult> ChangePassword(int id, [FromBody] ChangePasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { message = "Password is required." });

        var user = await _db.UserAccounts.FindAsync(id);
        if (user is null) return NotFound();

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
