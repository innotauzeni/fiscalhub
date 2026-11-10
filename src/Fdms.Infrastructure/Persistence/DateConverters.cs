using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fdms.Infrastructure.Persistence
{
    /// <summary>Stored values are UTC: read them back as UTC so JSON carries the "Z".</summary>
    public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : base(v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    public sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter() : base(v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
    }

    /// <summary>Stored values are Zimbabwe wall-clock readings: no zone is attached when they are read back or sent out.</summary>
    public sealed class WallClockConverter : ValueConverter<DateTime, DateTime>
    {
        public WallClockConverter() : base(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified)) { }
    }

    public sealed class NullableWallClockConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableWallClockConverter() : base(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Unspecified) : v) { }
    }
}
