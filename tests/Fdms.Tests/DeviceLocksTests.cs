using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Fdms.Tests
{
    public class DeviceLocksTests
    {
        private static DeviceLocks Make(bool distributed, string? cs = null) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fdms:DistributedLocks"] = distributed ? "true" : "false",
            ["ConnectionStrings:Default"] = cs,
            ["Fdms:LockWaitSeconds"] = "2",
        }).Build());

        [Fact]
        public async Task Local_lock_is_exclusive_per_device_and_independent_across_devices()
        {
            var locks = Make(false);
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            await using var first = await locks.AcquireAsync(a, default);
            Assert.Null(await locks.TryAcquireAsync(a, default));           // same device is busy
            await using (await locks.AcquireAsync(b, default)) { }           // another device is free
            await first.DisposeAsync();
            await using var again = await locks.TryAcquireAsync(a, default); // released
            Assert.NotNull(again);
        }

        [Fact]
        public async Task A_waiter_gets_a_retryable_409_when_the_device_stays_busy()
        {
            var locks = Make(false);
            var id = Guid.NewGuid();
            await using var held = await locks.AcquireAsync(id, default);
            var ex = await Assert.ThrowsAsync<GatewayException>(() => locks.AcquireAsync(id, default));
            Assert.Equal(409, ex.Status);
            Assert.Equal("DEVICE_BUSY", ex.Code);
        }

        [Fact]
        public async Task Two_instances_exclude_each_other_through_mysql()
        {
            var server = Environment.GetEnvironmentVariable("FDMS_TEST_MYSQL");
            if (string.IsNullOrWhiteSpace(server)) return;   // needs a real MySQL (see TestDb)
            var cs = server.TrimEnd(';') + ";SslMode=None;";
            var instanceA = Make(true, cs);
            var instanceB = Make(true, cs);                  // separate semaphores = separate processes
            var id = Guid.NewGuid();

            await using var held = await instanceA.AcquireAsync(id, default);
            Assert.Null(await instanceB.TryAcquireAsync(id, default));
            await held.DisposeAsync();
            await using var got = await instanceB.TryAcquireAsync(id, default);
            Assert.NotNull(got);
        }
    }
}
