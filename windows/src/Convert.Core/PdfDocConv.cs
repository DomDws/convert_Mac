namespace Convert.Core;

/// <summary>PDF → Word, OpenDocument or Rich Text: the text, rebuilt into paragraphs. No images, tables or page layout.</summary>
public static class PdfDocConv
{
    public static string Convert(string input, Target t, Runner runner, Action<double> progress)
    {
        runner.CheckCancelled();
        throw new ConvertException($"PDFs can't be converted to {t.Label} yet");
    }
}
