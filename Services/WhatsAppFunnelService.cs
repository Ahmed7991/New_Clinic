using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Services;

public class WhatsAppFunnelService
{
    private readonly ClinicDbContext _db;
    private readonly BookingService _booking;
    private readonly WhatsAppSender _whatsApp;

    public WhatsAppFunnelService(
        ClinicDbContext db,
        BookingService booking,
        WhatsAppSender whatsApp)
    {
        _db = db;
        _booking = booking;
        _whatsApp = whatsApp;
    }

    public async Task HandleIncomingAsync(WhatsAppIncoming msg)
    {
        // Get or create session
        var session = await _db.WhatsAppSessions
            .FirstOrDefaultAsync(s => s.WaId == msg.WaId);

        if (session is null)
        {
            session = new WhatsAppSession { WaId = msg.WaId };
            _db.WhatsAppSessions.Add(session);
            await _db.SaveChangesAsync();
        }

        // Expire stale sessions — reset if idle for more than 30 minutes
        if (session.Step != ConversationStep.AwaitingName
            && (DateTime.UtcNow - session.LastMessageAt).TotalMinutes > 30)
        {
            session.Step = ConversationStep.AwaitingName;
            session.CollectedName = null;
            session.CollectedPhone = null;
            session.MatchedPatientId = null;
        }

        session.LastMessageAt = DateTime.UtcNow;

        switch (session.Step)
        {
            case ConversationStep.AwaitingName:
                await HandleNameStepAsync(session, msg);
                break;

            case ConversationStep.AwaitingPhone:
                await HandlePhoneStepAsync(session, msg);
                break;

            case ConversationStep.AwaitingReturningUserChoice:
                await HandleReturningChoiceAsync(session, msg);
                break;

            case ConversationStep.Completed:
                // Reset for a new booking
                session.Step = ConversationStep.AwaitingName;
                session.CollectedName = null;
                session.CollectedPhone = null;
                session.MatchedPatientId = null;
                await HandleNameStepAsync(session, msg);
                break;
        }

        await _db.SaveChangesAsync();
    }

    // ────────────────────────────────────────────────────────────────────────────
    private async Task HandleNameStepAsync(WhatsAppSession session, WhatsAppIncoming msg)
    {
        var trimmed = msg.MessageBody.Trim();

        if (Regex.IsMatch(trimmed, @"^[\u0600-\u06FF\s]+$") && trimmed.Length >= 2)
        {
            session.CollectedName = trimmed;
            session.Step = ConversationStep.AwaitingPhone;
            await _whatsApp.SendAsync(session.WaId,
                "شكراً! الرجاء إدخال رقم هاتفك (أرقام فقط).");
        }
        else
        {
            await _whatsApp.SendAsync(session.WaId,
                "عذراً، الرجاء إدخال اسمك الكامل بالحروف العربية فقط، بدون أرقام أو حروف إنجليزية.");
        }
    }

    private async Task HandlePhoneStepAsync(WhatsAppSession session, WhatsAppIncoming msg)
    {
        // Extract digits only
        string digits = Regex.Replace(msg.MessageBody, @"\D", "");

        if (digits.Length < 10 || digits.Length > 15)
        {
            await _whatsApp.SendAsync(session.WaId,
                "الرجاء إدخال رقم هاتف صحيح (10-15 رقم).");
            return;
        }

        session.CollectedPhone = digits;

        // Check for returning patient
        var existing = await _booking.FindReturningPatientAsync(digits);
        if (existing is not null)
        {
            session.MatchedPatientId = existing.Id;
            session.Step = ConversationStep.AwaitingReturningUserChoice;
            await _whatsApp.SendAsync(session.WaId,
                $"هل تريد حجز موعد لـ {existing.FullNameAr}؟\n" +
                "اكتب \"نعم\" أو \"شخص آخر\".");
            return;
        }

        // New patient — proceed to booking
        await CompleteBookingAsync(session);
    }

    private async Task HandleReturningChoiceAsync(WhatsAppSession session, WhatsAppIncoming msg)
    {
        string body = msg.MessageBody.Trim();

        if (body.Contains("نعم") || body.Contains("اي") || body.Contains("نفسه"))
        {
            // Book for existing patient
            var patient = await _db.Patients.FindAsync(session.MatchedPatientId);
            if (patient is not null)
            {
                session.CollectedName = patient.FullNameAr;
                session.CollectedPhone = patient.PhoneNumber;
            }
            await CompleteBookingAsync(session);
        }
        else
        {
            // Someone else — restart name collection
            session.Step = ConversationStep.AwaitingName;
            session.CollectedName = null;
            session.CollectedPhone = null;
            session.MatchedPatientId = null;
            await _whatsApp.SendAsync(session.WaId,
                "حسناً، الرجاء إدخال الاسم الكامل بالعربية.");
        }
    }

    private async Task CompleteBookingAsync(WhatsAppSession session)
    {
        var result = await _booking.BookNextSlotAsync(new BookingRequest(
            session.CollectedName!,
            session.CollectedPhone!,
            session.WaId
        ));

        session.Step = ConversationStep.Completed;

        await _whatsApp.SendAsync(session.WaId,
            $"✅ تم حجز موعدك بنجاح!\n" +
            $"📋 الاسم: {result.PatientName}\n" +
            $"📅 التاريخ: {result.Date}\n" +
            $"🕐 الوقت المتوقع: {result.EstimatedTimeRange}\n\n" +
            "لا يمكن إلغاء أو تعديل الموعد عبر هذه الخدمة. " +
            "يرجى التواصل مع العيادة مباشرة.");
    }
}
