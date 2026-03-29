using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Models;
using ClinicApi.Services;
using ClinicApi.Tests.Helpers;
using NSubstitute;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace ClinicApi.Tests;

public class WhatsAppFunnelServiceTests
{
    private readonly ClinicDbContext _db;
    private readonly BookingService _booking;
    private readonly WhatsAppSender _whatsApp;
    private readonly OpenRouterService _ai;
    private readonly WhatsAppFunnelService _sut;

    public WhatsAppFunnelServiceTests()
    {
        _db = TestDbHelper.CreateContext();
        _booking = Substitute.For<BookingService>(_db);
        _whatsApp = Substitute.For<WhatsAppSender>(new HttpClient(), Substitute.For<IConfiguration>());
        _ai = Substitute.For<OpenRouterService>(new HttpClient(), Substitute.For<IConfiguration>());
        _sut = new WhatsAppFunnelService(_db, _booking, _whatsApp);
    }

    [Fact]
    public async Task HandleIncomingAsync_StaleSession_ResetsToAwaitingName()
    {
        // Arrange — session stuck at AwaitingPhone, last active 31 minutes ago
        var session = new WhatsAppSession
        {
            WaId = "964111222333",
            Step = ConversationStep.AwaitingPhone,
            CollectedName = "أحمد",
            CollectedPhone = "9647701234567",
            MatchedPatientId = 42,
            LastMessageAt = DateTime.UtcNow.AddMinutes(-31)
        };
        _db.WhatsAppSessions.Add(session);
        await _db.SaveChangesAsync();

        // AI returns a valid name so the step handler progresses
        _ai.ExtractArabicNameAsync(Arg.Any<string>()).Returns("VALID: علي");

        // Act
        await _sut.HandleIncomingAsync(new WhatsAppIncoming("964111222333", "علي"));

        // Assert — session was reset to AwaitingName, then processed the new message
        var updated = _db.WhatsAppSessions.First(s => s.WaId == "964111222333");
        Assert.Equal(ConversationStep.AwaitingPhone, updated.Step); // moved past AwaitingName
        Assert.Equal("علي", updated.CollectedName); // new name, not the old one
        Assert.Null(updated.CollectedPhone);          // was cleared on reset
        Assert.Null(updated.MatchedPatientId);         // was cleared on reset
    }

    [Fact]
    public async Task HandleIncomingAsync_FreshSession_DoesNotReset()
    {
        // Arrange — session at AwaitingPhone, last active 29 minutes ago (still fresh)
        var session = new WhatsAppSession
        {
            WaId = "964444555666",
            Step = ConversationStep.AwaitingPhone,
            CollectedName = "أحمد",
            LastMessageAt = DateTime.UtcNow.AddMinutes(-29)
        };
        _db.WhatsAppSessions.Add(session);
        await _db.SaveChangesAsync();

        // Phone step expects digits — provide a valid phone number
        _booking.FindReturningPatientAsync(Arg.Any<string>())
            .Returns((Patient?)null);
        _booking.BookNextSlotAsync(Arg.Any<BookingRequest>())
            .Returns(new BookingResponse(1, "أحمد", "2026-03-23", "09:00 - 09:15", 1));

        // Act
        await _sut.HandleIncomingAsync(new WhatsAppIncoming("964444555666", "9647701234567"));

        // Assert — session was NOT reset; phone step processed normally
        var updated = _db.WhatsAppSessions.First(s => s.WaId == "964444555666");
        Assert.Equal("أحمد", updated.CollectedName); // original name preserved
    }

    [Fact]
    public async Task HandleIncomingAsync_StaleButAlreadyAwaitingName_NoReset()
    {
        // Arrange — session at AwaitingName with stale timestamp (nothing to reset)
        var session = new WhatsAppSession
        {
            WaId = "964777888999",
            Step = ConversationStep.AwaitingName,
            LastMessageAt = DateTime.UtcNow.AddMinutes(-60)
        };
        _db.WhatsAppSessions.Add(session);
        await _db.SaveChangesAsync();

        _ai.ExtractArabicNameAsync(Arg.Any<string>()).Returns("VALID: سارة");

        // Act
        await _sut.HandleIncomingAsync(new WhatsAppIncoming("964777888999", "سارة"));

        // Assert — processed normally, no crash
        var updated = _db.WhatsAppSessions.First(s => s.WaId == "964777888999");
        Assert.Equal(ConversationStep.AwaitingPhone, updated.Step);
        Assert.Equal("سارة", updated.CollectedName);
    }

    [Fact]
    public async Task HandleIncomingAsync_NewSession_CreatesAndProcesses()
    {
        // Arrange — no existing session for this WaId
        _ai.ExtractArabicNameAsync(Arg.Any<string>()).Returns("VALID: محمد");

        // Act
        await _sut.HandleIncomingAsync(new WhatsAppIncoming("964000111222", "محمد"));

        // Assert — session created and processed
        var session = _db.WhatsAppSessions.First(s => s.WaId == "964000111222");
        Assert.Equal(ConversationStep.AwaitingPhone, session.Step);
        Assert.Equal("محمد", session.CollectedName);
    }

    [Fact]
    public async Task HandleIncomingAsync_ExactlyAt30Minutes_DoesNotReset()
    {
        // Arrange — session at AwaitingPhone, last active exactly 30 minutes ago
        var session = new WhatsAppSession
        {
            WaId = "964333444555",
            Step = ConversationStep.AwaitingPhone,
            CollectedName = "فاطمة",
            LastMessageAt = DateTime.UtcNow.AddMinutes(-29).AddSeconds(-59)
        };
        _db.WhatsAppSessions.Add(session);
        await _db.SaveChangesAsync();

        _booking.FindReturningPatientAsync(Arg.Any<string>())
            .Returns((Patient?)null);
        _booking.BookNextSlotAsync(Arg.Any<BookingRequest>())
            .Returns(new BookingResponse(1, "فاطمة", "2026-03-23", "09:00 - 09:15", 1));

        // Act
        await _sut.HandleIncomingAsync(new WhatsAppIncoming("964333444555", "9647701234567"));

        // Assert — 30 min is NOT > 30, so no reset; phone step processed
        var updated = _db.WhatsAppSessions.First(s => s.WaId == "964333444555");
        Assert.Equal("فاطمة", updated.CollectedName); // preserved
    }
}
