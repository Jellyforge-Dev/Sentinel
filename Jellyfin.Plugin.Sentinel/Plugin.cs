using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.Sentinel.Configuration;
using Jellyfin.Plugin.Sentinel.Native;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Sentinel;

/// <summary>
/// The main Sentinel plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string SqliteNativeLibraryName = "e_sqlite3";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Jellyfin's plugin loader (Emby.Server.Implementations.Plugins.PluginLoadContext) resolves
        // managed assemblies via AssemblyDependencyResolver but never overrides LoadUnmanagedDll, so it
        // provides no automatic native-library resolution on any platform. Without this resolver,
        // SQLitePCLRaw's lookup for "e_sqlite3" fails inside Jellyfin's custom load context even though
        // it works fine in a normal `dotnet test` process — which is why this wasn't caught by automated
        // tests and only surfaced on a real server.
        //
        // Separately, on Windows specifically, Jellyfin's own plugin manager
        // (Emby.Server.Implementations.Plugins.PluginManager.TryGetPluginDlls) recursively globs every
        // *.dll file under the plugin's folder and tries to load each one as a managed .NET assembly. A
        // native e_sqlite3.dll would match that glob and crash the whole plugin with a
        // BadImageFormatException. The Windows native library therefore ships as "e_sqlite3.dll.win" (a
        // non-".dll" extension) so it's invisible to that glob — the same technique used by
        // unfedorg/jellyfin-plugin-pdfcover for its own native dependency. Linux (.so) and macOS (.dylib)
        // don't need renaming since those extensions never match the *.dll glob.
        //
        // This registration must never throw: an exception here escapes the Plugin constructor, which
        // Jellyfin's plugin manager calls during server startup — that's the same class of "one plugin's
        // bug takes down the whole host" failure PluginServiceRegistrator's own database-init guard
        // exists to prevent (see the comment there). SetDllImportResolver itself throws
        // InvalidOperationException if a resolver is already registered for this assembly (e.g. on a
        // plugin reload) — that's not a real failure, the existing resolver is still perfectly valid, so
        // it's caught and ignored rather than left to propagate.
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, ResolveSqliteNativeLibrary);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <inheritdoc />
    public override string Name => "Sentinel";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4b5e3a79-7798-4066-841e-db7eb0125dc9");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "troubleshoot"
            }
        ];
    }

    /// <summary>
    /// Manually resolves the native SQLite library from this plugin's own <c>runtimes/&lt;rid&gt;/native/</c>
    /// folder, since Jellyfin's plugin load context does not do this automatically. See the comment in
    /// the constructor for the full explanation.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="IntPtr.Zero"/> (never throws) for every case it doesn't handle — an
    /// unsupported platform/architecture, a missing file, or a load failure all fall back to the
    /// runtime's own default resolution rather than risk crashing whatever called into SQLite.
    /// </remarks>
    private static IntPtr ResolveSqliteNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, SqliteNativeLibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        SentinelPlatform? platform = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            platform = SentinelPlatform.Windows;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            platform = SentinelPlatform.MacOs;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            platform = SentinelPlatform.Linux;
        }

        if (platform is null)
        {
            return IntPtr.Zero;
        }

        var relativePath = SqliteNativeLibraryResolver.GetRelativePath(platform.Value, RuntimeInformation.ProcessArchitecture);
        if (relativePath is null)
        {
            // No native library shipped for this platform/architecture combination (e.g. 32-bit ARM,
            // x86). Falling back to the wrong architecture's binary would throw inside NativeLibrary.Load
            // — this is exactly the bug this whole resolver exists to avoid, just one level up.
            return IntPtr.Zero;
        }

        var pluginFolder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (pluginFolder is null)
        {
            return IntPtr.Zero;
        }

        var nativePath = Path.Combine(pluginFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(nativePath))
        {
            return IntPtr.Zero;
        }

        try
        {
            return NativeLibrary.Load(nativePath);
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
        catch (BadImageFormatException)
        {
            return IntPtr.Zero;
        }
    }
}
