namespace CSharpier.Tests.FormattingTests;

public class CSharpGodotFormatting : BaseTest
{
    [DynamicTestBuilder]
    public void BuildTests(DynamicTestBuilderContext context)
    {
        this.BuildTests<CSharpGodotFormatting>(context, "cs_godot");
    }
}
