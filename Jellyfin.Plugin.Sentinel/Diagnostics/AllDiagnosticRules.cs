using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// The complete, single source of truth for which rule sets Sentinel actually registers.
/// </summary>
/// <remarks>
/// Exists so the production registration (<c>PluginServiceRegistrator</c>) and any test that
/// needs to exercise the real, combined rule set can never drift apart — a test that instead
/// re-built <c>CoreTranscodeRules.All.Concat(AdvancedTranscodeRules.All)</c> itself would stay
/// green even if production later registered a different combination.
/// </remarks>
public static class AllDiagnosticRules
{
    /// <summary>
    /// Gets every diagnostic rule Sentinel registers, across every rule set.
    /// </summary>
    public static IReadOnlyList<DiagnosticRule> All { get; } =
        CoreTranscodeRules.All.Concat(AdvancedTranscodeRules.All).ToList();
}
