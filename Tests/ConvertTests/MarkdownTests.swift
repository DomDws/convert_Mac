import Testing

@testable import Convert

/// The Markdown → HTML renderer. Its output is exact and stable, so these compare whole snippets.
struct MarkdownTests {
    private func html(_ lines: String...) -> String {
        Markdown.toHTML(lines.joined(separator: "\n"))
    }

    // MARK: Inline

    @Test func nestedEmphasis() {
        #expect(html("**bold *nested* bold**") == "<p><strong>bold <em>nested</em> bold</strong></p>")
    }

    @Test func underscoresInsideWordsAreNotItalic() {
        #expect(html("snake_case_word and _real italic_") == "<p>snake_case_word and <em>real italic</em></p>")
    }

    @Test func codeSpansProtectTheirContents() {
        #expect(html("`a*b*c` and *x*") == "<p><code>a*b*c</code> and <em>x</em></p>")
    }

    @Test func rawHTMLIsEscaped() {
        #expect(html(#"<b>raw</b> & "quotes""#) == "<p>&lt;b&gt;raw&lt;/b&gt; &amp; &quot;quotes&quot;</p>")
    }

    // MARK: Lists

    @Test func orderedListFromOneHasNoStart() {
        #expect(html("1. one", "2. two") == "<ol>\n<li>one\n</li>\n<li>two\n</li></ol>")
    }

    @Test func orderedListKeepsItsStartNumber() {
        // Like CommonMark, only the first number counts.
        #expect(
            html("3. three", "4. four", "9. nine")
                == "<ol start=\"3\">\n<li>three\n</li>\n<li>four\n</li>\n<li>nine\n</li></ol>")
    }

    @Test func orderedListCanStartAtZero() {
        #expect(html("0. zero") == "<ol start=\"0\">\n<li>zero\n</li></ol>")
    }

    @Test func nestedOrderedListKeepsItsStartNumber() {
        let expected = """
            <ul>
            <li>outer
            <ol start="2">
            <li>inner two
            </li>
            <li>inner three
            </li></ol>
            </li>
            <li>back
            </li></ul>
            """
        #expect(html("- outer", "  2. inner two", "  3. inner three", "- back") == expected)
    }

    @Test func orderedListAfterBulletsKeepsItsStartNumber() {
        #expect(
            html("- bullet", "5) five", "6) six")
                == "<ul>\n<li>bullet\n</li></ul><ol start=\"5\">\n<li>five\n</li>\n<li>six\n</li></ol>")
    }

    @Test func listBetweenParagraphs() {
        #expect(html("Para", "", "1. one", "", "After") == "<p>Para</p>\n<ol>\n<li>one\n</li></ol>\n<p>After</p>")
    }
}
