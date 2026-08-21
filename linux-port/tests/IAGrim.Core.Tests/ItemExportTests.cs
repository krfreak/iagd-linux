using IAGrim.Core.Backup;
using IAGrim.Platform;
using Xunit;

namespace IAGrim.Core.Tests;

/// <summary>
/// The GD Stash interchange format: upstream's <c>GDFileExporter</c>, ported as
/// <see cref="ItemExport"/>. Nothing exercised this before the Settings page grew a button for
/// it (BACKLOG entry 4) — the CLI's <c>iagd export</c> and <c>iagd import-file</c> and the host's
/// <c>/api/export</c> and <c>/api/import</c> all sit on top of the same three calls tested here.
/// </summary>
public class ItemExportTests : IDisposable {
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"iagd-export-{Guid.NewGuid():N}.db");

    private readonly string _exportPath = Path.Combine(
        Path.GetTempPath(), $"iagd-export-{Guid.NewGuid():N}.gds");

    public void Dispose() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm", _exportPath }) {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static LootedItem Item(string baseRecord, uint seed, bool hardcore = false, string mod = "") => new() {
        Mod = mod,
        IsHardcore = hardcore,
        BaseRecord = baseRecord,
        Seed = seed,
        Stats = [new LootStat(6, "Test Item")],
    };

    /// <summary>
    /// Write, then read back: the field order in <see cref="ItemExport.Write"/> and
    /// <see cref="ItemExport.Parse"/> has to agree with itself even if it never touches a real
    /// GD Stash file, and a round trip is the cheapest thing that would notice a swapped field.
    /// </summary>
    [Fact]
    public void ARoundTripPreservesTheRecordsThatIdentifyAnItem() {
        using (var store = new LootStore(_databasePath)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 111));
        }

        ItemExport.Export(_databasePath, _exportPath);
        var parsed = ItemExport.Parse(File.ReadAllBytes(_exportPath));

        var item = Assert.Single(parsed);
        Assert.Equal("records/items/gearweapons/swords1h/d013_sword.dbr", item.BaseRecord);
        Assert.Equal(111u, item.Seed);
        Assert.False(item.IsHardcore);
    }

    /// <summary>
    /// Hardcore and softcore are separate stashes in game (see <see cref="ItemExport.Export"/>'s
    /// own doc comment) — the filter that keeps them apart on the way out has to actually filter.
    /// </summary>
    [Fact]
    public void ExportingOneBranchLeavesTheOtherOut() {
        using (var store = new LootStore(_databasePath)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 1, hardcore: true));
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 2, hardcore: false));
        }

        var count = ItemExport.Export(_databasePath, _exportPath, hardcoreOnly: true);

        Assert.Equal(1, count);
        var item = Assert.Single(ItemExport.Parse(File.ReadAllBytes(_exportPath)));
        Assert.True(item.IsHardcore);
        Assert.Equal(1u, item.Seed);
    }

    /// <summary>
    /// Importing adds items, skips ones already present (same base record and seed — the same
    /// identity the loot importer uses), and refuses anything the collection has never kept.
    /// </summary>
    [Fact]
    public void ImportingSkipsDuplicatesAndRefusesUncollectableItems() {
        using (var store = new LootStore(_databasePath)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 1));
        }
        ItemExport.Export(_databasePath, _exportPath);

        // The same seed as the item already in the collection, plus one the collection has
        // never kept — a component, refused the same way the hook refuses one as it is looted.
        var extra = ItemExport.Parse(File.ReadAllBytes(_exportPath));
        extra.Add(extra[0] with { BaseRecord = "records/items/materia/x1_materia.dbr", Seed = 2 });
        ItemExport.Write(_exportPath, extra);

        var (imported, skipped, refused) = ItemExport.Import(_databasePath, _exportPath);

        Assert.Equal(0, imported);
        Assert.Equal(1, skipped);
        Assert.Equal(1, refused);
    }

    /// <summary>
    /// The interchange format carries no mod of its own (<c>GDFileExporter</c> never wrote one),
    /// so importing has to take it as a parameter — matching the CLI's <c>--mod</c> and the
    /// host's <c>ImportRequest.Mod</c>.
    /// </summary>
    [Fact]
    public void ImportedItemsAreAttributedToTheGivenMod() {
        using (var store = new LootStore(_databasePath)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 1));
        }
        ItemExport.Export(_databasePath, _exportPath);

        var target = Path.Combine(Path.GetTempPath(), $"iagd-export-target-{Guid.NewGuid():N}.db");
        try {
            var (imported, _, _) = ItemExport.Import(target, _exportPath, mod: "Grimarillion");
            Assert.Equal(1, imported);

            using var store = new LootStore(target);
            var row = Assert.Single(store.ListItems());
            var item = store.GetById(row.Id);
            Assert.Equal("Grimarillion", item?.Mod);
        }
        finally {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { target, target + "-wal", target + "-shm" }) {
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    /// <summary>
    /// A file claiming a version this reader does not understand must not be read as though it
    /// were one it does — the field layout differs enough between versions that guessing wrong
    /// produces items with their affixes shifted by one rather than a clean failure.
    /// </summary>
    [Fact]
    public void AnUnsupportedFileVersionIsRefusedRatherThanMisread() {
        var bytes = new byte[8];
        BitConverter.GetBytes(99).CopyTo(bytes, 0); // file version
        BitConverter.GetBytes(0).CopyTo(bytes, 4);  // item count

        Assert.Throws<InvalidDataException>(() => ItemExport.Parse(bytes));
    }
}
