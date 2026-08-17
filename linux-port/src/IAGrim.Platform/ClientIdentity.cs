namespace IAGrim.Platform;

/// <summary>
/// The installation's identifier, read from settings or minted on first use.
///
/// Deliberately not derived from anything about the machine — not the hostname, not a MAC
/// address, not a disk serial. A random value in a file the user owns can be inspected, cleared
/// or copied by them; a fingerprint cannot, and identifying an installation is all this is for.
/// </summary>
public static class ClientIdentity {
    /// <summary>
    /// The stored id, generating and persisting one the first time.
    ///
    /// Takes the caller's settings rather than loading its own, because saving a separately
    /// loaded copy would write back whatever else that copy was holding and quietly undo a
    /// change made elsewhere.
    /// </summary>
    public static string Resolve(AppSettings settings) {
        if (!string.IsNullOrWhiteSpace(settings.ClientId)) return settings.ClientId;

        // Upstream's shape: a GUID with the dashes taken out, 32 hex characters.
        settings.ClientId = Guid.NewGuid().ToString("N");

        try {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // An unwritable settings file means a new id every start, which is worse than one
            // stable id but better than refusing to sync. The caller cannot do anything about
            // it either, so it is not worth an error the user has to dismiss.
            Console.Error.WriteLine($"warning: could not save the client id: {ex.Message}");
        }

        return settings.ClientId;
    }
}
