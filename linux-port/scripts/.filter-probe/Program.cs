// Prints this port's filter groups in the same shape extract-filter-groups.py emits.
using IAGrim.Core.ItemStats;

void Emit(string category, IReadOnlyList<FilterGroup> groups) {
    foreach (var group in groups) {
        Console.WriteLine($"{category}\t{string.Join(",", group.Fields.Distinct().OrderBy(f => f, StringComparer.Ordinal))}");
    }
}

Emit("damage", FilterGroups.Damage);
Emit("dot", FilterGroups.DamageOverTime);
Emit("resist", FilterGroups.Resistances);
Emit("misc", FilterGroups.Misc);
