using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// Evaluates every registered <see cref="DiagnosticRule"/> against a <see cref="PlaybackEvent"/>.
/// A single event can produce zero, one, or several diagnoses — one per matching rule.
/// </summary>
public sealed class RuleEngine
{
    private readonly IReadOnlyList<DiagnosticRule> _rules;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleEngine"/> class with the given rule set.
    /// </summary>
    /// <param name="rules">The diagnostic rules to evaluate.</param>
    public RuleEngine(IReadOnlyList<DiagnosticRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>
    /// Diagnoses a playback event by evaluating all registered rules.
    /// </summary>
    /// <param name="playbackEvent">The playback event to diagnose.</param>
    /// <returns>A list of diagnoses that matched the event, or an empty list if no rules matched.</returns>
    public IReadOnlyList<Diagnosis> Diagnose(PlaybackEvent playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);

        var diagnoses = new List<Diagnosis>();

        foreach (var rule in _rules)
        {
            if (!rule.Predicate(playbackEvent))
            {
                continue;
            }

            diagnoses.Add(new Diagnosis
            {
                Code = rule.Code,
                Confidence = rule.Confidence,
                Evidence = new[]
                {
                    $"PlayMethod = {playbackEvent.PlayMethod}",
                    $"TranscodeReasons = {playbackEvent.TranscodeReasons}",
                    $"Matched rule = {rule.Code}"
                },
                Explanation = rule.Explain(playbackEvent),
                Recommendation = rule.Recommendation
            });
        }

        return diagnoses;
    }
}
