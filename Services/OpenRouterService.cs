using System.Net.Http.Json;
using System.Text.Json;

namespace ClinicApi.Services;

public class OpenRouterService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    // ═══════════════════════════════════════════════════════════════════════════
    // ARABIC SYSTEM PROMPT — Forces the AI into strict gatekeeper mode
    // ═══════════════════════════════════════════════════════════════════════════
    private const string ArabicSystemPrompt = @"
أنت مساعد حجز مواعيد في عيادة طبية. أنت لست روبوت محادثة عام.
مهمتك الوحيدة هي استخراج الاسم الكامل باللغة العربية من رسالة المستخدم والتحقق منه.

## القواعد الصارمة:

1. يجب أن يحتوي الاسم على حروف عربية فقط ومسافات.
2. ارفض فوراً أي إدخال يحتوي على:
   - أرقام (0-9 أو ٠-٩)
   - حروف إنجليزية (a-z, A-Z)
   - رموز أو أحرف خاصة
3. يجب أن يتكون الاسم من كلمتين على الأقل (اسم أول واسم ثاني).
4. لا تجب على أي سؤال أو موضوع خارج نطاق الحجز.

## طريقة الرد:

- إذا كان الاسم صحيحاً: أجب بـ VALID: متبوعاً بالاسم النظيف.
  مثال: VALID: أحمد محمد علي

- إذا كان الاسم غير صحيح: أجب برسالة رفض مختصرة بالعربية توضح السبب.
  مثال: الرجاء إدخال اسمك الكامل بالعربية فقط، بدون أرقام أو حروف إنجليزية.

- إذا أرسل المستخدم رسالة خارج الموضوع أو سؤالاً عاماً: أجب بـ:
  مرحباً! هذه خدمة حجز مواعيد فقط. الرجاء إدخال اسمك الكامل بالعربية لبدء الحجز.

## أمثلة:

رسالة: أحمد محمد → VALID: أحمد محمد
رسالة: Ahmed → الرجاء إدخال اسمك الكامل بالعربية فقط.
رسالة: أحمد123 → الرجاء إدخال اسمك بدون أرقام.
رسالة: كم سعر الكشف؟ → مرحباً! هذه خدمة حجز مواعيد فقط. الرجاء إدخال اسمك الكامل بالعربية لبدء الحجز.
رسالة: أحمد → الرجاء إدخال اسمك الكامل (الاسم الأول واسم العائلة على الأقل).
";

    public OpenRouterService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://openrouter.ai/api/v1/");
        _apiKey = config["OpenRouter:ApiKey"]
            ?? throw new InvalidOperationException("OpenRouter:ApiKey not configured");
        _model = config["OpenRouter:Model"] ?? "google/gemini-2.0-flash-001";

        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    /// <summary>
    /// Sends user message to OpenRouter for Arabic name extraction.
    /// Returns "VALID: Name" or an Arabic rejection message.
    /// </summary>
    public virtual async Task<string> ExtractArabicNameAsync(string userMessage)
    {
        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = ArabicSystemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 200,
            temperature = 0.0
        };

        var response = await _http.PostAsJsonAsync("chat/completions", body);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reply = json
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return reply?.Trim() ?? "حدث خطأ، الرجاء المحاولة مرة أخرى.";
    }
}
