using ClinicApi.DTOs;
using ClinicApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/webhook/whatsapp")]
[AllowAnonymous]
public class WhatsAppWebhookController : ControllerBase
{
    private readonly WhatsAppFunnelService _funnel;
    private readonly IConfiguration _config;

    public WhatsAppWebhookController(WhatsAppFunnelService funnel, IConfiguration config)
    {
        _funnel = funnel;
        _config = config;
    }

    /// <summary>Meta webhook verification (GET).</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string mode,
        [FromQuery(Name = "hub.verify_token")] string token,
        [FromQuery(Name = "hub.challenge")] string challenge)
    {
        var expected = _config["WhatsApp:VerifyToken"];
        if (mode == "subscribe" && token == expected)
            return Ok(challenge);

        return Forbid();
    }

    /// <summary>Incoming message handler (POST).</summary>
    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] JsonElement body)
    {
        try
        {
            // Navigate Meta's nested webhook payload
            var entry = body.GetProperty("entry")[0];
            var changes = entry.GetProperty("changes")[0];
            var value = changes.GetProperty("value");

            if (!value.TryGetProperty("messages", out var messages))
                return Ok(); // Status update, not a message

            var msg = messages[0];
            if (msg.GetProperty("type").GetString() != "text")
                return Ok(); // Ignore non-text (images, voice, etc.)

            var waId = msg.GetProperty("from").GetString()!;
            var text = msg.GetProperty("text").GetProperty("body").GetString()!;

            await _funnel.HandleIncomingAsync(new WhatsAppIncoming(waId, text));
        }
        catch (Exception ex)
        {
            // Log but always return 200 to Meta (prevents retries)
            Console.Error.WriteLine($"Webhook error: {ex.Message}");
        }

        return Ok();
    }
}
