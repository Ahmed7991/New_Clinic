using ClinicApi.Data;
using ClinicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicApi.Tests.Helpers;

public static class TestDbHelper
{
    /// <summary>
    /// Creates a fresh in-memory ClinicDbContext with seeded ClinicSettings.
    /// Each call gets a unique database name so tests don't interfere.
    /// </summary>
    public static ClinicDbContext CreateContext(string? dbName = null)
    {
        dbName ??= Guid.NewGuid().ToString();

        var options = new DbContextOptionsBuilder<ClinicDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var db = new ClinicDbContext(options);
        db.Database.EnsureCreated(); // triggers OnModelCreating + HasData seed
        return db;
    }
}
