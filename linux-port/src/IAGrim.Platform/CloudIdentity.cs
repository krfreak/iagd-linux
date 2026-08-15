using Microsoft.Data.Sqlite;

namespace IAGrim.Platform;

/// <summary>
/// The identity the backup service knows an item by — <c>PlayerItem.cloudid</c>.
///
/// It lives here, rather than with the rest of online sync, because it is assigned when an item
/// is *created* and not when it is uploaded. Upstream moved it for the same reason
/// (<c>CsvParsingService</c> mints one on loot): an item pushed to another machine over the live
/// socket without an id cannot be deduplicated against the copy that follows over REST, and the
/// two arrive as two items.
///
/// So every write path assigns one, whether or not the user has ever logged in. An id on an item
/// that is never uploaded costs 32 characters; an item without one that *is* uploaded costs a
/// duplicate.
/// </summary>
public static class CloudIdentity {
    /// <summary>
    /// A new identity. Upstream's loot path uses a GUID with the dashes stripped, which is 32
    /// characters — exactly the server's minimum.
    /// </summary>
    public static string New() => Guid.NewGuid().ToString().Replace("-", "");

    /// <summary>
    /// Whether the server would accept this id. Its rule, from <c>api/upload/upload.go</c>, is
    /// "32 characters or longer" and nothing else — upstream's stash-import path mints a
    /// dashed GUID, which is 36, and that passes too.
    /// </summary>
    public static bool IsAcceptable(string? id) => id is not null && id.Length >= 32;
}

/// <summary>
/// The record that an item was deleted here, so the deletion can be replayed to the backup
/// service and from there to the user's other machines.
/// </summary>
public static class CloudTombstone {
    /// <summary>
    /// Notes an item's cloud id in <c>deletedplayeritem_v3</c> before the item itself goes.
    ///
    /// Upstream's rule, from <c>PlayerItemDaoImpl.Remove</c>: only for items the server already
    /// knows about. An item that was never uploaded has nothing to delete remotely, and a
    /// tombstone for it would be a stray id sent to the service forever.
    ///
    /// Must be called <b>before</b> the item row is deleted — it reads the cloud id off that row.
    /// </summary>
    public static void Mark(SqliteConnection connection, long itemId, SqliteTransaction? transaction = null) {
        string? cloudId;
        bool synchronized;

        using (var read = connection.CreateCommand()) {
            read.Transaction = transaction;
            read.CommandText = "SELECT cloudid, IFNULL(cloud_hassync, 0) FROM PlayerItem WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", itemId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return;
            cloudId = reader.IsDBNull(0) ? null : reader.GetString(0);
            synchronized = reader.GetInt64(1) != 0;
        }

        if (!synchronized || string.IsNullOrEmpty(cloudId)) return;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO deletedplayeritem_v3 (id) VALUES ($id);";
        insert.Parameters.AddWithValue("$id", cloudId);
        insert.ExecuteNonQuery();
    }
}
