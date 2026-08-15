using System.IO.Compression;
using System.Net;
using IAGrim.Cloud.Dto;
using IAGrim.Platform;

namespace IAGrim.Cloud;

/// <summary>
/// Zipping Grim Dawn's own save files and putting them in the cloud — upstream's
/// <c>CharacterBackupService</c> plus the parts of <c>Utilities/Cloud/FileBackup.cs</c> it uses.
///
/// This is the game's data rather than this tool's, and it is the only thing here that uploads
/// something other than item metadata, so the limits are deliberately tight and all of them are
/// upstream's: seven file extensions, one megabyte per file, and only characters whose
/// <c>player.gdc</c> is newer than the last successful run. The server checks the extensions
/// again on its side and rejects the archive otherwise.
///
/// On Linux the saves live inside the Proton prefix rather than under My Documents, which is why
/// the save directory is a constructor argument instead of a static path.
/// </summary>
public sealed class CharacterBackupService {
    /// <summary>Upstream's interval: ten minutes.</summary>
    public const long CooldownMs = 1000 * 60 * 10;

    /// <summary>
    /// What may go in an archive. Upstream's <c>AcceptedFileFormats</c>; the server's own
    /// allowlist adds <c>.gdd</c>'s neighbours but is otherwise the same set.
    /// </summary>
    public static readonly string[] AcceptedFileFormats =
        [".gdc", ".gdd", ".fow", ".dat", ".bin", ".gst", ".gsh"];

    /// <summary>Upstream's per-file cap. Anything larger is left out of the archive.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>A character save smaller than this is treated as corrupt and skipped.</summary>
    public const long MinCharacterBytes = 4 * 1024;

    private readonly ICloudSettings _settings;
    private readonly AuthService _authService;
    private readonly string _savePath;
    private readonly string _stagingPath;
    private readonly ActionCooldown _cooldown;
    private bool _isActive = true;

    public CharacterBackupService(
        ICloudSettings settings,
        AuthService authService,
        string savePath,
        string stagingPath,
        long cooldownMs = CooldownMs) {
        _settings = settings;
        _authService = authService;
        _savePath = savePath;
        _stagingPath = stagingPath;
        _cooldown = new ActionCooldown(cooldownMs);
    }

    /// <summary>
    /// Upstream suspends this while the game is running (it watches for the process), because
    /// zipping a save Grim Dawn is writing produces a corrupt archive.
    /// </summary>
    public void SetIsActive(bool active) => _isActive = active;

    public void Execute() {
        if (_authService.CheckAuthentication() != AuthService.AccessStatus.Authorized) return;
        if (!SaveDirectoryExists()) return;
        if (!_isActive) return;

        // Discards the result: the timed pass has nobody watching it, and its outcome is already
        // reflected in the timestamp it does or does not advance. A pass the user asked for goes
        // through ExecuteInternal directly, where the result is shown.
        _cooldown.ExecuteIfReady(() => ExecuteInternal());
    }

    /// <summary>Whether there is anything to back up at all. Upstream's <c>MyDocumentsGrimDawnExists</c>.</summary>
    public bool SaveDirectoryExists() => Directory.Exists(Path.Combine(_savePath, "main"));

    /// <summary>
    /// One backup pass. Public so a "back up now" button does not have to wait out the cooldown.
    ///
    /// The timestamp advances only if <b>everything</b> succeeded, so one failed character means
    /// the whole set is retried rather than that character being skipped until it changes again.
    /// </summary>
    public CharacterBackupResult ExecuteInternal() {
        var lastSync = _settings.LastCharSyncUtc;
        var highestTimestamp = GetHighestCharacterTimestamp();
        var characters = ListCharactersNewerThan(lastSync);

        var everythingSucceeded = true;
        var uploaded = new List<string>();
        var failed = new List<string>();

        foreach (var character in characters) {
            var filename = Path.Combine(_stagingPath, $"{DateTime.Now.DayOfWeek}-{character}.zip");
            try {
                BackupCharacter(filename, character);
            }
            catch (IOException) {
                everythingSucceeded = false;
                failed.Add(character);
                continue;
            }

            if (Post($"{CloudUris.UploadCharacterUrl}?name={WebUtility.UrlEncode(character)}", filename)) {
                uploaded.Add(character);
            }
            else {
                everythingSucceeded = false;
                failed.Add(character);
            }
        }

        if (IsStashFilesNewerThan(lastSync)) {
            var filename = Path.Combine(_stagingPath, $"{DateTime.Now.DayOfWeek}-common.zip");
            BackupCommon(filename);
            var name = $"StashFiles-{DateTime.Now.DayOfWeek}";
            if (Post($"{CloudUris.UploadCharacterUrl}?name={name}", filename)) {
                uploaded.Add(name);
            }
            else {
                everythingSucceeded = false;
                failed.Add(name);
            }
        }

        if (everythingSucceeded) {
            _settings.LastCharSyncUtc = highestTimestamp;
            _settings.Save();
        }

        return new CharacterBackupResult(uploaded, failed, everythingSucceeded);
    }

    /// <summary>What has already been backed up, for the "view characters" link.</summary>
    public List<CharacterListDto> ListBackedUpCharacters() {
        try {
            return _authService.GetRestService()?.Get<CharacterListDto[]>(CloudUris.ListCharacterUrl!)?.ToList() ?? [];
        }
        catch (Exception) {
            return [];
        }
    }

    /// <summary>
    /// A short-lived, pre-signed URL for one backup. The file itself never comes through this
    /// application — the link goes to the browser.
    /// </summary>
    public string? GetDownloadUrl(string character) => RequestDownload(character).Url;

    /// <summary>
    /// A download link, with enough of the failure to tell the user something useful.
    ///
    /// "Not found" and "the link could not be signed" are different problems and the second one
    /// is temporary — and they are easy to confuse here, because the server writes the character's
    /// row *before* it stores the file. A character can therefore be listed and still have
    /// nothing behind it, which is exactly the case a bare "no backup" message describes wrongly.
    /// </summary>
    public CharacterDownload RequestDownload(string character) {
        try {
            var url = $"{CloudUris.DownloadCharacterUrl}?name={WebUtility.UrlEncode(character)}";
            var rest = _authService.GetRestService();
            if (rest is null) return new CharacterDownload(null, 0, "You are not signed in.");

            var result = rest.Get<CharacterDownloadUrlDto>(url);
            return result?.Url is { Length: > 0 } signed
                ? new CharacterDownload(signed, 200, null)
                : new CharacterDownload(null, 200, $"The server did not return a link for {character}.");
        }
        catch (CloudHttpException ex) when (ex.Code == (int)System.Net.HttpStatusCode.NotFound) {
            return new CharacterDownload(null, ex.Code, $"There is no backup of {character} on the server.");
        }
        catch (CloudHttpException ex) {
            // Most often the storage backend: the character is known, the link could not be made.
            return new CharacterDownload(null, ex.Code,
                $"The backup service could not produce a download link for {character} (error {ex.Code}). "
                + "This is usually temporary.");
        }
        catch (Exception) {
            return new CharacterDownload(null, 0, "Could not reach the backup service.");
        }
    }

    // ------------------------------------------------------------------ the file rules

    /// <summary>
    /// Characters whose save has changed since <paramref name="since"/>. Upstream's
    /// <c>ListCharactersNewerThan</c>: a directory under <c>Save/main</c> with a
    /// <c>player.gdc</c> that is newer and at least 4 KB.
    /// </summary>
    public IReadOnlyList<string> ListCharactersNewerThan(DateTime since) {
        var characterFolder = Path.Combine(_savePath, "main");
        if (!Directory.Exists(characterFolder)) return [];

        var result = new List<string>();
        foreach (var character in Directory.GetDirectories(characterFolder)) {
            var save = Path.Combine(character, "player.gdc");
            if (!File.Exists(save)) continue;
            if (File.GetLastWriteTimeUtc(save) <= since) continue;
            // Below 4 KB it is almost certainly a half-written file, and uploading it over a good
            // backup is worse than skipping it.
            if (new FileInfo(save).Length < MinCharacterBytes) continue;

            result.Add(Path.GetFileName(character));
        }

        return result;
    }

    /// <summary>
    /// Whether the shared stash files have changed.
    ///
    /// <b>This looks in a directory that does not exist, and that is upstream's code.</b> It
    /// joins <c>"Save"</c> onto a path that already ends in <c>Save</c>, so it tests
    /// <c>.../Grim Dawn/Save/Save/transfer.gst</c> and always answers false — the shared stash is
    /// in practice never uploaded by the Windows tool. Every neighbouring method
    /// (<c>GetHighestCharacterTimestamp</c>, <c>BackupCommon</c>) uses the correct path.
    ///
    /// Reproduced rather than corrected. Fixing it here would make this port upload a file to
    /// somebody else's storage that the tool it is a port of has never uploaded, which is not a
    /// decision a port gets to make on the service owner's behalf. It is written up in PORTING.md
    /// so it can be changed deliberately.
    /// </summary>
    public bool IsStashFilesNewerThan(DateTime since) {
        var gameSaves = Path.Combine(_savePath, "Save");
        foreach (var file in new[] { "transfer.gst", "transfer.gsh" }) {
            var filename = Path.Combine(gameSaves, file);
            if (File.Exists(filename) && File.GetLastWriteTimeUtc(filename) > since) return true;
        }

        return false;
    }

    /// <summary>
    /// The newest modification time across every character and the two stash files. Stored after
    /// a successful run so the next one only considers what has changed.
    /// </summary>
    public DateTime GetHighestCharacterTimestamp() {
        var characterFolder = Path.Combine(_savePath, "main");
        if (!Directory.Exists(characterFolder)) return default;

        var candidates = Directory.GetDirectories(characterFolder).ToList();
        foreach (var file in new[] { "transfer.gst", "transfer.gsh" }) {
            var filename = Path.Combine(_savePath, file);
            if (File.Exists(filename)) candidates.Add(filename);
        }

        if (candidates.Count == 0) return default;

        return candidates.Select(File.GetLastWriteTimeUtc).Max();
    }

    /// <summary>Zips one character's directory. Overwrites any previous archive for the same day.</summary>
    public void BackupCharacter(string target, string character) {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var source = Path.Combine(_savePath, "main", character);
        var files = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories);

        if (File.Exists(target)) File.Delete(target);

        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in files) {
            if (!IsAcceptedFileFormat(file)) continue;
            if (new FileInfo(file).Length > MaxFileBytes) continue;

            // The entry name keeps the character directory, so an archive restores into the
            // right place rather than as a heap of loose files.
            zip.CreateEntryFromFile(file, $"main/{character}/{Path.GetFileName(file)}");
        }

        zip.Comment = $"This backup of {character} was created at {DateTime.Now:G}.";
    }

    /// <summary>Zips the shared stash files, which live directly in the save directory.</summary>
    public void BackupCommon(string target) {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target)) File.Delete(target);

        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(_savePath, "*.*", SearchOption.TopDirectoryOnly)) {
            if (!IsAcceptedFileFormat(file)) continue;
            if (new FileInfo(file).Length > MaxFileBytes) continue;

            zip.CreateEntryFromFile(file, Path.GetFileName(file));
        }

        zip.Comment = $"This backup of your stash files was created at {DateTime.Now:G}.";
    }

    /// <summary>
    /// Upstream's rule, including the parenthesis test: Grim Dawn writes "player (1).gdc" style
    /// copies, and those are duplicates rather than saves worth uploading.
    /// </summary>
    public static bool IsAcceptedFileFormat(string path) =>
        AcceptedFileFormats.Contains(Path.GetExtension(path)) && !path.Contains('(');

    /// <summary>
    /// Uploads one archive as multipart form data under the field name <c>file</c>, which is what
    /// the server reads.
    /// </summary>
    private bool Post(string url, string filename) {
        var authProvider = _authService.GetAuthProvider();
        if (authProvider is null) return false;

        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authProvider.GetToken());
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-User", authProvider.GetUser());

            using var content = new MultipartFormDataContent();
            using var stream = File.OpenRead(filename);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            content.Add(file, "file", Path.GetFileName(filename));

            return client.PostAsync(url, content).GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception) {
            return false;
        }
    }

    internal sealed class CharacterDownloadUrlDto {
        public string? Url { get; set; }
    }
}

/// <summary>
/// What one backup pass did.
///
/// Upstream's is a bool held in a local, because the only consumer is the decision about whether
/// to advance the timestamp. Here it comes back out so the panel can say which characters went
/// and which did not: "backup failed" with no names is a message nobody can act on, and character
/// saves are the one thing here that this tool cannot regenerate from anywhere.
/// </summary>
/// <param name="Uploaded">Characters (and the stash archive) that reached the server.</param>
/// <param name="Failed">Ones that did not. These are retried on the next pass.</param>
/// <param name="EverythingSucceeded">
/// Whether the high-water mark advanced. False leaves it, so a partial run retries the whole set
/// rather than skipping what failed until it next changes.
/// </param>
/// <summary>
/// The outcome of asking for one character's download link.
/// </summary>
/// <param name="Url">The signed link, valid for about five minutes. Null when it failed.</param>
/// <param name="StatusCode">The server's status, or 0 if it was never reached.</param>
/// <param name="Error">Why it failed, phrased for the person who pressed the button.</param>
public sealed record CharacterDownload(string? Url, int StatusCode, string? Error);

public sealed record CharacterBackupResult(
    IReadOnlyList<string> Uploaded,
    IReadOnlyList<string> Failed,
    bool EverythingSucceeded) {
    public bool DidNothing => Uploaded.Count == 0 && Failed.Count == 0;
}
