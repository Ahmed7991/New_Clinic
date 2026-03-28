using System.Net.Http.Json;

namespace ClinicApi.Services;

public class WhatsAppSender
{
    private readonly HttpClient _http;
    private readonly string _phoneNumberId;

    public WhatsAppSender(HttpClient http, IConfiguration config)
    {
        _phoneNumberId = config["WhatsApp:PhoneNumberId"]
            ?? throw new InvalidOperationException("WhatsApp:PhoneNumberId not configured");

        var token = config["WhatsApp:AccessToken"]
            ?? throw new InvalidOperationException("WhatsApp:AccessToken not configured");

        _http = http;
        _http.BaseAddress = new Uri($"https://graph.facebook.com/v19.0/{_phoneNumberId}/");
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }

    /// <summary>Send a text message to a WhatsApp user.</summary>
    public virtual async Task SendAsync(string recipientWaId, string text)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            to = recipientWaId,
            type = "text",
            text = new { body = text }
        };

        var response = await _http.PostAsJsonAsync("messages", payload);
        response.EnsureSuccessStatusCode();
    }
}
