using System.Collections.Concurrent;
using ClinicApi.Data;
using ClinicApi.DTOs;
using ClinicApi.Models;
using ClinicApi.Services;
using ClinicApi.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClinicApi.Tests;

public class BookingServiceConcurrencyTests
{
    [Fact]
    public async Task BookNextSlotAsync_ConcurrentRequests_ShouldNotAssignDuplicatePositions()
    {
        // Use a real db context per service to mimic transient scope, but same underlying memory db root if possible,
        // or just rely on the new scope creating the context properly.
        // For accurate EF Core concurrency tests, InMemory database is not always perfect for locks,
        // but since our locking is static (in the BookingService class), it will lock threads sharing the AppDomain.

        var options = new DbContextOptionsBuilder<ClinicDbContext>()
            .UseInMemoryDatabase(databaseName: "ConcurrencyTestDb")
            .Options;

        // Seed settings
        using (var db = new ClinicDbContext(options))
        {
            db.Database.EnsureDeleted();
            db.Database.EnsureCreated();

            // Seed clinic settings if not exists (EnsureCreated might do it but just in case)
            if (!db.ClinicSettings.Any())
            {
                db.ClinicSettings.Add(new ClinicSettings
                {
                    Id = 1,
                    ClinicName = "Test",
                    AvgConsultationMinutes = 15,
                    DefaultStartTime = new TimeOnly(8, 0),
                    DefaultEndTime = new TimeOnly(16, 0),
                    WeeklyOffDays = "5"
                });
                db.SaveChanges();
            }
        }

        int totalRequests = 50;
        var tasks = new List<Task<BookingResponse>>();

        // Ensure all threads start at roughly the same time to maximize concurrency contention
        var startEvent = new ManualResetEventSlim(false);

        for (int i = 0; i < totalRequests; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                startEvent.Wait(); // wait for the signal
                using var db = new ClinicDbContext(options);
                var service = new BookingService(db);
                var req = new BookingRequest($"مريض {index}", $"964770{index:D7}", null);
                return await service.BookNextSlotAsync(req);
            }));
        }

        startEvent.Set(); // Release all tasks simultaneously
        var results = await Task.WhenAll(tasks);

        using (var db = new ClinicDbContext(options))
        {
            var appointments = await db.Appointments.ToListAsync();

            // 1. Verify we successfully created exactly 'totalRequests' appointments
            Assert.Equal(totalRequests, appointments.Count);

            // 2. Verify there are no duplicate (AppointmentDate, QueuePosition) pairs
            var duplicates = appointments
                .GroupBy(a => new { a.AppointmentDate, a.QueuePosition })
                .Where(g => g.Count() > 1)
                .ToList();

            Assert.Empty(duplicates);

            // 3. Verify positions are contiguous for each day
            var days = appointments.GroupBy(a => a.AppointmentDate).ToList();
            foreach (var day in days)
            {
                var positions = day.Select(a => a.QueuePosition).OrderBy(p => p).ToList();
                for (int i = 0; i < positions.Count; i++)
                {
                    Assert.Equal(i + 1, positions[i]);
                }
            }
        }
    }
}
