using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Enums;

namespace Fdms.Tests
{
    /// <summary>
    /// In-memory stand-in for ZIMRA FDMS. It issues real certificates for the CSR and, like FDMS, verifies every
    /// receipt/fiscal-day signature against that certificate and enforces counter/global-number sequencing.
    /// </summary>
    public sealed class FakeFdmsGateway : IFdmsGateway
    {
        private readonly X509Certificate2 _ca;
        private readonly Dictionary<int, X509Certificate2> _deviceCerts = new();
        private readonly Dictionary<int, State> _state = new();
        public int SubmitCalls { get; private set; }
        public bool FailNextSubmitTransiently { get; set; }
        public bool RejectNextSubmit { get; set; }
        /// <summary>"Online" or "Offline" - offline devices get DEV01 on openDay/getStatus/submitReceipt/closeDay, like FDMS.</summary>
        /// <summary>The mode given to devices registered from now on (each device keeps the mode it was registered with).</summary>
        public string OperatingMode { get; set; } = "Online";
        private readonly Dictionary<int, string> _modes = new();
        public bool FailNextFileUpload { get; set; }
        public int FileUploads { get; private set; }
        public List<FiscalDayFileDto> FilesReceived { get; } = new();
        private readonly Dictionary<string, FileStatusItem> _files = new();
        private readonly Dictionary<int, OfflineState> _offline = new();

        private sealed class OfflineState
        {
            public int DayNo; public int LastSequence; public int LastCounter; public long? LastGlobal; public string? LastHash;
            public List<FiscalCounter> Counters = new(); public DateTime Opened;
        }

        private bool IsOffline(int deviceId) => _modes.TryGetValue(deviceId, out var m) && m == "Offline";
        private void RequireOnline(int deviceId) { if (IsOffline(deviceId)) throw new FdmsException(422, "DEV01", "Device not found or not active (offline mode)"); }
        public List<ReceiptPayload> Received { get; } = new();
        public List<CloseDayRequest> Closed { get; } = new();

        private sealed class State
        {
            public string DayStatus = "FiscalDayClosed";
            public int? LastDayNo;
            public long? LastGlobalNo;
            public int LastCounter;
            public string? LastHash;
            public DateTime? LastDate;
            public DateTime Opened;
            public long NextReceiptId = 1000;
            public Dictionary<string, SubmitReceiptResponse> ByKey = new();
        }

        public FakeFdmsGateway()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Fake ZIMRA CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            _ca = new X509Certificate2(req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5)).Export(X509ContentType.Pfx));
        }

        private State S(int id) => _state.TryGetValue(id, out var s) ? s : _state[id] = new State();

        public Task<VerifyTaxpayerResponse> VerifyTaxpayerAsync(FdmsEnvironment env, int deviceId, string key, string serial, string mn, string mv, CancellationToken ct = default) =>
            Task.FromResult(new VerifyTaxpayerResponse("op", "ACME Pvt Ltd", "1234567890", "123456789", "Main Branch", null, null));

        public Task<RegisterDeviceResponse> RegisterDeviceAsync(FdmsEnvironment env, int deviceId, string key, string csrPem, string mn, string mv, CancellationToken ct = default)
        {
            if (key == "BAD") throw new FdmsException(422, "DEV02", "Activation key is incorrect");
            var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            var req = new CertificateRequest(csr.SubjectName, csr.PublicKey, HashAlgorithmName.SHA256);
            var generator = X509SignatureGenerator.CreateForRSA(_ca.GetRSAPrivateKey()!, RSASignaturePadding.Pkcs1);
            using var issued = req.Create(_ca.SubjectName, generator, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(8));
            _deviceCerts[deviceId] = new X509Certificate2(issued.RawData);
            _modes[deviceId] = OperatingMode;
            return Task.FromResult(new RegisterDeviceResponse("op", issued.ExportCertificatePem()));
        }

        public Task<RegisterDeviceResponse> IssueCertificateAsync(FdmsDeviceCredentials d, string csrPem, CancellationToken ct = default) =>
            RegisterDeviceAsync(d.Environment, d.DeviceId, "x", csrPem, d.ModelName, d.ModelVersion, ct);

        public Task<FdmsConfig> GetConfigAsync(FdmsDeviceCredentials d, CancellationToken ct = default) =>
            Task.FromResult(new FdmsConfig("op", "ACME Pvt Ltd", "1234567890", "123456789", "SN-1", "Main Branch", null, null, _modes.GetValueOrDefault(d.DeviceId, "Online"), 24, 2,
                new List<FdmsTax>
                {
                    new(1, null, "Exempt", new DateTime(2020, 1, 1), null),
                    new(2, 0m, "Zero rated", new DateTime(2020, 1, 1), null),
                    new(3, 15m, "VAT 15%", new DateTime(2020, 1, 1), null),
                }, DateTime.UtcNow.AddYears(1), "https://invoice.zimra.co.zw"));

        public Task<FdmsStatus> GetStatusAsync(FdmsDeviceCredentials d, CancellationToken ct = default)
        {
            RequireOnline(d.DeviceId);
            var s = S(d.DeviceId);
            return Task.FromResult(new FdmsStatus("op", s.DayStatus, null, null, null, s.LastDayNo, s.LastGlobalNo));
        }

        public Task<OpenDayResponse> OpenDayAsync(FdmsDeviceCredentials d, DateTime opened, int? no, CancellationToken ct = default)
        {
            RequireOnline(d.DeviceId);
            var s = S(d.DeviceId);
            if (s.DayStatus != "FiscalDayClosed") throw new FdmsException(422, "FISC01", "Open day is not allowed");
            if (no != (s.LastDayNo ?? 0) + 1) throw new FdmsException(422, "FISC01", "Wrong fiscal day number");
            s.DayStatus = "FiscalDayOpened"; s.LastDayNo = no; s.Opened = opened; s.LastCounter = 0; s.LastHash = null; s.LastDate = null;
            return Task.FromResult(new OpenDayResponse("op", no.Value));
        }

        public Task<SubmitReceiptResponse> SubmitReceiptAsync(FdmsDeviceCredentials d, ReceiptPayload r, CancellationToken ct = default)
        {
            SubmitCalls++;
            RequireOnline(d.DeviceId);
            if (FailNextSubmitTransiently) { FailNextSubmitTransiently = false; throw new FdmsException(502, null, "Bad gateway"); }
            if (RejectNextSubmit) { RejectNextSubmit = false; throw new FdmsException(422, "RCPT01", "Fiscal day is closed"); }

            var s = S(d.DeviceId);
            var key = $"{r.ReceiptGlobalNo}|{r.ReceiptDeviceSignature.Hash}";
            if (s.ByKey.TryGetValue(key, out var dup)) return Task.FromResult(dup with { OperationId = "op-dup" });   // idempotent duplicate

            if (s.DayStatus is not ("FiscalDayOpened" or "FiscalDayCloseFailed")) throw new FdmsException(422, "RCPT01", "Fiscal day is closed");
            if (r.ReceiptCounter != s.LastCounter + 1) throw new FdmsException(422, "RCPT011", "Receipt counter is not sequential");
            if (s.LastGlobalNo.HasValue && r.ReceiptGlobalNo != s.LastGlobalNo + 1) throw new FdmsException(422, "RCPT012", "Receipt global number is not sequential");

            // Re-derive the signed string exactly like FDMS and verify the device signature with the issued certificate.
            var type = Enum.Parse<ReceiptType>(r.ReceiptType);
            var taxes = r.ReceiptTaxes.Select(t => new ReceiptTaxLine(t.TaxId, t.TaxCode, t.TaxPercent, t.TaxAmount, t.SalesAmountWithTax)).ToList();
            var str = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(d.DeviceId, type, r.ReceiptCurrency, r.ReceiptGlobalNo,
                DateTime.Parse(r.ReceiptDate), r.ReceiptTotal, taxes, r.ReceiptCounter > 1 ? s.LastHash : null));
            using var pub = _deviceCerts[d.DeviceId].GetRSAPublicKey()!;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(str));
            if (Convert.ToBase64String(hash) != r.ReceiptDeviceSignature.Hash ||
                !pub.VerifyHash(hash, Convert.FromBase64String(r.ReceiptDeviceSignature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new FdmsException(422, "RCPT020", "Invoice signature is not valid");

            // The arithmetic rules FDMS enforces.
            if (Math.Abs(r.ReceiptPayments.Sum(p => p.PaymentAmount) - r.ReceiptTotal) > 0.001m) throw new FdmsException(422, "RCPT039", "payments != total");
            if (Math.Abs(r.ReceiptTaxes.Sum(t => t.SalesAmountWithTax) - r.ReceiptTotal) > 0.001m) throw new FdmsException(422, "RCPT038", "taxes != total");

            s.LastCounter = r.ReceiptCounter; s.LastGlobalNo = r.ReceiptGlobalNo; s.LastHash = r.ReceiptDeviceSignature.Hash;
            Received.Add(r);
            var res = new SubmitReceiptResponse("op", s.NextReceiptId++, DateTime.Parse(r.ReceiptDate), JsonDocument.Parse("{\"hash\":\"x\",\"signature\":\"y\"}").RootElement.Clone(), null);
            s.ByKey[key] = res;
            return Task.FromResult(res);
        }

        public Task<CloseDayResponse> CloseDayAsync(FdmsDeviceCredentials d, CloseDayRequest request, CancellationToken ct = default)
        {
            RequireOnline(d.DeviceId);
            var s = S(d.DeviceId);
            if (s.DayStatus is not ("FiscalDayOpened" or "FiscalDayCloseFailed")) throw new FdmsException(422, "FISC04", "Fiscal day not opened");

            var counters = request.FiscalDayCounters.Select(c => new FiscalCounter(
                Enum.Parse<FiscalCounterType>(c.FiscalCounterType), c.FiscalCounterCurrency, c.FiscalCounterTaxID, c.FiscalCounterTaxPercent,
                c.FiscalCounterMoneyType == null ? null : Enum.Parse<MoneyType>(c.FiscalCounterMoneyType), c.FiscalCounterValue));
            var str = SignatureStringBuilder.BuildFiscalDayString(d.DeviceId, request.FiscalDayNo, s.Opened.Date, counters);
            using var pub = _deviceCerts[d.DeviceId].GetRSAPublicKey()!;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(str));
            var ok = pub.VerifyHash(hash, Convert.FromBase64String(request.FiscalDayDeviceSignature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Closed.Add(request);
            s.DayStatus = ok && request.ReceiptCounter == s.LastCounter ? "FiscalDayClosed" : "FiscalDayCloseFailed";
            return Task.FromResult(new CloseDayResponse("op"));
        }

        public Task<PingResponse> PingAsync(FdmsDeviceCredentials d, CancellationToken ct = default) => Task.FromResult(new PingResponse("op-ping", 60));

        public Task<StockListResponse> GetStockListAsync(FdmsDeviceCredentials d, string? hsCode, string? goodName, int offset, int limit, CancellationToken ct = default)
        {
            var all = new List<StockGood> { new("85456852", "Shoes", 10m, 1, "ACME Pvt Ltd", null, null), new("19059010", "Bread", 40m, 1, "ACME Pvt Ltd", null, null) };
            var rows = all.Where(g => (hsCode == null || g.HsCode == hsCode) && (goodName == null || g.GoodName.StartsWith(goodName, StringComparison.OrdinalIgnoreCase))).ToList();
            return Task.FromResult(new StockListResponse(rows.Count, rows.Skip(offset).Take(limit).ToList()));
        }

        public Task<ServerCertificateResponse> GetServerCertificateAsync(FdmsEnvironment env, string? thumbprint, string mn, string mv, CancellationToken ct = default) =>
            Task.FromResult(new ServerCertificateResponse(new List<string> { _ca.ExportCertificatePem() }, DateTime.UtcNow.AddYears(1)));

        /// <summary>Validates a fiscal-day file the way FDMS does: sequence, receipt chain + signatures, then footer counters + signature.</summary>
        public Task<SubmitFileResponse> SubmitFileAsync(FdmsDeviceCredentials d, string fileName, string base64Content, CancellationToken ct = default)
        {
            FileUploads++;
            if (FailNextFileUpload) { FailNextFileUpload = false; throw new FdmsException(502, null, "Bad gateway"); }
            if (!IsOffline(d.DeviceId)) throw new FdmsException(422, "FILE03", "Device operating mode is not Offline");
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Content));
            Assert_Size(base64Content);
            var file = JsonSerializer.Deserialize<FiscalDayFileDto>(json, FdmsJson.Options)!;
            if (file.Header.DeviceId != d.DeviceId) throw new FdmsException(422, "FILE05", "Device id in input parameters and file header are different");
            FilesReceived.Add(file);

            if (!_offline.TryGetValue(d.DeviceId, out var st) || st.DayNo != file.Header.FiscalDayNo)
                _offline[d.DeviceId] = st = new OfflineState { DayNo = file.Header.FiscalDayNo, Opened = DateTime.Parse(file.Header.FiscalDayOpened) };

            var errors = new List<string>();
            if (file.Header.FileSequence != st.LastSequence + 1) errors.Add("IncorrectFileFormat");
            else
            {
                st.LastSequence = file.Header.FileSequence;
                using var pub = _deviceCerts[d.DeviceId].GetRSAPublicKey()!;
                foreach (var r in file.Content?.Receipts ?? new List<ReceiptPayload>())
                {
                    if (r.ReceiptCounter != st.LastCounter + 1 || (st.LastGlobal.HasValue && r.ReceiptGlobalNo != st.LastGlobal + 1)) { errors.Add("MissingReceipts"); break; }
                    var type = Enum.Parse<ReceiptType>(r.ReceiptType);
                    var taxes = r.ReceiptTaxes.Select(t => new ReceiptTaxLine(t.TaxId, t.TaxCode, t.TaxPercent, t.TaxAmount, t.SalesAmountWithTax)).ToList();
                    var str = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(d.DeviceId, type, r.ReceiptCurrency, r.ReceiptGlobalNo,
                        DateTime.Parse(r.ReceiptDate), r.ReceiptTotal, taxes, r.ReceiptCounter > 1 ? st.LastHash : null));
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(str));
                    if (Convert.ToBase64String(hash) != r.ReceiptDeviceSignature.Hash ||
                        !pub.VerifyHash(hash, Convert.FromBase64String(r.ReceiptDeviceSignature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    { errors.Add("ReceiptsWithValidationErrors"); break; }
                    st.LastCounter = r.ReceiptCounter; st.LastGlobal = r.ReceiptGlobalNo; st.LastHash = r.ReceiptDeviceSignature.Hash;

                    var (byTax, taxByTax) = type switch
                    {
                        ReceiptType.FiscalInvoice => (FiscalCounterType.SaleByTax, FiscalCounterType.SaleTaxByTax),
                        ReceiptType.CreditNote => (FiscalCounterType.CreditNoteByTax, FiscalCounterType.CreditNoteTaxByTax),
                        _ => (FiscalCounterType.DebitNoteByTax, FiscalCounterType.DebitNoteTaxByTax),
                    };
                    var deltas = new List<FiscalCounter>();
                    foreach (var t in r.ReceiptTaxes)
                    {
                        deltas.Add(new FiscalCounter(byTax, r.ReceiptCurrency, t.TaxId, t.TaxPercent, null, t.SalesAmountWithTax));
                        deltas.Add(new FiscalCounter(taxByTax, r.ReceiptCurrency, t.TaxId, t.TaxPercent, null, t.TaxAmount));
                    }
                    foreach (var p in r.ReceiptPayments)
                        deltas.Add(new FiscalCounter(FiscalCounterType.BalanceByMoneyType, r.ReceiptCurrency, null, null, Enum.Parse<MoneyType>(p.MoneyTypeCode), p.PaymentAmount));
                    st.Counters = FiscalCounterCalculator.Merge(st.Counters, deltas).ToList();
                }

                if (errors.Count == 0 && file.Footer != null)
                {
                    var f = file.Footer;
                    var counters = f.FiscalDayCounters.Select(c => new FiscalCounter(Enum.Parse<FiscalCounterType>(c.FiscalCounterType), c.FiscalCounterCurrency,
                        c.FiscalCounterTaxID, c.FiscalCounterTaxPercent, c.FiscalCounterMoneyType == null ? null : Enum.Parse<MoneyType>(c.FiscalCounterMoneyType), c.FiscalCounterValue)).ToList();
                    var expected = FiscalCounterCalculator.Merge(Array.Empty<FiscalCounter>(), st.Counters).OrderBy(c => c.Type).ThenBy(c => c.Currency).ThenBy(c => c.TaxId).ThenBy(c => c.MoneyType).ToList();
                    var actual = FiscalCounterCalculator.Merge(Array.Empty<FiscalCounter>(), counters).OrderBy(c => c.Type).ThenBy(c => c.Currency).ThenBy(c => c.TaxId).ThenBy(c => c.MoneyType).ToList();
                    if (!expected.SequenceEqual(actual)) errors.Add("CountersMismatch");
                    else if (f.ReceiptCounter != st.LastCounter) errors.Add("CountersMismatch");
                    else
                    {
                        var str = SignatureStringBuilder.BuildFiscalDayString(d.DeviceId, st.DayNo, st.Opened.Date, counters);
                        using var pub2 = _deviceCerts[d.DeviceId].GetRSAPublicKey()!;
                        if (!pub2.VerifyHash(SHA256.HashData(Encoding.UTF8.GetBytes(str)), Convert.FromBase64String(f.FiscalDayDeviceSignature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                            errors.Add("BadCertificateSignature");
                    }
                }
            }

            var op = "file-" + Guid.NewGuid().ToString("N")[..8];
            _files[op] = new FileStatusItem(op, DateTime.UtcNow, d.DeviceId, fileName, DateTime.UtcNow,
                errors.Count == 0 ? "FileProcessingIsSuccessful" : "FileProcessingWithErrors", errors, file.Header.FiscalDayNo, file.Header.FileSequence);
            return Task.FromResult(new SubmitFileResponse(op));
        }

        private static void Assert_Size(string base64) { if (base64.Length > 3_000_000) throw new FdmsException(422, "FILE01", "File is too big. Allowed file size: 3 MB"); }

        public Task<FileStatusResponse> GetFileStatusAsync(FdmsDeviceCredentials d, DateTime from, DateTime till, string? operationId, CancellationToken ct = default) =>
            Task.FromResult(new FileStatusResponse("op", _files.Values.Where(v => operationId == null || v.OperationId == operationId).ToList()));

        public void Invalidate(int deviceId) { }
    }
}
