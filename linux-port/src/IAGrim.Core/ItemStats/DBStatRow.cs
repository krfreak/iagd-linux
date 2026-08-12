using DataAccess;

namespace IAGrim.Core.ItemStats.Dto;

/// <summary>
/// One raw stat field from Grim Dawn's item database, as the seed engine consumes it.
///
/// Copied from upstream's <c>IAGrim/Services/Dto/DBSTatRow.cs</c>. Upstream's version also
/// carries a <c>ToStat()</c> converting to the NHibernate entity <c>DatabaseItemStat</c>;
/// that is dropped here, since this port stores stats directly and does not use the ORM
/// (see PORTING.md).
/// </summary>
public class DBStatRow : IItemStat {
    public string? Record { get; set; }
    public string? Stat { get; set; }
    public double Value { get; set; }
    public string? TextValue { get; set; }

    /// <summary>
    /// IItemStat exposes Value as float; the seed engine needs the full double, so the
    /// double is authoritative and this narrows only at the interface boundary.
    /// </summary>
    float IItemStat.Value {
        get => (float)Value;
        set => Value = value;
    }

    public long Id { get; set; }
}
