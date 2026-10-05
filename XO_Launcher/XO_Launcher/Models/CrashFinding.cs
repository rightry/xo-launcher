using System;
using System.Collections.Generic;
using System.Linq;

namespace XO_Launcher.Models
{
    public sealed class CrashFinding
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Advice { get; init; } = "";
        public string Severity { get; init; } = "提示";
        public string? Evidence { get; init; }
    }

    public sealed class CrashAnalysis
    {
        public string Headline { get; init; } = "";
        public string Excerpt { get; init; } = "";
        public IReadOnlyList<CrashFinding> Findings { get; init; } = Array.Empty<CrashFinding>();
        public bool HasKnownCause => Findings.Any(f => f.Id != "unknown");
    }
}
