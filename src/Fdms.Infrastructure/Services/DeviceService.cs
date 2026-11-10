using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    public sealed class DeviceService : IDeviceService
    {
        private readonly FdmsDbContext _db;
        private readonly IFdmsGateway _gw;
        private readonly ICsrGenerator _csr;
        private readonly DeviceAccessor _devices;
        private readonly DeviceLocks _locks;
        private readonly ILogger<DeviceService> _log;
        private readonly ICallerScope _scope;
        private readonly IBillingService _billing;

        public DeviceService(FdmsDbContext db, IFdmsGateway gw, ICsrGenerator csr, DeviceAccessor devices, DeviceLocks locks, ICallerScope scope, IBillingService billing, ILogger<DeviceService> log)
        {
            _scope = scope; _billing = billing;
            _db = db; _gw = gw; _csr = csr; _devices = devices; _locks = locks; _log = log;
        }

        public Task<VerifyTaxpayerResponse> VerifyAsync(DeviceIdentityRequest r, CancellationToken ct) =>
            _gw.VerifyTaxpayerAsync(r.Environment, r.ZimraDeviceId, r.ActivationKey, r.SerialNo, r.ModelName, r.ModelVersion, ct);

        public async Task<DeviceDto> RegisterAsync(Guid clientId, DeviceIdentityRequest r, CancellationToken ct)
        {
            // a caller limited to certain devices could not see the new one: registering is for people with access to the whole company
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot register new ones.");
            var env = (int)r.Environment;
            if (await _db.Devices.AnyAsync(d => d.ZimraDeviceId == r.ZimraDeviceId && d.Environment == env, ct))
                throw GatewayException.Conflict("DEVICE_EXISTS", $"Device {r.ZimraDeviceId} is already registered on this gateway.");
            await _billing.EnsureWithinPlanAsync(clientId, "devices", ct);
            if (r.Environment == FdmsEnvironment.Production) await _billing.EnsureLiveAllowedAsync(clientId, ct);

            // New key pair + CSR, then register. The certificate is only valid together with THIS private key.
            var (keyPem, csrPem) = _csr.Generate(r.SerialNo, r.ZimraDeviceId);
            var reg = await _gw.RegisterDeviceAsync(r.Environment, r.ZimraDeviceId, r.ActivationKey, csrPem, r.ModelName, r.ModelVersion, ct);

            var device = new Device
            {
                ClientId = clientId,
                ZimraDeviceId = r.ZimraDeviceId,
                Environment = env,
                SerialNo = r.SerialNo,
                ModelName = r.ModelName,
                ModelVersion = r.ModelVersion,
                CertificatePem = reg.Certificate,
                PrivateKeyProtected = _devices.Protect(keyPem),
            };
            _db.Devices.Add(device);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "device.register", Detail = $"zimraDeviceId={r.ZimraDeviceId} env={r.Environment}" });
            await _db.SaveChangesAsync(ct);   // persist before anything else so the registration is never lost

            try { await ApplyConfigAsync(device, ct); }
            catch (FdmsException ex) { _log.LogWarning(ex, "Device {Id} registered but getConfig failed; call refresh-config later.", device.ZimraDeviceId); }

            return device.ToDto();
        }

        public async Task<IReadOnlyList<DeviceDto>> ListAsync(Guid clientId, CancellationToken ct) =>
            await ListWithBranchesAsync(clientId, ct);

        private async Task<IReadOnlyList<DeviceDto>> ListWithBranchesAsync(Guid clientId, CancellationToken ct)
        {
            var devices = await _db.Devices.AsNoTracking().InScope(_scope).Where(d => d.ClientId == clientId).OrderBy(d => d.ZimraDeviceId).ToListAsync(ct);
            var names = await _db.Branches.AsNoTracking().Where(b => b.ClientId == clientId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            return devices.Select(d => d.ToDto() with { AssignedBranch = d.BranchId.HasValue ? names.GetValueOrDefault(d.BranchId.Value) : null }).ToList();
        }

        public async Task<DeviceDto> GetAsync(Guid clientId, Guid deviceId, CancellationToken ct) =>
            (await _devices.GetAsync(clientId, deviceId, ct, track: false)).ToDto();

        public async Task<DeviceDto> RenameAsync(Guid clientId, Guid deviceId, string? name, CancellationToken ct)
        {
            var d = await _devices.GetAsync(clientId, deviceId, ct);
            name = string.IsNullOrWhiteSpace(name) ? null : System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ");
            if (name is { Length: > 80 }) throw GatewayException.BadRequest("NAME_TOO_LONG", "A machine name may have at most 80 characters.");
            if (name != null && await _db.Devices.AnyAsync(x => x.ClientId == clientId && x.Id != d.Id && x.Name == name, ct)) throw GatewayException.Conflict("NAME_USED", "Another machine already has that name.");
            d.Name = name;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "device.rename", Entity = "Device", EntityId = d.Id.ToString(), Detail = $"device {d.ZimraDeviceId}: {name ?? "(no name)"}" });
            await _db.SaveChangesAsync(ct);
            return d.ToDto();
        }

        public async Task<DeviceDto> RefreshConfigAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            await ApplyConfigAsync(device, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "device.refresh-config", Entity = "Device", EntityId = device.Id.ToString(), Detail = $"device={device.ZimraDeviceId}" });
            await _db.SaveChangesAsync(ct);
            return device.ToDto();
        }

        public async Task<DeviceDto> RenewCertificateAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            var gate = await _locks.AcquireAsync(device.Id, ct);
            try
            {
                var (keyPem, csrPem) = _csr.Generate(device.SerialNo, device.ZimraDeviceId);
                var issued = await _gw.IssueCertificateAsync(_devices.Credentials(device), csrPem, ct);

                device.CertificatePem = issued.Certificate;
                device.PrivateKeyProtected = _devices.Protect(keyPem);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "device.renew-certificate", Detail = $"zimraDeviceId={device.ZimraDeviceId}" });
                await _db.SaveChangesAsync(ct);
                _gw.Invalidate(device.ZimraDeviceId);   // next call builds a client with the new certificate

                await ApplyConfigAsync(device, ct);
                return device.ToDto();
            }
            finally { await gate.DisposeAsync(); }
        }

        public async Task<PingResponse> PingAsync(Guid clientId, Guid deviceId, CancellationToken ct) =>
            await _gw.PingAsync(_devices.Credentials(await _devices.GetAsync(clientId, deviceId, ct, track: false)), ct);

        public async Task<StockListResponse> GetStockAsync(Guid clientId, Guid deviceId, string? hsCode, string? goodName, int offset, int limit, CancellationToken ct) =>
            await _gw.GetStockListAsync(_devices.Credentials(await _devices.GetAsync(clientId, deviceId, ct, track: false)), hsCode, goodName, offset, limit, ct);

        public async Task<ServerCertificateResponse> GetServerCertificateAsync(Guid clientId, Guid deviceId, string? thumbprint, CancellationToken ct)
        {
            var d = await _devices.GetAsync(clientId, deviceId, ct, track: false);
            return await _gw.GetServerCertificateAsync((FdmsEnvironment)d.Environment, thumbprint, d.ModelName, d.ModelVersion, ct);
        }

        private async Task ApplyConfigAsync(Device device, CancellationToken ct)
        {
            var cfg = await _gw.GetConfigAsync(_devices.Credentials(device), ct);
            device.TaxPayerName = cfg.TaxPayerName;
            device.TaxPayerTin = cfg.TaxPayerTin;
            device.VatNumber = cfg.VatNumber;
            device.BranchName = cfg.DeviceBranchName;
            var a = cfg.DeviceBranchAddress;
            device.BranchAddress = a == null ? null : string.Join(", ", new[] { $"{a.HouseNo} {a.Street}".Trim(), a.City, a.Province }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } addr ? addr : null;
            device.BranchPhone = cfg.DeviceBranchContacts?.PhoneNo; device.BranchEmail = cfg.DeviceBranchContacts?.Email;
            device.QrUrl = cfg.QrUrl;
            device.DayMaxHrs = cfg.TaxPayerDayMaxHrs;
            device.CertificateValidTill = cfg.CertificateValidTill;
            device.OperatingMode = Enum.TryParse<DeviceOperatingMode>(cfg.DeviceOperatingMode, true, out var m) ? m : DeviceOperatingMode.Online;
            device.TaxesJson = JsonSerializer.Serialize(cfg.ApplicableTaxes, FdmsJson.Options);
            device.ConfigRefreshedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
