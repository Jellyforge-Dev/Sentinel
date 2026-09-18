using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.Sentinel.Configuration;
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
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, ResolveSqliteNativeLibrary);
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
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Manually resolves the native SQLite library from this plugin's own <c>runtimes/&lt;rid&gt;/native/</c>
    /// folder, since Jellyfin's plugin load context does not do this automatically. See the comment in
    /// the constructor for the full explanation.
    /// </summary>
    private static IntPtr ResolveSqliteNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, SqliteNativeLibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        string rid;
        string fileName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            fileName = "e_sqlite3.dll.win";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            fileName = "libe_sqlite3.dylib";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            fileName = "libe_sqlite3.so";
        }
        else
        {
            return IntPtr.Zero;
        }

        var pluginFolder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (pluginFolder is null)
        {
            return IntPtr.Zero;
        }

        var nativePath = Path.Combine(pluginFolder, "runtimes", rid, "native", fileName);
        return File.Exists(nativePath) ? NativeLibrary.Load(nativePath) : IntPtr.Zero;
    }
}
