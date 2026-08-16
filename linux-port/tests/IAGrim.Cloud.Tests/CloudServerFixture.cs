using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// A real backup server, on loopback, for the duration of the test run.
///
/// Starts the monolith from <c>scripts/build-sync-server.sh</c> against a throwaway
/// <c>STORAGE_PATH</c> and registers a user through the *actual* login endpoints — the same
/// three calls the client makes, with the pin code read out of the server's own
/// <c>core.db</c> in place of the e-mail nobody is going to receive here.
///
/// If the server has not been built the tests skip rather than fail: a checkout without Go
/// should still be able to run the rest of the suite. Run
/// <c>linux-port/scripts/build-sync-server.sh</c> once to enable them.
/// </summary>
public sealed class CloudServerFixture : IDisposable {
    private readonly Process? _process;

    public string? SkipReason { get; }
    public string Host { get; } = "";
    public string StoragePath { get; } = "";
    public string Email { get; } = "";
    public string Token { get; } = "";

    /// <summary>True when a server is up and a user is authenticated against it.</summary>
    public bool Available => SkipReason is null;

    public CloudServerFixture() {
        var binary = LocateServer();
        if (binary is null) {
            SkipReason = "backup server not built; run linux-port/scripts/build-sync-server.sh";
            return;
        }

        StoragePath = Path.Combine(Path.GetTempPath(), "iagd-cloud-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StoragePath);

        var port = FreePort();
        Host = $"http://127.0.0.1:{port}";

        var start = new ProcessStartInfo {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["STORAGE_PATH"] = StoragePath;
        start.Environment["PORT"] = port.ToString();
        start.Environment["GIN_MODE"] = "release";
        // Leaving these set would point the server's migration path at a real MySQL host.
        start.Environment.Remove("DATABASE_HOST");
        start.Environment.Remove("DATABASE_USER");
        start.Environment.Remove("DATABASE_NAME");
        start.Environment.Remove("DATABASE_PASSWORD");

        _process = Process.Start(start);
        if (_process is null) {
            SkipReason = "could not start the backup server";
            return;
        }

        // Drain the pipes, otherwise the server blocks on a full stdout buffer partway through
        // a run and every later request times out.
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        if (!WaitForHealth()) {
            SkipReason = "the backup server did not become healthy";
            return;
        }

        try {
            (Email, Token) = NewAccount();
        }
        catch (Exception ex) {
            SkipReason = $"could not register a test user: {ex.Message}";
        }
    }

    /// <summary>
    /// A fresh account, for a test that asserts on absolute item counts.
    ///
    /// The server partitions everything by user, so a per-test account is the only way two tests
    /// can both say "this collection now holds exactly one item" without one of them being wrong
    /// about whose item it is.
    /// </summary>
    public (string Email, string Token) NewAccount() {
        // Registration is throttled per e-mail *and per IP*, four attempts in four hours, and
        // every test here comes from 127.0.0.1. Clearing the counter is the harness working
        // around its own unusual usage; the server's rule is untouched, and the whole database
        // is thrown away at the end of the run.
        ClearThrottle();

        var email = $"tester-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        return (email, Register(email));
    }

    private void ClearThrottle() {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(StoragePath, "core.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        // The server holds this open in WAL mode; a second writer just has to be willing to wait.
        command.CommandText = "PRAGMA busy_timeout=5000; DELETE FROM throttleentry;";
        command.ExecuteNonQuery();
    }

    /// <summary>An HttpClient carrying the two headers every protected endpoint requires.</summary>
    public HttpClient Client() {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-User", Email);
        return client;
    }

    /// <summary>Points <see cref="CloudUris"/> at this server. Loopback only, always.</summary>
    public void UseUris() {
        Assert.StartsWith("http://127.0.0.1:", Host);
        Environment.SetEnvironmentVariable("IAGD_CLOUD_HOST", Host);
        CloudUris.Initialize(CloudUris.EnvLocalDev);
    }

    // The three real calls: ask for a login, read the code the server stored, exchange it.
    private string Register(string email) {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        var key = Guid.NewGuid().ToString();

        // Fire and forget, deliberately.
        //
        // /login writes the attempt row and *then* sends the pin code through AWS SES. With no
        // credentials and no SES to reach, that call sits there for about ten seconds before
        // failing — and this used to wait for it, once per test, which was almost the entire
        // runtime of this suite. Nothing here needs the response: the row this reads next is
        // already committed by the time the mail attempt starts, and its 500 says only that an
        // e-mail nobody is going to receive was not sent.
        //
        // The server handles the rest of that request on its own goroutine; the pin is polled for
        // below rather than read once, since the write and this read are now genuinely concurrent.
        try {
            client.GetAsync($"{Host}/login?email={Uri.EscapeDataString(email)}&token={key}")
                  .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            // The 500, or our own 500 ms timeout arriving first. Both are expected.
        }

        var code = WaitForPinCode(key)
                   ?? throw new InvalidOperationException($"no login attempt stored for {email}");

        var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("key", key),
            new KeyValuePair<string, string>("code", code),
        ]);
        var response = client.PostAsync($"{Host}/auth", form).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException($"/auth returned {response.StatusCode}: {body}");
        }

        return CloudJson.Deserialize<AuthResponse>(body)?.Token
               ?? throw new InvalidOperationException($"/auth returned no token: {body}");
    }

    private sealed class AuthResponse {
        public string? Token { get; set; }
    }

    /// <summary>
    /// The pin the server stored for this login attempt, waiting for it to appear.
    ///
    /// The row is written before the mail attempt, so this normally succeeds first time; the
    /// wait covers the case where the request has not reached the handler yet.
    /// </summary>
    private string? WaitForPinCode(string key) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) {
            var code = ReadPinCode(key);
            if (!string.IsNullOrEmpty(code)) return code;
            Thread.Sleep(25);
        }
        return null;
    }

    private string? ReadPinCode(string key) {
        // Read-only, and a copy: the server holds the database open in WAL mode.
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(StoragePath, "core.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code FROM authattempt WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private bool WaitForHealth() {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline) {
            if (_process?.HasExited == true) return false;
            try {
                if (client.GetAsync($"{Host}/health").GetAwaiter().GetResult().IsSuccessStatusCode) {
                    return true;
                }
            }
            catch (Exception) { /* not up yet */ }
            Thread.Sleep(200);
        }
        return false;
    }

    private static string? LocateServer() {
        var configured = Environment.GetEnvironmentVariable("IAGD_SYNC_SERVER");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        // tests/IAGrim.Cloud.Tests/bin/Debug/net10.0 -> repository root
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            var candidate = Path.Combine(directory.FullName, "build", "iagd-onlinesync", "Server", "bin", "monolith");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static int FreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() {
        try {
            if (_process is { HasExited: false }) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception) { /* already gone */ }

        _process?.Dispose();

        try {
            if (!string.IsNullOrEmpty(StoragePath) && Directory.Exists(StoragePath)) {
                Directory.Delete(StoragePath, recursive: true);
            }
        }
        catch (IOException) { /* best effort */ }
    }
}

/// <summary>
/// One server for the whole run. Starting the monolith per test class costs a second each and
/// they do not interfere: every test registers its own user, and the server partitions by user.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CloudServerCollection : ICollectionFixture<CloudServerFixture> {
    public const string Name = "backup server";
}
