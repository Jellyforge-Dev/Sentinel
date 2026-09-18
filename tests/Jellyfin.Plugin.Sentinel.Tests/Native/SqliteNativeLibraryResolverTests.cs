using Jellyfin.Plugin.Sentinel.Native;
using System.Runtime.InteropServices;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Native;

public class SqliteNativeLibraryResolverTests
{
    [Theory]
    [InlineData(SentinelPlatform.Windows, Architecture.X64, "runtimes/win-x64/native/e_sqlite3.dll.win")]
    [InlineData(SentinelPlatform.Windows, Architecture.Arm64, "runtimes/win-arm64/native/e_sqlite3.dll.win")]
    [InlineData(SentinelPlatform.Linux, Architecture.X64, "runtimes/linux-x64/native/libe_sqlite3.so")]
    [InlineData(SentinelPlatform.Linux, Architecture.Arm64, "runtimes/linux-arm64/native/libe_sqlite3.so")]
    [InlineData(SentinelPlatform.MacOs, Architecture.X64, "runtimes/osx-x64/native/libe_sqlite3.dylib")]
    [InlineData(SentinelPlatform.MacOs, Architecture.Arm64, "runtimes/osx-arm64/native/libe_sqlite3.dylib")]
    public void GetRelativePath_ReturnsCorrectPath_ForEverySupportedCombination(
        SentinelPlatform platform, Architecture architecture, string expectedPath)
    {
        var result = SqliteNativeLibraryResolver.GetRelativePath(platform, architecture);

        Assert.Equal(expectedPath, result);
    }

    [Theory]
    [InlineData(SentinelPlatform.Windows, Architecture.X86)]
    [InlineData(SentinelPlatform.Windows, Architecture.Arm)]
    [InlineData(SentinelPlatform.Linux, Architecture.X86)]
    [InlineData(SentinelPlatform.Linux, Architecture.Arm)]
    [InlineData(SentinelPlatform.Linux, Architecture.S390x)]
    [InlineData(SentinelPlatform.Linux, Architecture.Ppc64le)]
    [InlineData(SentinelPlatform.MacOs, Architecture.Arm)]
    public void GetRelativePath_ReturnsNull_ForUnsupportedCombinations_InsteadOfGuessingWrongBinary(
        SentinelPlatform platform, Architecture architecture)
    {
        var result = SqliteNativeLibraryResolver.GetRelativePath(platform, architecture);

        Assert.Null(result);
    }
}
