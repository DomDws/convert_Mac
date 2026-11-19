using UglyToad.PdfPig.Exceptions;

namespace Convert.Core;

/// <summary>
/// PDF → Word, OpenDocument or Rich Text: the text, rebuilt into paragraphs (see <see cref="PdfText"/>),
/// with its fonts, sizes and colours. No images, tables or page layout ("text only").
/// </summary>
public static class PdfDocConv
{
    public static string Convert(string input, Target t, Runner runner, Action<double> progress)
    {
        runner.CheckCancelled();
        UglyToad.PdfPig.PdfDocument doc;
        try { doc = UglyToad.PdfPig.PdfDocument.Open(input); }
        catch (PdfDocumentEncryptedException) { throw new ConvertException("This PDF is password-protected"); }
        catch (Exception) { throw new ConvertException("Couldn't open this PDF"); }

        using (doc)
        {
            var paragraphs = new List<TextParagraph>();
            var count = doc.NumberOfPages;
            for (var i = 0; i < count; i++)
            {
                runner.CheckCancelled();
                try
                {
                    paragraphs.AddRange(PdfText.Paragraphs(doc.GetPage(i + 1)));
                }
                catch (PdfDocumentEncryptedException) { throw new ConvertException("This PDF is password-protected"); }
                catch (Exception e) when (e is not (OperationCanceledException or ConvertException or OutOfMemoryException))
                {
                    // PdfPig reads a page only when asked for it, so a damaged one shows up here.
                    throw new ConvertException($"Couldn't read page {i + 1} of this PDF");
                }
                progress((i + 1.0) / count);
            }
            if (!paragraphs.Any(p => p.Text.Any(c => !char.IsWhiteSpace(c))))
                throw new ConvertException("This PDF contains no extractable text");

            runner.CheckCancelled();
            var out_ = Paths.UniqueOutput(input, t.Ext);
            // Stopped or failed: no unwanted or half-written file stays behind (WriteNew removes it).
            Paths.WriteNew(out_, file =>
            {
                DocWriter.Write(file, t.Ext, paragraphs);
                runner.CheckCancelled();
            });
            return out_;
        }
    }
}
