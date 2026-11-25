using LectureSmith.Services;
using Xunit;

namespace LectureSmith.Tests;

public class MathFormatterTests
{
    [Fact]
    public void FormatFormula_HandlesNestedBracesInFractionsAndRoots()
    {
        var formatted = MathFormatter.FormatFormula(@"\frac{x^{2} + 1}{\sqrt{\alpha + \beta}}", useMarkdownEmphasis: false);
        Assert.Equal("(x² + 1 / √(α + β))", formatted);
    }

    [Fact]
    public void ReplaceInlineAndDisplayMath_PreservesCurrencyAndFormatsMath()
    {
        var input = "The textbook costs $50 and saves $20, while $x_i + y^2 = \\alpha$ holds and $1 + 2 = 3$.";
        var output = MathFormatter.ReplaceInlineAndDisplayMath(input, useMarkdownEmphasis: false);

        Assert.Contains("$50 and saves $20", output);
        Assert.Contains("xᵢ + y² = α", output);
        Assert.Contains("1 + 2 = 3", output);
    }
}
