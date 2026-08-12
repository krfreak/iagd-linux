namespace IAGrim.Platform;

/// <summary>
/// The startup step every entry point shares: work out which collection database to use, check
/// it, and say so.
///
/// Kept in one place because the app, the headless host and the CLI must agree — a CLI that
/// imported into one database while the host served another would be the kind of bug that only
/// shows up as "my items disappeared".
/// </summary>
public static class Startup {
    /// <summary>
    /// Resolves and prepares the database. Returns false when the choice was rejected, having
    /// already explained why.
    /// </summary>
    public static bool SelectDatabase(string[] args, AppSettings settings, TextWriter output) {
        var chosen = LinuxPaths.ResolveDatabase(args, settings.DatabaseFile);

        try {
            var selection = DatabaseSelection.Prepare(LinuxPaths.DatabaseFile);
            if (selection.Notice is { } notice) {
                output.WriteLine(notice);
            }
            else if (chosen is not null) {
                output.WriteLine(chosen);
            }
            return true;
        }
        catch (InvalidDataException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("       check --database, IAGD_DATABASE, or 'iagd settings databaseFile'.");
            return false;
        }
    }
}
