using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// What happens to a loot file after it is in the database. Both halves of this were reported
/// from a real install: every consumed file was copied into the backup directory under the
/// hook's own name with <c>overwrite: true</c>, and nothing ever deleted one — so a name
/// collision would take an item's only record with it, and a collection built over months
/// leaves tens of thousands of files behind.
///
/// Upstream answers both in <c>CsvParsingService</c>: a <c>-conflict.csv</c> rename rather than
/// a clobber, and a sweep of files older than three days when it starts.
/// </summary>
public class LootBackupTests : IDisposable {
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "iagd-loot-backup-" + Guid.NewGuid().ToString("N"));

    public LootBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Write(string name, string content, TimeSpan? age = null) {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        if (age is not null) File.SetLastWriteTime(path, DateTime.Now - age.Value);
        return path;
    }

    /// <summary>
    /// The retry case, and the reason the copy stays unconditional: a pass whose File.Copy
    /// worked and whose File.Delete did not sees its own copy again on the next pass.
    /// </summary>
    [Fact]
    public void TheSameFileCopiedTwiceKeepsItsName() {
        var backup = Write("ABC123.csv", "records/item.dbr;123");
        var incoming = Path.Combine(_dir, "incoming");
        Directory.CreateDirectory(incoming);
        var file = Path.Combine(incoming, "ABC123.csv");
        File.WriteAllText(file, "records/item.dbr;123");

        Assert.Equal(backup, LootWatcher.BackupTarget(file, _dir));
    }

    /// <summary>
    /// The case that loses an item. Two different loot files cannot be told apart by name — the
    /// hook picks one at random — so the only evidence available is the bytes.
    /// </summary>
    [Fact]
    public void ADifferentFileUnderTheSameNameGetsItsOwn() {
        Write("ABC123.csv", "records/one.dbr;1");
        var incoming = Path.Combine(_dir, "incoming");
        Directory.CreateDirectory(incoming);
        var file = Path.Combine(incoming, "ABC123.csv");
        File.WriteAllText(file, "records/two.dbr;2");

        var target = LootWatcher.BackupTarget(file, _dir);

        Assert.NotEqual(Path.Combine(_dir, "ABC123.csv"), target);
        Assert.EndsWith("-conflict.csv", target);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void AFreeNameIsUsedAsIs() {
        var file = Write("DEF456.csv", "records/item.dbr;9");
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(Path.Combine(empty, "DEF456.csv"), LootWatcher.BackupTarget(file, empty));
    }

    [Fact]
    public void FilesPastTheRetentionAreDeletedAndNewerOnesKept() {
        var old = Write("old.csv", "a", age: TimeSpan.FromDays(4));
        var older = Write("older.csv", "b", age: TimeSpan.FromDays(400));
        var recent = Write("recent.csv", "c", age: TimeSpan.FromDays(1));
        var justNow = Write("now.csv", "d");

        Assert.Equal(2, LootWatcher.PruneBackups(_dir));

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(justNow));
    }

    /// <summary>Nothing else in the directory is ours to delete.</summary>
    [Fact]
    public void OnlyCsvFilesAreSwept() {
        var note = Write("notes.txt", "keep me", age: TimeSpan.FromDays(400));

        Assert.Equal(0, LootWatcher.PruneBackups(_dir));
        Assert.True(File.Exists(note));
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsNotAnError() {
        Assert.Equal(0, LootWatcher.PruneBackups(Path.Combine(_dir, "nope")));
    }
}
