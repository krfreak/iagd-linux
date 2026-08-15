using System.Diagnostics;

namespace IAGrim.Platform;

/// <summary>
/// Opens a URL in whatever browser the desktop session has.
///
/// Upstream hands links to <c>ShellExecute</c> from the client process, and the equivalent here
/// has to happen host-side for the same reason: the app window is a WebKitGTK view with no
/// handler for <c>window.open</c> or for <c>target="_blank"</c> links, so a page that tries to
/// open its own links reaches nothing at all — silently.
/// </summary>
public static class DesktopBrowser {
    /// <summary>
    /// Hands <paramref name="url"/> to xdg-open. Returns false rather than throwing when there
    /// is no desktop session — a headless host, a container, a machine reached over the network
    /// — because every caller has somewhere else to go: the page either shows the address or,
    /// when it is running in a real browser rather than the app window, opens it itself.
    /// </summary>
    public static bool Open(string url, out string? error) {
        try {
            using var opener = Process.Start(new ProcessStartInfo {
                FileName = "xdg-open",
                ArgumentList = { url },
                UseShellExecute = false,
            });

            error = null;
            return opener is not null;
        }
        catch (Exception ex) {
            error = ex.Message;
            return false;
        }
    }
}
