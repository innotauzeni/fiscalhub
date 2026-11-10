using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The company's file cabinet for its books: supplier invoices, contracts, tax papers, scans. A file can be attached to a bill, an asset or a supplier. Only a short list of safe
    /// file types is accepted and the content is checked against the type (not just the name). Every file is kept for a number of years (7 by default) and cannot be deleted before
    /// then; the keep-until date can be extended but never shortened.
    /// </summary>
    public sealed class DocumentService : IDocumentService
    {
        public const int MaxFileBytes = 8 * 1024 * 1024;
        private static readonly string[] Categories = { "Supplier invoice", "Contract", "Tax", "Bank", "Payroll", "Asset", "Other" };
        private static readonly string[] LinkTypes = { "none", "bill", "asset", "supplier" };
        private static readonly Dictionary<string, string> Types = new()
        {
            [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".csv"] = "text/csv", [".txt"] = "text/plain",
        };

        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IConfiguration _cfg;
        public DocumentService(FdmsDbContext db, ICallerScope scope, IConfiguration cfg) { _db = db; _scope = scope; _cfg = cfg; }

        private int Years => Math.Clamp(_cfg.GetValue("Documents:RetentionYears", 7), 1, 50);
        private long LimitBytes => _cfg.GetValue("Documents:CompanyLimitMb", 500) * 1024L * 1024L;
        private static DateTime Today => DateTime.UtcNow.Date;

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's documents.");
        }

        /// <summary>The first bytes must match the type the name claims (a renamed program is refused).</summary>
        private static bool ContentMatches(string ext, byte[] d) => ext switch
        {
            ".pdf" => d.Length > 4 && d[0] == 0x25 && d[1] == 0x50 && d[2] == 0x44 && d[3] == 0x46,
            ".png" => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47,
            ".jpg" or ".jpeg" => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF,
            ".xlsx" or ".docx" => d.Length > 4 && d[0] == 0x50 && d[1] == 0x4B && d[2] == 0x03 && d[3] == 0x04,
            ".csv" or ".txt" => !d.Take(4096).Contains((byte)0),
            _ => false,
        };

        private static string SafeName(string name)
        {
            name = Path.GetFileName((name ?? "").Replace('\\', '/'));
            var clean = new string(name.Where(c => !char.IsControl(c) && c != '"' && c != '<' && c != '>' && c != '|' && c != ':' && c != '*' && c != '?').ToArray()).Trim();
            return clean.Length > 200 ? clean[^200..] : clean;
        }

        private async Task<DocumentDto> ToDtoAsync(StoredDocument d, CancellationToken ct)
        {
            var labels = await LabelsAsync(d.ClientId, new[] { d }, ct);
            return Dto(d, labels);
        }

        private static DocumentDto Dto(StoredDocument d, Dictionary<Guid, string> labels) =>
            new(d.Id, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.Category, d.LinkType, d.LinkId, d.LinkId is { } id ? labels.GetValueOrDefault(id) : null, d.Notes, d.RetainUntil, d.RetainUntil.Date < Today, d.UploadedBy, d.UploadedAt);

        private async Task<Dictionary<Guid, string>> LabelsAsync(Guid clientId, IEnumerable<StoredDocument> docs, CancellationToken ct)
        {
            var labels = new Dictionary<Guid, string>();
            var bills = docs.Where(d => d.LinkType == "bill" && d.LinkId != null).Select(d => d.LinkId!.Value).Distinct().ToList();
            var assets = docs.Where(d => d.LinkType == "asset" && d.LinkId != null).Select(d => d.LinkId!.Value).Distinct().ToList();
            var suppliers = docs.Where(d => d.LinkType == "supplier" && d.LinkId != null).Select(d => d.LinkId!.Value).Distinct().ToList();
            if (bills.Count > 0)
            {
                var rows = await (from b in _db.Bills.AsNoTracking() join s in _db.Suppliers.AsNoTracking() on b.SupplierId equals s.Id where b.ClientId == clientId && bills.Contains(b.Id) select new { b.Id, b.Number, Supplier = s.Name }).ToListAsync(ct);
                foreach (var r in rows) labels[r.Id] = $"Bill {r.Number} · {r.Supplier}";
            }
            if (assets.Count > 0) foreach (var a in await _db.FixedAssets.AsNoTracking().Where(a => a.ClientId == clientId && assets.Contains(a.Id)).Select(a => new { a.Id, a.Name }).ToListAsync(ct)) labels[a.Id] = "Asset " + a.Name;
            if (suppliers.Count > 0) foreach (var s in await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId && suppliers.Contains(s.Id)).Select(s => new { s.Id, s.Name }).ToListAsync(ct)) labels[s.Id] = "Supplier " + s.Name;
            return labels;
        }

        public async Task<IReadOnlyList<DocumentDto>> ListAsync(Guid clientId, DocumentFilter f, CancellationToken ct)
        {
            RequireWholeCompany();
            var q = _db.Documents.AsNoTracking().Where(d => d.ClientId == clientId);
            if (!string.IsNullOrWhiteSpace(f.Category)) q = q.Where(d => d.Category == f.Category);
            if (!string.IsNullOrWhiteSpace(f.LinkType)) q = q.Where(d => d.LinkType == f.LinkType);
            if (f.LinkId is { } lid) q = q.Where(d => d.LinkId == lid);
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(d => d.Title.Contains(s) || d.FileName.Contains(s) || (d.Notes != null && d.Notes.Contains(s))); }
            // the file bytes stay in the database: only the descriptive columns are read
            var rows = (await q.OrderByDescending(d => d.UploadedAt).Take(Math.Clamp(f.Take, 1, 500))
                .Select(d => new { d.Id, d.ClientId, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.Category, d.LinkType, d.LinkId, d.Notes, d.RetainUntil, d.UploadedBy, d.UploadedAt }).ToListAsync(ct))
                .Select(d => new StoredDocument { Id = d.Id, ClientId = d.ClientId, Title = d.Title, FileName = d.FileName, ContentType = d.ContentType, SizeBytes = d.SizeBytes, Category = d.Category, LinkType = d.LinkType, LinkId = d.LinkId, Notes = d.Notes, RetainUntil = d.RetainUntil, UploadedBy = d.UploadedBy, UploadedAt = d.UploadedAt }).ToList();
            var labels = await LabelsAsync(clientId, rows, ct);
            return rows.Select(d => Dto(d, labels)).ToList();
        }

        public async Task<DocumentSummaryDto> SummaryAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            var files = await _db.Documents.CountAsync(d => d.ClientId == clientId, ct);
            var bytes = await _db.Documents.Where(d => d.ClientId == clientId).SumAsync(d => (long?)d.SizeBytes, ct) ?? 0;
            return new DocumentSummaryDto(files, bytes, LimitBytes, Years);
        }

        public async Task<DocumentDto> UploadAsync(Guid clientId, UploadDocumentRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var fileName = SafeName(req.FileName);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (fileName.Length == 0 || !Types.ContainsKey(ext)) throw GatewayException.BadRequest("TYPE_NOT_ALLOWED", "Upload a PDF, picture (PNG or JPG), Excel or Word file, CSV or text file.");
            if (req.Data.Length == 0) throw GatewayException.BadRequest("FILE_EMPTY", "That file is empty.");
            if (req.Data.Length > MaxFileBytes) throw GatewayException.BadRequest("FILE_TOO_BIG", $"A file can be at most {MaxFileBytes / 1024 / 1024} MB.");
            if (!ContentMatches(ext, req.Data)) throw GatewayException.BadRequest("CONTENT_MISMATCH", "The file does not look like a " + ext + " file.");
            var title = (req.Title ?? "").Trim();
            if (title.Length == 0) title = Path.GetFileNameWithoutExtension(fileName);
            if (title.Length > 150) throw GatewayException.BadRequest("TITLE_TOO_LONG", "The title can be at most 150 characters.");
            var category = Categories.Contains(req.Category) ? req.Category : throw GatewayException.BadRequest("CATEGORY_INVALID", "Choose a category.");
            var linkType = string.IsNullOrWhiteSpace(req.LinkType) ? "none" : req.LinkType;
            if (!LinkTypes.Contains(linkType)) throw GatewayException.BadRequest("LINK_INVALID", "Attach it to a bill, an asset or a supplier, or to nothing.");
            Guid? linkId = linkType == "none" ? null : req.LinkId ?? throw GatewayException.BadRequest("LINK_INVALID", "Choose what to attach it to.");
            if (linkId is { } lid)
            {
                var ok = linkType switch
                {
                    "bill" => await _db.Bills.AnyAsync(b => b.Id == lid && b.ClientId == clientId, ct),
                    "asset" => await _db.FixedAssets.AnyAsync(a => a.Id == lid && a.ClientId == clientId, ct),
                    _ => await _db.Suppliers.AnyAsync(s => s.Id == lid && s.ClientId == clientId, ct),
                };
                if (!ok) throw GatewayException.NotFound(linkType == "bill" ? "Bill" : linkType == "asset" ? "Asset" : "Supplier");
            }
            if ((req.Notes?.Length ?? 0) > 500) throw GatewayException.BadRequest("NOTES_TOO_LONG", "The notes can be at most 500 characters.");
            var used = await _db.Documents.Where(d => d.ClientId == clientId).SumAsync(d => (long?)d.SizeBytes, ct) ?? 0;
            if (used + req.Data.Length > LimitBytes) throw GatewayException.Conflict("STORAGE_FULL", $"Your document storage ({LimitBytes / 1024 / 1024} MB) is full. Remove files that are past their keep-until date, or ask us for more space.");
            var hash = Convert.ToHexString(SHA256.HashData(req.Data)).ToLowerInvariant();
            if (await _db.Documents.AnyAsync(d => d.ClientId == clientId && d.Sha256 == hash && d.LinkType == linkType && d.LinkId == linkId, ct))
                throw GatewayException.Conflict("DUPLICATE", "That exact file is already stored there.");

            var minimum = Today.AddYears(Years);
            var retain = req.RetainUntil?.Date ?? minimum;
            if (retain < minimum) throw GatewayException.BadRequest("RETENTION_TOO_SHORT", $"Records are kept for at least {Years} years (until {minimum:d MMM yyyy}).");
            var doc = new StoredDocument
            {
                ClientId = clientId, Title = title, FileName = fileName, ContentType = Types[ext], SizeBytes = req.Data.Length, Sha256 = hash, Data = req.Data, Category = category,
                LinkType = linkType, LinkId = linkId, Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(), RetainUntil = retain, UploadedBy = _scope.UserName,
            };
            _db.Documents.Add(doc);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "document.upload", Entity = "Document", EntityId = doc.Id.ToString(), Detail = $"{title} ({fileName}, {req.Data.Length / 1024} KB)" });
            await _db.SaveChangesAsync(ct);
            return await ToDtoAsync(doc, ct);
        }

        public async Task<DocumentDto> UpdateAsync(Guid clientId, Guid id, UpdateDocumentRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var d = await _db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
            var title = (req.Title ?? "").Trim();
            if (title.Length is 0 or > 150) throw GatewayException.BadRequest("TITLE_INVALID", "Give the document a title of up to 150 characters.");
            if (!Categories.Contains(req.Category)) throw GatewayException.BadRequest("CATEGORY_INVALID", "Choose a category.");
            if ((req.Notes?.Length ?? 0) > 500) throw GatewayException.BadRequest("NOTES_TOO_LONG", "The notes can be at most 500 characters.");
            if (req.RetainUntil is { } r && r.Date < d.RetainUntil.Date) throw GatewayException.BadRequest("RETENTION_SHORTENED", "The keep-until date can be extended but never shortened.");
            d.Title = title; d.Category = req.Category; d.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
            if (req.RetainUntil is { } until) d.RetainUntil = until.Date;
            await _db.SaveChangesAsync(ct);
            return await ToDtoAsync(d, ct);
        }

        public async Task<DocumentFile> DownloadAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var d = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
            return new DocumentFile(d.FileName, d.ContentType, d.Data);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var d = await _db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
            if (d.RetainUntil.Date >= Today) throw GatewayException.Conflict("RETENTION", $"This record must be kept until {d.RetainUntil:d MMM yyyy}. It can be deleted after that date.");
            _db.Documents.Remove(d);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "document.delete", Entity = "Document", EntityId = d.Id.ToString(), Detail = $"{d.Title} ({d.FileName})" });
            await _db.SaveChangesAsync(ct);
        }
    }
}
