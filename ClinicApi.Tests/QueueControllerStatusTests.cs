using ClinicApi.Controllers;
using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Hubs;
using ClinicApi.Models;
using ClinicApi.Services;
using ClinicApi.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace ClinicApi.Tests;

public class QueueControllerStatusTests
{
    private readonly ClinicDbContext _db;
    private readonly QueueController _controller;

    public QueueControllerStatusTests()
    {
        _db = TestDbHelper.CreateContext();
        var mockContext = new DefaultHttpContext();
        _controller = new QueueController(
            _db,
            Substitute.For<BookingService>(_db),
            Substitute.For<NotificationService>(_db, Substitute.For<WhatsAppSender>(new HttpClient(), Substitute.For<IConfiguration>())),
            Substitute.For<IHubContext<QueueHub>>(),
            Substitute.For<AuditService>(_db)
        )
        {
            ControllerContext = new ControllerContext { HttpContext = mockContext }
        };
    }

    private Appointment SeedAppointment(AppointmentStatus status)
    {
        var p = new Patient { FullNameAr = "Test", PhoneNumber = "123" };
        _db.Patients.Add(p);
        _db.SaveChanges();

        var appt = new Appointment
        {
            PatientId = p.Id,
            AppointmentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            QueuePosition = 1,
            Status = status
        };
        _db.Appointments.Add(appt);
        _db.SaveChanges();

        return appt;
    }

    [Theory]
    [InlineData(AppointmentStatus.Pending)]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.UpNext)]
    public async Task UpdateStatus_AllowsManualNoShow_FromValidStates(AppointmentStatus fromState)
    {
        var appt = SeedAppointment(fromState);

        var result = await _controller.UpdateStatus(appt.Id, new StatusUpdateRequest(AppointmentStatus.DidNotAttend));

        Assert.IsType<OkResult>(result);
        Assert.Equal(AppointmentStatus.DidNotAttend, appt.Status);
    }

    [Fact]
    public async Task UpdateStatus_BlocksManualNoShow_FromInRoom()
    {
        var appt = SeedAppointment(AppointmentStatus.InRoom);

        var result = await _controller.UpdateStatus(appt.Id, new StatusUpdateRequest(AppointmentStatus.DidNotAttend));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(AppointmentStatus.InRoom, appt.Status);
    }

    [Theory]
    [InlineData(AppointmentStatus.Confirmed)]
    [InlineData(AppointmentStatus.UpNext)]
    public async Task UpdateStatus_AllowsStepOut_FromValidStates(AppointmentStatus fromState)
    {
        var appt = SeedAppointment(fromState);

        var result = await _controller.UpdateStatus(appt.Id, new StatusUpdateRequest(AppointmentStatus.SteppedOut));

        Assert.IsType<OkResult>(result);
        Assert.Equal(AppointmentStatus.SteppedOut, appt.Status);
    }

    [Theory]
    [InlineData(AppointmentStatus.UpNext)]
    [InlineData(AppointmentStatus.InRoom)]
    public async Task UpdateStatus_AllowsReturnFromStepOut_ToValidStates(AppointmentStatus toState)
    {
        var appt = SeedAppointment(AppointmentStatus.SteppedOut);

        var result = await _controller.UpdateStatus(appt.Id, new StatusUpdateRequest(toState));

        Assert.IsType<OkResult>(result);
        Assert.Equal(toState, appt.Status);
    }
}
