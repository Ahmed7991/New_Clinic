using ClinicApi.Controllers;
using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Hubs;
using ClinicApi.Models;
using ClinicApi.Services;
using ClinicApi.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace ClinicApi.Tests;

public class DoctorViewTests
{
    private readonly ClinicDbContext _db;
    private readonly QueueController _controller;

    public DoctorViewTests()
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

        // Seed required clinic settings
        if (!_db.ClinicSettings.Any())
        {
            _db.ClinicSettings.Add(new ClinicSettings
            {
                Id = 1,
                ClinicName = "Test",
                AvgConsultationMinutes = 15,
                DefaultStartTime = new TimeOnly(8, 30),
                DefaultEndTime = new TimeOnly(16, 0),
                WeeklyOffDays = "5"
            });
            _db.SaveChanges();
        }
    }

    private Patient SeedPatient(string name, string phone)
    {
        var p = new Patient { FullNameAr = name, PhoneNumber = phone };
        _db.Patients.Add(p);
        _db.SaveChanges();
        return p;
    }

    private Appointment SeedAppointment(
        Patient patient, int position, AppointmentStatus status,
        DateOnly? date = null)
    {
        var d = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = new TimeOnly(8, 30).AddMinutes((position - 1) * 15);
        var appt = new Appointment
        {
            PatientId = patient.Id,
            AppointmentDate = d,
            QueuePosition = position,
            EstimatedStart = start,
            EstimatedEnd = start.AddMinutes(15),
            Status = status,
            EnteredRoomAt = status == AppointmentStatus.InRoom ? DateTime.UtcNow : null
        };
        _db.Appointments.Add(appt);
        _db.SaveChanges();
        return appt;
    }

    [Fact]
    public async Task DoctorView_ReturnsInRoomAsCurrentPatient()
    {
        var p1 = SeedPatient("أحمد", "9641111111");
        var p2 = SeedPatient("سارة", "9642222222");

        SeedAppointment(p1, 1, AppointmentStatus.InRoom);
        SeedAppointment(p2, 2, AppointmentStatus.UpNext);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.NotNull(dto.CurrentPatient);
        Assert.Equal("أحمد", dto.CurrentPatient!.PatientName);
        Assert.Equal("InRoom", dto.CurrentPatient.Status);
    }

    [Fact]
    public async Task DoctorView_ReturnsUpNextAsUpNextPatient()
    {
        var p1 = SeedPatient("أحمد", "9641111111");
        var p2 = SeedPatient("سارة", "9642222222");

        SeedAppointment(p1, 1, AppointmentStatus.InRoom);
        SeedAppointment(p2, 2, AppointmentStatus.UpNext);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.NotNull(dto.UpNext);
        Assert.Equal("سارة", dto.UpNext!.PatientName);
        Assert.Equal("UpNext", dto.UpNext.Status);
    }

    [Fact]
    public async Task DoctorView_ReturnsBothPatients()
    {
        var p1 = SeedPatient("أحمد", "9641111111");
        var p2 = SeedPatient("سارة", "9642222222");
        var p3 = SeedPatient("علي", "9643333333");

        SeedAppointment(p1, 1, AppointmentStatus.Completed);
        SeedAppointment(p2, 2, AppointmentStatus.InRoom);
        SeedAppointment(p3, 3, AppointmentStatus.UpNext);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.NotNull(dto.CurrentPatient);
        Assert.NotNull(dto.UpNext);
        Assert.Equal("سارة", dto.CurrentPatient!.PatientName);
        Assert.Equal("علي", dto.UpNext!.PatientName);
    }

    [Fact]
    public async Task DoctorView_FallsBackToPendingWhenNoUpNext()
    {
        var p1 = SeedPatient("أحمد", "9641111111");
        var p2 = SeedPatient("سارة", "9642222222");

        SeedAppointment(p1, 1, AppointmentStatus.InRoom);
        SeedAppointment(p2, 2, AppointmentStatus.Pending);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.NotNull(dto.CurrentPatient);
        Assert.NotNull(dto.UpNext);
        Assert.Equal("سارة", dto.UpNext!.PatientName);
        Assert.Equal("Pending", dto.UpNext.Status);
    }

    [Fact]
    public async Task DoctorView_SkipsSteppedOutForUpNext()
    {
        var p1 = SeedPatient("أحمد", "9641111111");
        var p2 = SeedPatient("سارة", "9642222222");

        SeedAppointment(p1, 1, AppointmentStatus.InRoom);
        SeedAppointment(p2, 2, AppointmentStatus.SteppedOut);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.NotNull(dto.CurrentPatient);
        Assert.Null(dto.UpNext); // Because there is no Pending/Confirmed/UpNext to pick up
    }

    [Fact]
    public async Task DoctorView_ReturnsNullsWhenQueueEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.Null(dto.CurrentPatient);
        Assert.Null(dto.UpNext);
    }

    [Fact]
    public async Task DoctorView_IgnoresCancelledAndCompleted()
    {
        var p1 = SeedPatient("أحمد", "9641111111");
        var p2 = SeedPatient("سارة", "9642222222");

        SeedAppointment(p1, 1, AppointmentStatus.Cancelled);
        SeedAppointment(p2, 2, AppointmentStatus.Completed);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dto = await _controller.BuildDoctorViewAsync(today);

        Assert.Null(dto.CurrentPatient);
        Assert.Null(dto.UpNext);
    }
}
