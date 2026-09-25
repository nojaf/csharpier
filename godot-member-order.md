# Godot member order

A handoff for the agent implementing this. It adds an opt-in pass to CSharpier that reorders
the members of every class, struct and record in a file, following an order adapted from the
[GDScript style guide's code order](https://docs.godotengine.org/en/stable/tutorials/scripting/gdscript/gdscript_styleguide.html#code-order),
plus one extra rule: a method only calls methods declared above it.

The first consumer is a Godot 4 C# game (`~/Projects/tumult`, 26 `.cs` files). The design was
settled in conversation with the owner. The decisions below are final unless implementation
proves one impossible, in which case stop and ask rather than pick something else.

This repository uses OpenSpec (`openspec/`). If the change should go through it, turn this file
into a change under `openspec/changes/` first.

## Why inside CSharpier

The reorder and the formatter both need the parsed tree, and the reorder's output has to be
formatted anyway. Doing both in one pass means one command and one hook, and it means
CSharpier's own validator (`SyntaxNodeComparer`) confirms that the reorder only moved members.
There is precedent: CSharpier already sorts usings and modifiers.

## The option

- **Off by default.** Without the option, output must be byte-identical to today's.
- **A CLI flag on `format` and `check`:** `--member-order godot`. Make its value an enum, so
  that other orders could exist later. This is a fork-only feature and will not go upstream,
  so there is no need to follow upstream's reluctance to add options.
- **No config file support.** The consuming project must not need a `.csharpierrc` or any other
  new file just to opt in. The flag is passed by the owner's `csfmt` shell wrapper and by the
  game's Claude Code hook, which calls the binary directly.
- Plumb it from the CLI options into `PrinterOptions` (what `CSharpFormatter.FormatAsync`
  receives), and into `CodeFormatterOptions` for the library API and tests.

## The order

Applies to the member list of each `class`, `struct` and `record` (and `record struct`)
declaration. Interfaces and enums are left alone. Nested types are reordered recursively, each
by its own member list.

| # | Group | Members | Within the group |
|---|---|---|---|
| 1 | Nested types | enums first, then records, classes, structs, interfaces, unions, and delegates that are not signals | source order |
| 2 | Constants | `const` fields | public, then private |
| 3 | Signals | delegates with a `[Signal]` attribute, `event` declarations | source order |
| 4 | Exports | fields and properties with an `[Export]` attribute | source order |
| 5 | Static state | static fields and static properties | public, then private |
| 6 | Instance state | all other fields, properties and indexers | public, then private |
| 6b | State helpers | methods of groups 10 and 11 called from the bodies or initializers of groups 3 to 7, and the methods those call in turn | rule 2, then source order |
| 7 | Constructors | static constructor, then instance constructors, then a finalizer | source order |
| 8 | Engine overrides | see below | fixed order, then source order |
| 9 | Other overrides | methods with `override` that are not in group 8 | source order |
| 10 | Static methods | static methods, operators, conversion operators | rule 2, then source order |
| 11 | Instance methods | everything else | rule 2, then source order |

Notes on classification:

- **"Public, then private"** means by declared accessibility, most visible first: `public`,
  `internal`, `protected internal`, `protected`, `private protected`, `private`. A member with no
  modifier is `private`. Ties keep source order.
- **Attributes are matched by name, syntactically:** `Export`, `ExportAttribute`, `Godot.Export`,
  or `global::Godot.Export`, and the same forms for `Signal`. Only `[Export]` itself puts a
  member in group 4. `[ExportGroup]`, `[ExportSubgroup]` and `[ExportCategory]` sit on the
  export that follows them, so they move with it and need no handling of their own.
- **Group 8** is any method with the `override` modifier whose name starts with `_`. Godot's
  virtuals all do, and C# code otherwise never would. Order: `_Init`, `_EnterTree`, `_Ready`,
  `_Process`, `_PhysicsProcess`, then the rest (`_ExitTree`, `_Input`, `_UnhandledInput`,
  `_Notification`, `_Draw`, and so on) in source order.
- **A `static` member** goes to group 5 or 10, whatever else it is. The exceptions are `const`
  (group 2, implicitly static) and a static constructor (group 7).
- **Signals** are always delegates, so a `[Signal]` delegate is group 3, never group 1.

## Rule 2: callee above caller

**The rule:** in groups 6b, 10 and 11, a method may only call methods of its own type that are
declared above it.

Group 6b was added after the first version: `Match.cs` has properties like
`Tide => Wired(ExportTide)`, and the static `Wired` belongs above `_Ready`, next to the
properties that use it. Constructors feed 6b the same way: setup code sits above `_Ready`, so
its helpers do too, and since 6b sits between groups 6 and 7 they land above the constructor.

**Exempt as callers:** groups 8 and 9, because something outside the class calls them (the
engine, or a base class through virtual dispatch). They may call methods anywhere in the type.
Only groups 8 and 9 are exempt. Signal, export, state and constructor bodies and initializers
(groups 3 to 7) are callers too: the methods they call move to group 6b.

### What counts as a call

Each of these makes `Foo` a callee of the enclosing method:

```csharp
Foo(x);                  // bare invocation
this.Foo(x);             // explicit this
PlayerControl.Foo(x);    // qualified with the containing type's own name
() => Foo(x)             // inside a lambda or local function: belongs to the enclosing method
```

These do not count:

```csharp
Critters.CritterTapped += CrittersOnCritterTapped;  // method group (subscriptions do not count)
Callable.From(Foo);                                 // method group, same reasoning
other.Foo();                                        // another instance: syntax cannot tell its type
base.Foo();                                         // declared in the base class
Other.Foo();                                        // another type
```

- **Only invocations count.** A method group reference never does.
- **Name-based:** `Foo` must be the name of a method declared in the same type declaration
  (groups 6b, 10 or 11). A call to `Foo` means every overload of `Foo` must sit above the caller.
  No semantic model is needed, and none should be used.
- **Recursion:** a method calling itself is always allowed.
- **A local function or lambda** whose name shadows a member method: calls to it are not member
  calls. Checking local function names in the enclosing method is enough.

### Scope

**One type declaration in one file.** Methods declared elsewhere do not exist as far as rule 2 is
concerned, including other `partial` halves of the same class, generated or not. Calls between
two types in the same file do not count either, including between a nested type and its outer
type. (A nested type sits in group 1, above all of its outer type's methods, so its calls into
the outer type could never satisfy the rule.)

### The sort

A stable topological sort per group. Repeatedly place the earliest method, in source order,
whose callees in the same group have all been placed. This puts each callee just above its first
caller and leaves every other method in its current order, so the first run's diff is only what
the rule forces. Calls from group 11 into group 10 are satisfied by the group order and add no
constraint within either group.

**Call cycles:** methods that call each other, directly or through others (a strongly connected
component), form one block in the sort. The block's methods keep their source order among
themselves, the block is placed as one (just above its first caller, once every method it calls
outside the block has been placed), and everything else sorts normally. Each cycle produces a
warning naming its methods, saying they were kept together in source order. A cycle does not
stop the rest of the type from being ordered.

## When to refuse

In this case, leave the type declaration's member order untouched. Formatting still
applies as usual.

- **Preprocessor directives between members** (`#if`, `#region`, and so on). Moving members
  across them changes what they mean.

A refusal should be visible to the user. Check whether `CodeFormatterResult` or the CLI has a
channel for per-file warnings. If none fits, a silent no-op is acceptable for a first version,
but say so in the handoff back.

## Worked example

`tumult/Player/PlayerControl.cs`, members only, current order:

```csharp
[Signal] public delegate void SelectionChangedEventHandler(...);
[Signal] public delegate void MoveOrderedEventHandler(...);
private Critter? _selectedCritter;
private sealed record Wiring(Tide Tide, Critters Critters);
private Wiring? _wiring;
private Wiring Wired => ...;
private Tide Tide => Wired.Tide;
private Critters Critters => Wired.Critters;
private const int SlotCount = 3;
public void Initialize(...)                        // subscribes the two handlers with +=
public override void _UnhandledInput(...)          // calls FriendlyCritterUnder
private bool FriendlyCritterUnder(...)
public override void _UnhandledKeyInput(...)       // calls CommitSlot
private void CrittersOnCritterStoppedWalking(...)  // calls Deselect
private void CrittersOnCritterTapped(...)          // calls Deselect, Select
public void CommitSlot(int index)                  // calls LockOnTargets, Deselect
private void Deselect()
private void Select(...)
public Critter[] LockOnTargets()
```

Expected order:

```csharp
private sealed record Wiring(Tide Tide, Critters Critters);   // 1
private const int SlotCount = 3;                              // 2
[Signal] public delegate void SelectionChangedEventHandler(...);  // 3
[Signal] public delegate void MoveOrderedEventHandler(...);
private Critter? _selectedCritter;                            // 6, all private: source order
private Wiring? _wiring;
private Wiring Wired => ...;
private Tide Tide => Wired.Tide;
private Critters Critters => Wired.Critters;
public override void _UnhandledInput(...)                     // 8
public override void _UnhandledKeyInput(...)
public void Initialize(...)                                   // 11, += does not count
private bool FriendlyCritterUnder(...)
private void Deselect()                                       // pulled above its first caller
private void CrittersOnCritterStoppedWalking(...)
private void Select(...)
private void CrittersOnCritterTapped(...)
public Critter[] LockOnTargets()
public void CommitSlot(int index)
```

Make this a test case (trimmed to compile standalone), along with one test per group, the
overload case, a call cycle (and a cycle called from another method), a constructor calling a
helper, a `#region`, a nested type with its own members, and a partial
class.

## Where it goes in the code

- **The pass:** a new `MemberReorderer` (in `Src/CSharpier.Core/CSharp/`) that takes a
  `CompilationUnitSyntax` and returns a rewritten one. Call it in `CSharpFormatter.FormatAsync`
  between `GetRootAsync` (around line 105) and `Node.Print(rootNode, printingContext)` (around
  line 152), only when the option is on. When it moves anything, set a new
  `printingContext.State.ReorderedMembers` flag, alongside `ReorderedModifiers` in
  `CSharpPrintingContext`. OR it into the result the same way `ReorderedModifiers` is combined
  across the preprocessor-symbol loop that follows, and expose it on `CodeFormatterResult`.
- **Idempotence:** that loop re-parses and re-prints the formatted output. The pass must be
  idempotent: running it on its own output changes nothing.
- **Trivia:** a member's leading trivia (doc comment, attributes' comments, blank lines) moves
  with it. Normalise blank lines at the seams: the member that ends up first in the type must not
  gain a leading blank line, and a member moved between two others must keep its separation.
  Check the behaviour of the recent "don't insert new lines between elements" change
  (`eb62eb7a`) so the reorder does not fight it.
- **Validation:** `SyntaxNodeComparer` gets a `reorderedMembers` constructor parameter next to
  `reorderedModifiers`. When it is set, compare a type declaration's members as a multiset:
  sort both lists by a stable key and compare pairwise, the way `CompareUsingDirectives` handles
  usings (around line 528). Every member must still be compared deeply; only position is
  ignored. Pass the flag from every place that constructs the comparer:
  `CSharpier.Cli/FormattingEngine.cs` (around line 317), the tests' `BaseTest.cs`,
  `Samples.cs`, the benchmarks, and the playground.
- **Tests:** formatting tests pick their options from the directory name (`xml_ignore` becomes
  `XmlWhitespaceSensitivity.Ignore` in `BaseTest.BuildTests`). A `TestFiles/cs_godot` directory
  with its own test class, following that convention, fits. `.test` holds the input and
  `.expected.test` the output.

## Out of scope

- An analyzer or any warning for code that is already out of order. `csharpier check` already
  fails when formatting would change a file, so that covers enforcement.
- Semantic analysis of any kind.
- Ordering of top-level types in a file, of interface members, and of enum members.
