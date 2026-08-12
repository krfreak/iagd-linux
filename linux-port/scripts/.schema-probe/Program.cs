// Opens a database built purely from upstream's DDL and exercises the read paths.
using IAGrim.Host;

var path = args[0];
var collection = new CollectionService(path);
var views = new CollectionViewService(path);

var all = collection.Search(new ItemQuery(), 0, 10);
Console.WriteLine($"items={all.Total}");

var byName = collection.Search(new ItemQuery { Wildcard = "plagueborne" }, 0, 10);
Console.WriteLine($"byName={byName.Total}");

// Reaches ReplicaItemRow, which is where a schema mismatch would hide.
var byStatLine = collection.Search(new ItemQuery { Wildcard = "acid" }, 0, 10);
Console.WriteLine($"byStatLine={byStatLine.Total}");

var byRarity = collection.Search(new ItemQuery { Rarity = "Blue" }, 0, 10);
Console.WriteLine($"byRarity={byRarity.Total}");

var byLevel = collection.Search(new ItemQuery { MinimumLevel = 90 }, 0, 10);
Console.WriteLine($"byLevel={byLevel.Total}");

// Every remaining filter, purely to prove none of them throws on upstream's tables.
collection.Search(new ItemQuery {
    Filters = [["offensiveBaseFireMin"]], IsRetaliation = true, PetBonuses = true,
    HasPetBonus = true, Classes = ["Occultist"], Slot = ["WeaponHunting_Ranged1h"],
    WithGrantSkillsOnly = true, WithSummonerSkillOnly = true, DuplicatesOnly = true,
    SocketedOnly = true, RecentOnly = true,
}, 0, 10);
Console.WriteLine("allFilters=ok");

var detail = collection.Get(1);
Console.WriteLine($"detailStats={detail?.Stats.Count ?? -1}");

views.Collection(new ItemQuery());
views.Aggregate();
views.Sets();
Console.WriteLine("views=ok");
