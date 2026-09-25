using Microsoft.CodeAnalysis;

namespace CSharpier.Core;

public class CodeFormatterResult
{
    internal CodeFormatterResult() { }

    public string Code { get; internal init; } = string.Empty;
    internal string DocTree { get; init; } = string.Empty;
    internal string AST { get; init; } = string.Empty;
    internal string WarningMessage { get; init; } = string.Empty;
    public IEnumerable<Diagnostic> ErrorDiagnostics { get; internal init; } = [];

    internal string FailureMessage { get; init; } = string.Empty;

    internal static readonly CodeFormatterResult Null = new();

    internal bool ReorderedModifiers { get; init; }
    internal bool ReorderedUsingsWithDisabledText { get; init; }
    internal bool MovedTrailingTrivia { get; init; }
    internal bool ReorderedMembers { get; init; }

    // types whose members were left in their original order, with the reason
    internal IReadOnlyList<string> MemberOrderWarnings { get; init; } = [];
}
