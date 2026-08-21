namespace IAGrim.Platform;

/// <summary>
/// Whether a "loot from" tab and a "deposit to" tab are safe to save together.
///
/// Upstream's stash picker refuses to close while the two are the same non-zero tab
/// (StashTabPicker.cs): looting and depositing through one tab means the deposit lands back in
/// the tab the importer is about to empty, and the two fight each other on every pass. 0 is
/// exempt on both sides because it means "the last tab" rather than naming a specific one — two
/// settings both left at the default do not collide, matching upstream, which exempts the same
/// case explicitly.
///
/// This port has no modal to block a save on, so the check moves to where the save happens
/// instead. It lives beside <see cref="AppSettings"/> rather than in the host because there is
/// more than one writer: PUT /api/settings is one and <c>iagd settings stashToDepositTo</c> is
/// another. A rule enforced at only one of them is a rule with a way round it.
/// </summary>
public static class StashTabGuard {
    public static bool Collide(int stashToLootFrom, int stashToDepositTo) =>
        stashToLootFrom == stashToDepositTo && stashToLootFrom != 0;

    public const string Message =
        "Loot from and Deposit to are set to the same tab. Items would land back in the tab " +
        "being emptied. Point them at different tabs, or leave one at 0 for the last tab.";
}
