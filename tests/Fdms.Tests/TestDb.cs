using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fdms.Tests
{
    /// <summary>
    /// End-to-end tests use an in-memory database by default. Set FDMS_TEST_MYSQL to a server connection string
    /// (e.g. "Server=127.0.0.1;Port=3399;User=root;Password=;") to run the same tests against real MySQL, each fixture in its own database.
    /// </summary>
    public static class TestDb
    {
        public static void Use(IServiceCollection s, string name)
        {
            s.RemoveAll<DbContextOptions<FdmsDbContext>>();
            s.RemoveAll<DbContextOptions>();

            var server = Environment.GetEnvironmentVariable("FDMS_TEST_MYSQL");
            if (string.IsNullOrWhiteSpace(server)) { s.AddDbContext<FdmsDbContext>(o => o.UseInMemoryDatabase(name)); return; }

            var cs = $"{server.TrimEnd(';')};Database=fdms_test_{name.Replace("-", "")[..12]};SslMode=None;";
            s.AddDbContext<FdmsDbContext>(o => o.UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36))));
            using var ctx = new FdmsDbContext(new DbContextOptionsBuilder<FdmsDbContext>().UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36))).Options);
            ctx.Database.EnsureDeleted();
            ctx.Database.EnsureCreated();
        }
    }
}
