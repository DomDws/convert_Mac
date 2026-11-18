namespace Convert.Core;

/// <summary>Fonts: between desktop fonts (.ttf, .otf, and .ttc/.otc collections) and web fonts (WOFF, WOFF2).</summary>
public static class FontConv
{
    /// <summary>Converts a font file. A collection becomes a folder with one file per font, named after the fonts.</summary>
    public static List<string> Convert(string input, Target t, Runner runner)
    {
        runner.CheckCancelled();
        throw new ConvertException("Fonts can't be converted yet");
    }
}
