using AwesomeAssertions;
using CSharpier.Core;
using CSharpier.Core.CSharp;

namespace CSharpier.Tests.CSharp;

internal sealed class MemberReordererTests
{
    private static readonly CodeFormatterOptions Godot = new() { MemberOrder = MemberOrder.Godot };

    [Test]
    public void Default_Options_Keep_Member_Order()
    {
        var code = """
            class C
            {
                void Caller() => Callee();

                void Callee() { }

                const int Constant = 1;
            }

            """;

        var result = CSharpFormatter.Format(code);

        result.Code.Should().Be(code);
        result.ReorderedMembers.Should().BeFalse();
    }

    [Test]
    public void Godot_Option_Reorders_Members()
    {
        var code = """
            class C
            {
                void Caller() => Callee();

                void Callee() { }

                const int Constant = 1;
            }
            """;

        var result = CSharpFormatter.Format(code, Godot);

        result
            .Code.Should()
            .Be(
                """
                class C
                {
                    const int Constant = 1;

                    void Callee() { }

                    void Caller() => Callee();
                }

                """
            );
        result.ReorderedMembers.Should().BeTrue();
        result.MemberOrderWarnings.Should().BeEmpty();
    }

    [Test]
    public void Call_Cycle_Is_Kept_Together_And_Warns()
    {
        var code = """
            class C
            {
                void A() => B();

                void B() => A();

                const int Constant = 1;
            }
            """;

        var result = CSharpFormatter.Format(code, Godot);

        result
            .Code.Should()
            .Be(
                """
                class C
                {
                    const int Constant = 1;

                    void A() => B();

                    void B() => A();
                }

                """
            );
        result
            .MemberOrderWarnings.Should()
            .Equal(
                "The methods A, B of C call each other, so they were kept together in source order."
            );
    }

    [Test]
    public void Directive_Between_Members_Leaves_Order_And_Warns()
    {
        var code = """
            class C
            {
                #region Methods
                void Method() { }
                #endregion

                const int Constant = 1;
            }

            """;

        var result = CSharpFormatter.Format(code, Godot);

        result.Code.Should().Be(code);
        result
            .MemberOrderWarnings.Should()
            .Equal(
                "Did not reorder the members of C: there are preprocessor directives between its members."
            );
    }

    [Test]
    public void Directive_Between_Members_Already_In_Order_Does_Not_Warn()
    {
        var code = """
            class C
            {
                const int Constant = 1;

                #region Methods
                void Method() { }
                #endregion
            }

            """;

        var result = CSharpFormatter.Format(code, Godot);

        result.Code.Should().Be(code);
        result.MemberOrderWarnings.Should().BeEmpty();
    }

    [Test]
    public void Nested_Type_Name_Is_Qualified_In_Warning()
    {
        var code = """
            class Outer
            {
                class Inner
                {
                    void A() => B();

                    void B() => A();
                }
            }

            """;

        var result = CSharpFormatter.Format(code, Godot);

        result
            .MemberOrderWarnings.Should()
            .Equal(
                "The methods A, B of Outer.Inner call each other, so they were kept together in source order."
            );
    }

    [Test]
    public void Calls_Inside_Conditional_Compilation_Count()
    {
        var code = """
            class C
            {
                void Caller()
                {
            #if DEBUG
                    Callee();
            #endif
                }

                void Callee() { }
            }
            """;

        var result = CSharpFormatter.Format(code, Godot);

        result
            .Code.Should()
            .Be(
                """
                class C
                {
                    void Callee() { }

                    void Caller()
                    {
                #if DEBUG
                        Callee();
                #endif
                    }
                }

                """
            );
        CSharpFormatter.Format(result.Code, Godot).Code.Should().Be(result.Code);
    }

    [Test]
    public void Ignored_Type_Is_Left_Alone()
    {
        var code = """
            // csharpier-ignore
            class C
            {
                void Method() { }
                const int Constant = 1;
            }

            """;

        var result = CSharpFormatter.Format(code, Godot);

        result.Code.Should().Be(code);
        result.ReorderedMembers.Should().BeFalse();
    }
}
