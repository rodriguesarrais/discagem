using Discagem.Shared.Text;

namespace Discagem.Tests.Shared;

public sealed class MessageMarkupTests
{
    [Fact]
    public void ParsesInlineFormatting()
    {
        var spans = MessageMarkup.ParseInline("a **bold** *it* __under__ ~~gone~~ `code` https://example.com/x?y=1. @alice!");
        var kinds = spans.Where(s => s.Kind != SpanKind.Text).Select(s => (s.Kind, s.Text)).ToList();

        Assert.Equal(
            [
                (SpanKind.Bold, "bold"),
                (SpanKind.Italic, "it"),
                (SpanKind.Underline, "under"),
                (SpanKind.Strikethrough, "gone"),
                (SpanKind.Code, "code"),
                (SpanKind.Link, "https://example.com/x?y=1"),
                (SpanKind.Mention, "@alice"),
            ],
            kinds);
        Assert.Equal("a bold it under gone code https://example.com/x?y=1. @alice!",
            string.Concat(spans.Select(s => s.Text)).Replace("  ", " "));
    }

    [Fact]
    public void LeavesUnbalancedMarkersAlone()
    {
        var spans = MessageMarkup.ParseInline("2 * 3 = 6 and snake_case_name, email bob@example.com");
        Assert.All(spans, s => Assert.Equal(SpanKind.Text, s.Kind));
    }

    [Fact]
    public void ParsesCodeBlocksAndQuotes()
    {
        var blocks = MessageMarkup.Parse("look:\n```csharp\nvar x = 1;\n```\n> quoted **text**\nafter");

        Assert.Collection(blocks,
            b => Assert.Equal("look:", Assert.IsType<ParagraphBlock>(b).Spans.Single().Text),
            b =>
            {
                var code = Assert.IsType<CodeBlock>(b);
                Assert.Equal("csharp", code.Language);
                Assert.Equal("var x = 1;", code.Code);
            },
            b => Assert.Contains(Assert.IsType<QuoteBlock>(b).Spans, s => s.Kind == SpanKind.Bold),
            b => Assert.Equal("after", Assert.IsType<ParagraphBlock>(b).Spans.Single().Text));
    }

    [Fact]
    public void UnterminatedFenceIsPlainText()
    {
        var blocks = MessageMarkup.Parse("```not closed");
        Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
    }

    [Theory]
    [InlineData("hey @Alice", "alice", true)]
    [InlineData("hey @alicia", "alice", false)]
    [InlineData("mail alice@example.com", "alice", false)]
    [InlineData("@everyone meeting now", "bob", true)]
    [InlineData("`@alice` in code", "alice", false)]
    public void DetectsMentions(string content, string username, bool expected) =>
        Assert.Equal(expected, MessageMarkup.Mentions(content, username));

    [Theory]
    [InlineData("🎉", true)]
    [InlineData("👍🏽 👍", true)]
    [InlineData("ok 👍", false)]
    [InlineData("", false)]
    public void DetectsEmojiOnlyMessages(string content, bool expected) =>
        Assert.Equal(expected, MessageMarkup.IsEmojiOnly(content));
}
