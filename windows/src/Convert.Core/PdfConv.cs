using ImageMagick;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Convert.Core;

public static class PdfConv
{
    public static async Task<List<string>> ConvertAsync(string input, Target t, ConvSettings s, Runner runner, Action<double> progress)
    {
        runner.CheckCancelled();
        if (t.Id == "pdf-txt") return [await Task.Run(() => ToText(input, runner))];
        if (t.IsPdfText) return [await Task.Run(() => PdfDocConv.Convert(input, t, runner, progress))];

        WinPdf.PdfDocument doc;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
            doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x8007052B))   // ERROR_WRONG_PASSWORD
        {
            throw new ConvertException("This PDF is password-protected");
        }
        catch (Exception)
        {
            throw new ConvertException("Couldn't open this PDF");
        }

        var count = (int)doc.PageCount;
        if (count == 0) throw new ConvertException("This PDF has no pages");

        // One page → file next to the PDF. Several → a folder with page 1, page 2, …
        string? folder = null;
        if (count > 1)
        {
            folder = Paths.UniqueFolder(input, " pages");
            Directory.CreateDirectory(folder);
        }
        var single = folder == null ? Paths.UniqueOutput(input, t.Ext) : null;
        var scale = s.PdfDpi / 96.0;   // PdfPage.Size is in DIPs (1/96 inch)

        // Stop takes effect between pages. Stopped or failed partway, the pages written so far are
        // removed again: half a folder of pages would look like a finished conversion.
        var made = folder ?? single!;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                runner.CheckCancelled();
                using var page = doc.GetPage(i);
                var w = (uint)Math.Round(page.Size.Width * scale);
                var h = (uint)Math.Round(page.Size.Height * scale);
                if (w == 0 || h == 0) throw new ConvertException($"Page {i + 1} has no size");
                using var stream = new InMemoryRandomAccessStream();
                try
                {
                    await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions
                    {
                        DestinationWidth = w,
                        DestinationHeight = h,
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                        BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                    });
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new ConvertException($"Couldn't render page {i + 1}");
                }
                var out_ = single ?? Path.Combine(folder!, $"page {i + 1}.{t.Ext}");
                stream.Seek(0);
                await using (var src = stream.AsStreamForRead())
                {
                    try
                    {
                        using var img = new MagickImage(src, MagickFormat.Png);
                        // Windows.Data.Pdf scales the destination size by the display scaling factor; undo that so
                        // the chosen dpi means the same on every machine.
                        if (img.Width != w || img.Height != h)
                            img.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
                        img.Density = new Density(s.PdfDpi, s.PdfDpi, DensityUnit.PixelsPerInch);
                        if (t.Id == "pdf-jpg") img.Quality = (uint)Math.Round(Math.Clamp(s.ImageQuality, 0.05, 1) * 100);
                        await Paths.WriteNewAsync(out_, file => img.WriteAsync(file, t.Id == "pdf-jpg" ? MagickFormat.Jpeg : MagickFormat.Png));
                    }
                    catch (MagickException)
                    {
                        throw new ConvertException($"Couldn't write page {i + 1}");
                    }
                }
                progress((i + 1.0) / count);
            }
            runner.CheckCancelled();
        }
        catch
        {
            // A single page's file removes itself (see Paths.WriteNew); the folder is ours to remove.
            Paths.TryDelete(folder);
            throw;
        }
        return [made];
    }

    public static string ToText(string input, Runner runner)
    {
        UglyToad.PdfPig.PdfDocument doc;
        try { doc = UglyToad.PdfPig.PdfDocument.Open(input); }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) { throw new ConvertException("This PDF is password-protected"); }
        catch (Exception) { throw new ConvertException("Couldn't open this PDF"); }

        using (doc)
        {
            var pages = new List<string>();
            foreach (var page in doc.GetPages())
            {
                runner.CheckCancelled();
                pages.Add(ContentOrderTextExtractor.GetText(page));
            }
            var text = string.Join("\n\n", pages).Trim();
            if (text.Length == 0) throw new ConvertException("This PDF contains no extractable text");
            runner.CheckCancelled();
            var out_ = Paths.UniqueOutput(input, "txt");
            Paths.WriteNew(out_, file =>
            {
                using var writer = new StreamWriter(file);
                writer.Write(text + "\n");
                runner.CheckCancelled();
            });
            return out_;
        }
    }
}
