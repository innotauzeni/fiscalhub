namespace Fdms.Api.Security
{
    /// <summary>
    /// The web addresses allowed to call this API from a browser (the web console when it has its own site), from <c>Cors:Origins</c>:
    /// a JSON array or one comma-separated string, e.g. "https://fiscal.example.com". Trailing slashes are ignored.
    /// </summary>
    public static class CorsOrigins
    {
        public static string[] From(IConfiguration cfg)
        {
            var list = cfg.GetSection("Cors:Origins").Get<string[]>();
            if (list == null || list.Length == 0) list = (cfg["Cors:Origins"] ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            return list.Select(o => o.Trim().TrimEnd('/')).Where(o => Uri.TryCreate(o, UriKind.Absolute, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static bool Allows(IConfiguration cfg, Uri origin) =>
            From(cfg).Any(o => string.Equals(o, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }
}
