using LectureSmith.ViewModels;
using Xunit;

namespace LectureSmith.Tests;

public class MarkdownViewerViewModelTests
{
    [Fact]
    public void UpdateMarkdown_ParsesBlocksAndPreservesPrefixReferencesDuringStreaming()
    {
        using var vm = new MarkdownViewerViewModel();

        vm.UpdateMarkdown("""
            # Lecture 1

            ## Slide 1 — Intro

            ![[slides/slide_01.png]]

            First paragraph being streamed
            """);

        Assert.Equal(4, vm.Blocks.Count);
        var headingRef = vm.Blocks[0];
        var slideHeadingRef = vm.Blocks[1];
        var imgBlock = Assert.IsType<SlideImageBlockItem>(vm.Blocks[2]);
        Assert.Equal("slides/slide_01.png", imgBlock.ImageRef);
        var paraRef = Assert.IsType<ParagraphBlockItem>(vm.Blocks[3]);

        // Stream additional text onto the trailing paragraph + add a table & callout
        vm.UpdateMarkdown("""
            # Lecture 1

            ## Slide 1 — Intro

            ![[slides/slide_01.png]]

            First paragraph being streamed with **bold** and $x_1$.

            > [!tip] Exam tip: focus on Big-O.

            | Concept | Description |
            |---|---|
            | Big-O | Upper bound |
            """);

        // Completed prefix blocks MUST keep exact reference identity (zero UI control churn)
        Assert.Same(headingRef, vm.Blocks[0]);
        Assert.Same(slideHeadingRef, vm.Blocks[1]);
        Assert.Same(imgBlock, vm.Blocks[2]);
        Assert.Same(paraRef, vm.Blocks[3]);

        var quote = Assert.IsType<QuoteBlockItem>(vm.Blocks[4]);
        Assert.Equal("💡 Tip", quote.CalloutTitle);

        var table = Assert.IsType<TableBlockItem>(vm.Blocks[5]);
        Assert.Equal(["Concept", "Description"], table.Headers);
        Assert.Single(table.Rows);
        Assert.Equal(["Big-O", "Upper bound"], table.Rows[0]);
    }
}
