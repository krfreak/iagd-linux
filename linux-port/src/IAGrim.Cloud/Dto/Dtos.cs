namespace IAGrim.Cloud.Dto;

/// <summary>An item to remove from the backup. Upstream's <c>DeleteItemDto</c>.</summary>
public sealed class DeleteItemDto {
    public string? Id { get; set; }
}

/// <summary>Upstream's <c>ItemIdentifierDto</c> — a row of <c>deletedplayeritem_v3</c>.</summary>
public sealed class ItemIdentifierDto {
    public string? Id { get; set; }
}

/// <summary>
/// A download batch. Upstream's <c>ItemDownloadDto</c>, and the server's <c>responseType</c> in
/// both <c>api/download</c> and <c>api/buddyitems</c>.
/// </summary>
public sealed class ItemDownloadDto {
    public List<CloudItemDto>? Items { get; set; }
    public List<DeleteItemDto>? Removed { get; set; }

    /// <summary>
    /// The timestamp to ask from next time. The server sends its own clock, *except* when the
    /// batch hit its cap, in which case it sends the highest item timestamp minus one so the
    /// next request resumes rather than skipping the remainder.
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>True when the batch hit the server's cap and more items are waiting.</summary>
    public bool IsPartial { get; set; }
}

/// <summary>How long to wait between calls of one kind, in milliseconds.</summary>
public sealed class LimitEntry {
    public long Download { get; set; }
    public long Upload { get; set; }
    public long Delete { get; set; }
}

/// <summary>
/// The cooldowns <c>/logincheck</c> hands out. <c>Regular</c> is for a single machine;
/// <c>MultiUsage</c> is the faster set, used only when the user has said they play on more than
/// one PC.
/// </summary>
public sealed class LimitsDto {
    public LimitEntry? Regular { get; set; }
    public LimitEntry? MultiUsage { get; set; }
}

/// <summary>One backed-up character, as listed by <c>GET /character</c>.</summary>
public sealed class CharacterListDto {
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
