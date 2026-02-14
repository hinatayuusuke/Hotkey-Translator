using System.Collections.Generic;
using System.Linq;

namespace Hotkey_Translator.Services.Capture;

internal sealed class CaptureExecutionTrace
{
    public CaptureExecutionTrace(IReadOnlyList<CaptureAttemptResult> attempts)
    {
        Attempts = attempts;
    }

    public IReadOnlyList<CaptureAttemptResult> Attempts { get; }

    public string BuildSummary()
    {
        if (Attempts.Count == 0)
        {
            return "no-attempt";
        }

        return string.Join(" | ", Attempts.Select(BuildAttemptSummary));
    }

    private static string BuildAttemptSummary(CaptureAttemptResult attempt)
    {
        var summary = $"provider={attempt.ProviderKind},success={attempt.Success},reason={attempt.Reason}";
        if (attempt.IsBlackFrame)
        {
            summary += ",black=true";
        }

        if (attempt.CooldownUntil.HasValue)
        {
            summary += $",cooldownUntil={attempt.CooldownUntil.Value:HH:mm:ss}";
        }

        if (!string.IsNullOrWhiteSpace(attempt.Detail))
        {
            summary += $",detail={attempt.Detail}";
        }

        return summary;
    }
}
