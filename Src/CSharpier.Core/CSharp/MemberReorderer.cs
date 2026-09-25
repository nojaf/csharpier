using CSharpier.Core.CSharp.SyntaxPrinter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpier.Core.CSharp;

// Reorders the members of classes, structs and records following an order adapted from the
// GDScript style guide, and within the method groups places a callee above its first caller.
// A type is left untouched when its members can't be moved safely, see TryGetRefusal.
internal class MemberReorderer
{
    private enum Group
    {
        NestedTypes,
        Constants,
        Signals,
        Exports,
        StaticState,
        InstanceState,
        StateHelpers,
        Constructors,
        EngineOverrides,
        OtherOverrides,
        StaticMethods,
        InstanceMethods,
    }

    private static readonly string[] EngineOverrideOrder =
    [
        "_Init",
        "_EnterTree",
        "_Ready",
        "_Process",
        "_PhysicsProcess",
    ];

    private readonly List<string> warnings;

    private MemberReorderer(List<string> warnings)
    {
        this.warnings = warnings;
    }

    public bool ReorderedMembers { get; private set; }

    public static CompilationUnitSyntax Reorder(
        CompilationUnitSyntax compilationUnit,
        List<string> warnings,
        out bool reorderedMembers
    )
    {
        var reorderer = new MemberReorderer(warnings);
        var newMembers = reorderer.VisitMembers(compilationUnit.Members, string.Empty);
        reorderedMembers = reorderer.ReorderedMembers;

        return reorderedMembers ? compilationUnit.WithMembers(newMembers) : compilationUnit;
    }

    // Visits the types in a member list without changing the order of the list itself.
    private SyntaxList<MemberDeclarationSyntax> VisitMembers(
        SyntaxList<MemberDeclarationSyntax> members,
        string containingName
    )
    {
        var result = new List<MemberDeclarationSyntax>(members.Count);
        var changed = false;
        var ignored = false;
        foreach (var member in members)
        {
            if (
                SyntaxPrinter.Token.HasLeadingCommentMatching(
                    member,
                    CSharpierIgnore.IgnoreEndRegex
                )
            )
            {
                ignored = false;
            }
            else if (
                SyntaxPrinter.Token.HasLeadingCommentMatching(
                    member,
                    CSharpierIgnore.IgnoreStartRegex
                )
            )
            {
                ignored = true;
            }

            var newMember =
                ignored || CSharpierIgnore.HasIgnoreComment(member)
                    ? member
                    : member switch
                    {
                        BaseNamespaceDeclarationSyntax namespaceDeclaration => this.VisitNamespace(
                            namespaceDeclaration
                        ),
                        TypeDeclarationSyntax typeDeclaration => this.VisitType(
                            typeDeclaration,
                            containingName
                        ),
                        _ => member,
                    };

            changed |= !ReferenceEquals(newMember, member);
            result.Add(newMember);
        }

        return changed ? SyntaxFactory.List(result) : members;
    }

    private BaseNamespaceDeclarationSyntax VisitNamespace(
        BaseNamespaceDeclarationSyntax namespaceDeclaration
    )
    {
        var visitedMembers = this.VisitMembers(namespaceDeclaration.Members, string.Empty);
        return visitedMembers == namespaceDeclaration.Members
            ? namespaceDeclaration
            : namespaceDeclaration.WithMembers(visitedMembers);
    }

    private TypeDeclarationSyntax VisitType(
        TypeDeclarationSyntax typeDeclaration,
        string containingName
    )
    {
        var name =
            containingName.Length == 0
                ? typeDeclaration.Identifier.ValueText
                : containingName + "." + typeDeclaration.Identifier.ValueText;

        var visitedMembers = this.VisitMembers(typeDeclaration.Members, name);
        if (visitedMembers != typeDeclaration.Members)
        {
            typeDeclaration = typeDeclaration.WithMembers(visitedMembers);
        }

        if (
            typeDeclaration
            is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
        )
        {
            return typeDeclaration;
        }

        var members = typeDeclaration.Members;
        var order = GetOrder(typeDeclaration, out var groups, out var cycles);
        foreach (var cycle in cycles)
        {
            this.warnings.Add(
                $"The methods {string.Join(", ", cycle)} of {name} call each other, so they were kept together in source order."
            );
        }

        var isUnchanged = true;
        for (var x = 0; x < order.Count; x++)
        {
            if (order[x] != x)
            {
                isUnchanged = false;
                break;
            }
        }

        if (isUnchanged)
        {
            return typeDeclaration;
        }

        if (TryGetRefusal(typeDeclaration, out var refusal))
        {
            this.warnings.Add($"Did not reorder the members of {name}: {refusal}.");
            return typeDeclaration;
        }

        var reordered = order.Select(o => members[o]).ToList();

        // the printer drops the blank lines in front of the first member, so the member that
        // takes its place hands its blank lines to the member that was first
        if (order[0] != 0)
        {
            var oldFirst = members[0];
            var newFirst = reordered[0];
            var (oldFirstSeparator, oldFirstRest) = SplitSeparator(oldFirst.GetLeadingTrivia());
            var (newFirstSeparator, newFirstRest) = SplitSeparator(newFirst.GetLeadingTrivia());
            reordered[0] = newFirst.WithLeadingTrivia(oldFirstSeparator.Concat(newFirstRest));
            reordered[order.IndexOf(0)] = oldFirst.WithLeadingTrivia(
                newFirstSeparator.Concat(oldFirstRest)
            );
        }

        // members keep their own blank lines within a group, but each group starts with one
        for (var x = 1; x < reordered.Count; x++)
        {
            if (groups[order[x]] == groups[order[x - 1]])
            {
                continue;
            }

            var leadingTrivia = reordered[x].GetLeadingTrivia();
            var (separator, _) = SplitSeparator(leadingTrivia);
            if (!separator.Any(o => o.RawSyntaxKind() is SyntaxKind.EndOfLineTrivia))
            {
                reordered[x] = reordered[x]
                    .WithLeadingTrivia(leadingTrivia.Insert(0, SyntaxFactory.EndOfLine("\n")));
            }
        }

        this.ReorderedMembers = true;
        return typeDeclaration.WithMembers(SyntaxFactory.List(reordered));
    }

    private static bool TryGetRefusal(TypeDeclarationSyntax typeDeclaration, out string refusal)
    {
        foreach (var member in typeDeclaration.Members)
        {
            if (member.GetLeadingTrivia().Any(o => o.IsDirective))
            {
                refusal = "there are preprocessor directives between its members";
                return true;
            }

            if (
                SyntaxPrinter.Token.HasLeadingCommentMatching(
                    member,
                    CSharpierIgnore.IgnoreStartRegex
                )
                || SyntaxPrinter.Token.HasLeadingCommentMatching(
                    member,
                    CSharpierIgnore.IgnoreEndRegex
                )
            )
            {
                refusal =
                    "there are csharpier-ignore-start or csharpier-ignore-end comments between its members";
                return true;
            }
        }

        if (typeDeclaration.CloseBraceToken.LeadingTrivia.Any(o => o.IsDirective))
        {
            refusal = "there are preprocessor directives between its members";
            return true;
        }

        refusal = string.Empty;
        return false;
    }

    // Splits leading trivia into the blank lines in front of the member and everything after them.
    private static (List<SyntaxTrivia> separator, List<SyntaxTrivia> rest) SplitSeparator(
        SyntaxTriviaList leadingTrivia
    )
    {
        var lastEndOfLine = -1;
        for (var x = 0; x < leadingTrivia.Count; x++)
        {
            var kind = leadingTrivia[x].RawSyntaxKind();
            if (kind is SyntaxKind.EndOfLineTrivia)
            {
                lastEndOfLine = x;
            }
            else if (kind is not SyntaxKind.WhitespaceTrivia)
            {
                break;
            }
        }

        return (
            leadingTrivia.Take(lastEndOfLine + 1).ToList(),
            leadingTrivia.Skip(lastEndOfLine + 1).ToList()
        );
    }

    // Returns the new order as indexes into the members, and the names of methods that call each other.
    private static List<int> GetOrder(
        TypeDeclarationSyntax typeDeclaration,
        out Group[] groups,
        out List<List<string>> cycles
    )
    {
        cycles = [];
        var members = typeDeclaration.Members;
        groups = members.Select(GetGroup).ToArray();
        MarkStateHelpers(typeDeclaration, groups);
        var memberGroups = groups;

        var result = new List<int>(members.Count);
        for (var group = Group.NestedTypes; group <= Group.InstanceMethods; group++)
        {
            var indexes = Enumerable
                .Range(0, members.Count)
                .Where(o => memberGroups[o] == group)
                .ToList();
            if (indexes.Count == 0)
            {
                continue;
            }

            if (group is Group.StateHelpers or Group.StaticMethods or Group.InstanceMethods)
            {
                result.AddRange(SortCalleesAboveCallers(typeDeclaration, indexes, cycles));
            }
            else
            {
                // OrderBy is a stable sort, so ties keep their source order
                result.AddRange(indexes.OrderBy(o => GetRankInGroup(members[o], group)));
            }
        }

        return result;
    }

    // Methods called from the bodies and initializers of state members (fields, properties,
    // indexers, events) and from constructors, and the methods those call in turn, move up to
    // just below the state so that they sit above everything using them.
    private static void MarkStateHelpers(TypeDeclarationSyntax typeDeclaration, Group[] groups)
    {
        var members = typeDeclaration.Members;
        var pending = new Stack<int>();
        for (var x = 0; x < members.Count; x++)
        {
            if (
                groups[x]
                is Group.Signals
                    or Group.Exports
                    or Group.StaticState
                    or Group.InstanceState
                    or Group.Constructors
            )
            {
                pending.Push(x);
            }
        }

        while (pending.Count > 0)
        {
            var calledNames = GetCalledMethodNames(members[pending.Pop()], typeDeclaration);
            for (var x = 0; x < members.Count; x++)
            {
                if (
                    groups[x] is Group.StaticMethods or Group.InstanceMethods
                    && GetMethodName(members[x]) is { } name
                    && calledNames.Contains(name)
                )
                {
                    groups[x] = Group.StateHelpers;
                    pending.Push(x);
                }
            }
        }
    }

    private static Group GetGroup(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case DelegateDeclarationSyntax delegateDeclaration
                when HasAttribute(delegateDeclaration.AttributeLists, "Signal"):
                return Group.Signals;
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                return Group.NestedTypes;
            case FieldDeclarationSyntax fieldDeclaration
                when HasModifier(fieldDeclaration.Modifiers, SyntaxKind.ConstKeyword):
                return Group.Constants;
            case ConstructorDeclarationSyntax or DestructorDeclarationSyntax:
                return Group.Constructors;
        }

        if (HasModifier(member.Modifiers, SyntaxKind.StaticKeyword))
        {
            return member is BaseFieldDeclarationSyntax or BasePropertyDeclarationSyntax
                ? Group.StaticState
                : Group.StaticMethods;
        }

        switch (member)
        {
            case EventFieldDeclarationSyntax or EventDeclarationSyntax:
                return Group.Signals;
            case FieldDeclarationSyntax
            or PropertyDeclarationSyntax when HasAttribute(member.AttributeLists, "Export"):
                return Group.Exports;
            case BaseFieldDeclarationSyntax or BasePropertyDeclarationSyntax:
                return Group.InstanceState;
            case MethodDeclarationSyntax methodDeclaration
                when HasModifier(methodDeclaration.Modifiers, SyntaxKind.OverrideKeyword):
                return methodDeclaration.Identifier.ValueText is ['_', ..]
                    ? Group.EngineOverrides
                    : Group.OtherOverrides;
            case OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax:
                return Group.StaticMethods;
            default:
                return Group.InstanceMethods;
        }
    }

    private static int GetRankInGroup(MemberDeclarationSyntax member, Group group)
    {
        switch (group)
        {
            case Group.NestedTypes:
                return member is EnumDeclarationSyntax ? 0 : 1;
            case Group.Constants:
            case Group.StaticState:
            case Group.InstanceState:
                return GetAccessibilityRank(member.Modifiers);
            case Group.Constructors:
                return member switch
                {
                    ConstructorDeclarationSyntax constructor
                        when HasModifier(constructor.Modifiers, SyntaxKind.StaticKeyword) => 0,
                    ConstructorDeclarationSyntax => 1,
                    _ => 2,
                };
            case Group.EngineOverrides:
                var index = Array.IndexOf(
                    EngineOverrideOrder,
                    ((MethodDeclarationSyntax)member).Identifier.ValueText
                );
                return index >= 0 ? index : EngineOverrideOrder.Length;
            default:
                return 0;
        }
    }

    private static int GetAccessibilityRank(SyntaxTokenList modifiers)
    {
        var isPublic = HasModifier(modifiers, SyntaxKind.PublicKeyword);
        var isInternal = HasModifier(modifiers, SyntaxKind.InternalKeyword);
        var isProtected = HasModifier(modifiers, SyntaxKind.ProtectedKeyword);
        var isPrivate = HasModifier(modifiers, SyntaxKind.PrivateKeyword);

        return (isPublic, isInternal, isProtected, isPrivate) switch
        {
            (true, _, _, _) => 0,
            (_, true, false, _) => 1,
            (_, true, true, _) => 2,
            (_, _, true, false) => 3,
            (_, _, true, true) => 4,
            _ => 5,
        };
    }

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
    {
        foreach (var modifier in modifiers)
        {
            if (modifier.RawSyntaxKind() == kind)
            {
                return true;
            }
        }

        return false;
    }

    // Matches Name, NameAttribute, Godot.Name and global::Godot.Name, without a semantic model.
    private static bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, string name)
    {
        foreach (var attributeList in attributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var attributeName = string.Concat(
                    attribute.Name.ToString().Where(o => !char.IsWhiteSpace(o))
                );
                if (attributeName.StartsWith("global::", StringComparison.Ordinal))
                {
                    attributeName = attributeName["global::".Length..];
                }

                if (attributeName.StartsWith("Godot.", StringComparison.Ordinal))
                {
                    attributeName = attributeName["Godot.".Length..];
                }

                if (attributeName == name || attributeName == name + "Attribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A stable topological sort: repeatedly place the earliest method, in source order, whose
    // callees in the same group have all been placed. Methods that call each other, directly or
    // through others, form a block that is placed as one, keeping its own source order.
    private static List<int> SortCalleesAboveCallers(
        TypeDeclarationSyntax typeDeclaration,
        List<int> indexes,
        List<List<string>> cycles
    )
    {
        var members = typeDeclaration.Members;
        var count = indexes.Count;
        var callees = new List<int>[count];
        for (var x = 0; x < count; x++)
        {
            var calledNames = GetCalledMethodNames(members[indexes[x]], typeDeclaration);
            var ownName = GetMethodName(members[indexes[x]]);
            callees[x] = [];
            for (var y = 0; y < count; y++)
            {
                var name = GetMethodName(members[indexes[y]]);
                // a call to its own name is treated as recursion, overloads can't be told apart
                if (name is not null && name != ownName && calledNames.Contains(name))
                {
                    callees[x].Add(y);
                }
            }
        }

        var reaches = new bool[count][];
        for (var x = 0; x < count; x++)
        {
            reaches[x] = new bool[count];
            var pending = new Stack<int>(callees[x]);
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (!reaches[x][next])
                {
                    reaches[x][next] = true;
                    callees[next].ForEach(pending.Push);
                }
            }
        }

        // each method's block is named after its earliest member in source order
        var blocks = new int[count];
        for (var x = 0; x < count; x++)
        {
            blocks[x] = x;
            for (var y = 0; y < x; y++)
            {
                if (reaches[x][y] && reaches[y][x])
                {
                    blocks[x] = blocks[y];
                    break;
                }
            }
        }

        for (var x = 0; x < count; x++)
        {
            var blockMembers = Enumerable.Range(0, count).Where(o => blocks[o] == x).ToList();
            if (blockMembers.Count > 1)
            {
                cycles.Add(
                    blockMembers
                        .Select(o => GetMethodName(members[indexes[o]])!)
                        .Distinct()
                        .ToList()
                );
            }
        }

        var placed = new bool[count];
        var result = new List<int>(count);
        while (result.Count < count)
        {
            // the blocks form an acyclic graph, so there is always a block that can be placed
            var block = Enumerable
                .Range(0, count)
                .First(x =>
                    blocks[x] == x
                    && !placed[x]
                    && Enumerable
                        .Range(0, count)
                        .Where(o => blocks[o] == x)
                        .All(o => callees[o].TrueForAll(c => blocks[c] == x || placed[blocks[c]]))
                );

            placed[block] = true;
            result.AddRange(
                Enumerable.Range(0, count).Where(o => blocks[o] == block).Select(o => indexes[o])
            );
        }

        return result;
    }

    private static string? GetMethodName(MemberDeclarationSyntax member) =>
        member is MethodDeclarationSyntax methodDeclaration
            ? methodDeclaration.Identifier.ValueText
            : null;

    // Collects the names invoked as Foo(), this.Foo() or TypeName.Foo(), including from lambdas
    // and local functions. Method groups, base.Foo() and other.Foo() don't count.
    private static HashSet<string> GetCalledMethodNames(
        MemberDeclarationSyntax member,
        TypeDeclarationSyntax typeDeclaration
    )
    {
        var typeName = typeDeclaration.Identifier.ValueText;
        var localNames = new HashSet<string>();
        var bareNames = new HashSet<string>();
        var memberNames = new HashSet<string>();

        foreach (var node in member.DescendantNodes())
        {
            switch (node)
            {
                case LocalFunctionStatementSyntax localFunction:
                    localNames.Add(localFunction.Identifier.ValueText);
                    break;
                case VariableDeclaratorSyntax variableDeclarator:
                    localNames.Add(variableDeclarator.Identifier.ValueText);
                    break;
                case ParameterSyntax parameter:
                    localNames.Add(parameter.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax designation:
                    localNames.Add(designation.Identifier.ValueText);
                    break;
                case InvocationExpressionSyntax invocation:
                    switch (invocation.Expression)
                    {
                        case SimpleNameSyntax simpleName:
                            bareNames.Add(simpleName.Identifier.ValueText);
                            break;
                        case MemberAccessExpressionSyntax
                        {
                            RawKind: (int)SyntaxKind.SimpleMemberAccessExpression,
                        } memberAccess
                            when memberAccess.Expression is ThisExpressionSyntax
                                || (
                                    memberAccess.Expression is SimpleNameSyntax qualifier
                                    && qualifier.Identifier.ValueText == typeName
                                ):
                            memberNames.Add(memberAccess.Name.Identifier.ValueText);
                            break;
                    }
                    break;
            }
        }

        bareNames.ExceptWith(localNames);
        memberNames.UnionWith(bareNames);
        return memberNames;
    }
}
