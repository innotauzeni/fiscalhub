using System.Collections.Concurrent;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// One lock per device. Receipt numbers (counter, global no, hash chain) must be gapless and strictly ordered, so every
    /// operation that mutates a device's fiscal sequence runs under this lock. It is a process-local semaphore (cheap, fair)
    /// PLUS a MySQL named lock (GET_LOCK), which makes several API instances on one database safe.
    /// Turn the database lock off with Fdms:DistributedLocks=false (single instance / tests on a non-MySQL provider).
    /// </summary>
    public sealed class DeviceLocks
    {
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _local = new();
        private readonly string? _connectionString;
        private readonly TimeSpan _wait;

        public DeviceLocks(IConfiguration cfg)
        {
            var distributed = cfg.GetValue("Fdms:DistributedLocks", true);
            _connectionString = distributed ? cfg.GetConnectionString("Default") : null;
            _wait = TimeSpan.FromSeconds(cfg.GetValue("Fdms:LockWaitSeconds", 45));
        }

        /// <summary>Waits for the device; throws a retryable 409 if it stays busy.</summary>
        public async Task<IAsyncDisposable> AcquireAsync(Guid deviceId, CancellationToken ct) =>
            await TryAsync(deviceId, _wait, ct) ?? throw GatewayException.Conflict("DEVICE_BUSY", "Another operation on this device is taking long; retry in a moment.");

        /// <summary>Returns null immediately if the device is busy.</summary>
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid deviceId, CancellationToken ct) => TryAsync(deviceId, TimeSpan.Zero, ct);

        private async Task<IAsyncDisposable?> TryAsync(Guid deviceId, TimeSpan wait, CancellationToken ct)
        {
            var sem = _local.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
            if (!await sem.WaitAsync(wait, ct)) return null;
            if (_connectionString == null) return new Handle(sem, null, null);

            MySqlConnection? conn = null;
            try
            {
                conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                var name = "fdms:dev:" + deviceId;
                await using var cmd = new MySqlCommand("SELECT GET_LOCK(@n, @t)", conn);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@t", (int)Math.Ceiling(wait.TotalSeconds));
                var got = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0) == 1;
                if (!got) { await conn.DisposeAsync(); sem.Release(); return null; }
                return new Handle(sem, conn, name);
            }
            catch
            {
                if (conn != null) await conn.DisposeAsync();
                sem.Release();
                throw;
            }
        }

        private sealed class Handle : IAsyncDisposable
        {
            private SemaphoreSlim? _sem; private MySqlConnection? _conn; private readonly string? _name;
            public Handle(SemaphoreSlim sem, MySqlConnection? conn, string? name) { _sem = sem; _conn = conn; _name = name; }

            public async ValueTask DisposeAsync()
            {
                var sem = Interlocked.Exchange(ref _sem, null);
                if (sem == null) return;   // already released
                try
                {
                    if (_conn != null)
                    {
                        try { await using var cmd = new MySqlCommand("SELECT RELEASE_LOCK(@n)", _conn); cmd.Parameters.AddWithValue("@n", _name); await cmd.ExecuteScalarAsync(); }
                        catch { /* closing the connection releases the lock anyway */ }
                        await _conn.DisposeAsync();
                        _conn = null;
                    }
                }
                finally { sem.Release(); }
            }
        }
    }

    /// <summary>Loads devices tenant-safely and decrypts their credentials.</summary>
    public sealed class DeviceAccessor
    {
        public const string KeyPurpose = "Fdms.Device.PrivateKey.v1";
        private readonly FdmsDbContext _db;
        private readonly IDataProtector _protector;
        private readonly ICallerScope _scope;

        public DeviceAccessor(FdmsDbContext db, IDataProtectionProvider dp, ICallerScope scope)
        {
            _db = db;
            _scope = scope;
            _protector = dp.CreateProtector(KeyPurpose);
        }

        public string Protect(string privateKeyPem) => _protector.Protect(privateKeyPem);

        public async Task<Device> GetAsync(Guid clientId, Guid deviceId, CancellationToken ct, bool track = true)
        {
            var q = track ? _db.Devices : _db.Devices.AsNoTracking();
            // ClientId filter is the tenant boundary: another client's device is indistinguishable from a missing one.
            // An API token is bound to one device: every other device of the same company is equally invisible to it.
            if (_scope.AllowedDeviceIds is { } allowed && !allowed.Contains(deviceId)) throw GatewayException.NotFound("Device");
            return await q.FirstOrDefaultAsync(d => d.Id == deviceId && d.ClientId == clientId, ct)
                   ?? throw GatewayException.NotFound("Device");
        }

        public async Task<Device> GetByIdAsync(Guid deviceId, CancellationToken ct) =>
            await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw GatewayException.NotFound("Device");

        public FdmsDeviceCredentials Credentials(Device d) => new(
            (FdmsEnvironment)d.Environment, d.ZimraDeviceId, d.ModelName, d.ModelVersion, d.CertificatePem, _protector.Unprotect(d.PrivateKeyProtected));

        public string PrivateKey(Device d) => _protector.Unprotect(d.PrivateKeyProtected);
    }

    public static class Mappers
    {
        public static List<FdmsTax> ParseTaxes(string? json) =>
            string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<FdmsTax>>(json, FdmsJson.Options) ?? new();

        /// <summary>Limits a query to the devices the caller may reach (a token: its device; a branch-limited user: theirs). No-op for everyone else.</summary>
        public static IQueryable<Device> InScope(this IQueryable<Device> q, ICallerScope scope) =>
            scope.AllowedDeviceIds?.ToArray() is { } ids ? q.Where(d => ids.Contains(d.Id)) : q;

        public static IQueryable<Receipt> InScope(this IQueryable<Receipt> q, ICallerScope scope) =>
            scope.AllowedDeviceIds?.ToArray() is { } ids ? q.Where(r => ids.Contains(r.DeviceId)) : q;

        public static DeviceDto ToDto(this Device d) => new(
            d.Id, d.ZimraDeviceId, (FdmsEnvironment)d.Environment, d.SerialNo, d.TaxPayerName, d.TaxPayerTin, d.VatNumber, d.BranchName,
            d.OperatingMode, d.CertificateValidTill, d.QrUrl, d.IsActive,
            ParseTaxes(d.TaxesJson).Select(t => new DeviceTaxDto(t.TaxId, t.TaxPercent, t.TaxName, t.TaxValidFrom, t.TaxValidTill)).ToList(), d.BranchId, null, d.Name);

        public static FiscalCounter ToCounter(this FiscalDayCounterRow c) =>
            new(c.Type, c.Currency, c.TaxId, c.TaxPercent, c.MoneyType, c.Value);

        public static FiscalDayDto ToDto(this FiscalDay d) => new(
            d.DeviceId, d.FiscalDayNo, d.Status.ToString(), d.OpenedAt, d.ClosedAt, d.LastReceiptCounter, d.ClosingErrorCode,
            d.Counters.Select(c => new FiscalCounterView(c.Type.ToString(), c.Currency, c.TaxId, c.TaxPercent, c.MoneyType?.ToString(), c.Value)).ToList());

        public static ReceiptDto ToDto(this Receipt r)
        {
            var errors = string.IsNullOrWhiteSpace(r.ValidationErrorsJson)
                ? new List<ValidationErrorView>()
                : JsonSerializer.Deserialize<List<ValidationErrorView>>(r.ValidationErrorsJson, FdmsJson.Options) ?? new();
            return new ReceiptDto(r.Id, r.DeviceId, r.ExternalRef, r.InvoiceNo, r.Type.ToString(), r.Currency, r.Status.ToString(),
                r.FiscalDayNo, r.ReceiptCounter, r.ReceiptGlobalNo, r.ReceiptDate, r.Total, r.FdmsReceiptId, r.ServerDate, r.QrUrl,
                r.Hash, errors, r.ErrorCode, r.ErrorMessage, r.HoldUntil);
        }
    }
}
