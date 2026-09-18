using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Sentinel.Native;

/// <summary>
/// The operating system families Sentinel ships a native SQLite library for. Public only so it
/// can appear in xUnit <c>[Theory]</c>/<c>[InlineData]</c> test signatures, which must be public
/// end to end — <see cref="SqliteNativeLibraryResolver"/> itself stays internal.
/// </summary>
public enum SentinelPlatform
{
    /// <summary>Windows.</summary>
    Windows,

    /// <summary>Linux (glibc).</summary>
    Linux,

    /// <summary>macOS.</summary>
    MacOs
}

/// <summary>
/// Pure, testable mapping from (platform, architecture) to the native SQLite library's path
/// relative to the plugin folder. Kept separate from <see cref="Plugin"/> so the exact
/// combination that caused a real production bug (an incomplete platform/architecture mapping
/// silently falling back to the wrong binary) can be unit tested directly.
/// </summary>
internal static class SqliteNativeLibraryResolver
{
    /// <summary>
    /// Returns the native SQLite library's path relative to the plugin folder for the given
    /// platform and process architecture, or null if Sentinel doesn't ship a native library for
    /// that combination. Returning null here — rather than guessing a fallback — is deliberate:
    /// loading the wrong architecture's binary throws inside the DI startup path and can crash
    /// the entire Jellyfin host, not just disable this plugin.
    /// </summary>
    public static string? GetRelativePath(SentinelPlatform platform, Architecture architecture)
    {
        var rid = (platform, architecture) switch
        {
            (SentinelPlatform.Windows, Architecture.X64) => "win-x64",
            (SentinelPlatform.Windows, Architecture.Arm64) => "win-arm64",
            (SentinelPlatform.Linux, Architecture.X64) => "linux-x64",
            (SentinelPlatform.Linux, Architecture.Arm64) => "linux-arm64",
            (SentinelPlatform.MacOs, Architecture.X64) => "osx-x64",
            (SentinelPlatform.MacOs, Architecture.Arm64) => "osx-arm64",
            _ => null
        };

        if (rid is null)
        {
            return null;
        }

        // Windows ships as ".dll.win", not ".dll" — see the comment in Plugin.cs's constructor
        // for why: Jellyfin's plugin loader globs every *.dll under the plugin folder and tries
        // to load each one as a managed assembly, which crashes on a native library.
        var fileName = platform switch
        {
            SentinelPlatform.Windows => "e_sqlite3.dll.win",
            SentinelPlatform.MacOs => "libe_sqlite3.dylib",
            _ => "libe_sqlite3.so"
        };

        return $"runtimes/{rid}/native/{fileName}";
    }
}
