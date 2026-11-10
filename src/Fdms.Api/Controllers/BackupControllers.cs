using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>Database backups. Platform operator only: a backup holds every company's data, so anyone who belongs to a company is refused here even if some role was given the right.</summary>
    [ApiController, Authorize, HasPermission(Perm.BackupsManage), Route("api/v1/backups")]
    public class BackupsController : ControllerBase
    {
        private readonly IBackupService _s;
        public BackupsController(IBackupService s) => _s = s;

        private void OperatorOnly()
        {
            if (User.ClientIdOrNull() != null) throw new GatewayException(403, "OPERATOR_ONLY", "Backups are only available to the platform operator.");
        }

        /// <summary>Settings in force (folder, schedule, how many are kept) and the existing backups, newest first.</summary>
        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) { OperatorOnly(); return Ok(new { info = await _s.InfoAsync(ct), items = await _s.ListAsync(ct) }); }

        /// <summary>Back up now. Takes as long as the database is big; the file appears in the list when finished.</summary>
        [HttpPost] public async Task<IActionResult> Create(CancellationToken ct) { OperatorOnly(); return Ok(await _s.CreateAsync("manual", ct)); }

        [HttpGet("{id:guid}/download")]
        public async Task<IActionResult> Download(Guid id, [FromQuery] bool plain = false, CancellationToken ct = default)
        {
            OperatorOnly();
            var (path, name) = await _s.GetFileAsync(id, ct);
            Response.Headers.CacheControl = "no-store";
            if (!plain) return PhysicalFile(path, "application/gzip", name);
            // an ordinary .sql file for servers without gunzip (Windows): the same dump, uncompressed on the way out
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return File(new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress), "application/sql", name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name + ".sql");
        }

        /// <summary>The empty database structure as one SQL script (every table and index), for setting up a new server. Run it first, then load a data backup.</summary>
        [HttpGet("schema")]
        public async Task<IActionResult> Schema(CancellationToken ct)
        {
            OperatorOnly();
            var (name, sql) = await _s.SchemaScriptAsync(ct);
            Response.Headers.CacheControl = "no-store";
            return File(System.Text.Encoding.UTF8.GetBytes(sql), "application/sql", name);
        }

        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { OperatorOnly(); await _s.DeleteAsync(id, ct); return NoContent(); }
    }
}
