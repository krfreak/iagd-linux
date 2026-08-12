namespace Iagd.Probe;

/// <summary>
/// Union of the hook DLL's enum (HookDll/Hook/MessageType.h) and the host's
/// (IAGrim/UI/Misc/MessageType.cs). The two disagree: the DLL defines
/// SetDifficultyRamp (8001) which the host omits, and the host defines
/// CINEMATIC_TEXT (9000) which the DLL no longer sends. Both are included so an
/// unexpected value is reported as genuinely unknown rather than misattributed.
/// </summary>
internal static class MessageTypes {
    private static readonly Dictionary<int, string> Names = new() {
        [3] = "REPORT_WORKER_THREAD_LAUNCHED",
        [11] = "CloudGetNumFiles",
        [12] = "CloudRead",
        [13] = "CloudWrite",
        [20] = "GameInfo_IsHardcore",
        [21] = "GameInfo_SetModName",
        [25] = "Stash_Item_BasicInfo",
        [44] = "ERROR_HOOKING_GENERIC",
        [47] = "GameInfo_IsHardcore_via_init",
        [52] = "SUCCESS_HOOKING_GENERIC",
        [62] = "ITEMSEEDDATA_PLAYERID_ERR_NOGAME",
        [63] = "ITEMSEEDDATA_PLAYERID_ERR_NOITEM",
        [74] = "ITEMSEEDDATA_PLAYERID",
        [8000] = "GAMEENGINE_UPDATE",
        [8001] = "GAMEENGINE_SetDifficultyRamp",   // DLL-only
        [8100] = "INJECTION_CANCELLED",
        [9000] = "CINEMATIC_TEXT",                 // host-only, legacy
    };

    public static string Name(int type) =>
        Names.TryGetValue(type, out var n) ? n : $"UNKNOWN({type})";

    public static bool IsKnown(int type) => Names.ContainsKey(type);

    /// <summary>
    /// Types the host treats as high-frequency telemetry. Folded into counters rather
    /// than printed, or they bury everything else.
    /// </summary>
    public static bool IsNoisy(int type) => type is 8000 or 8001;
}
