using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Convert.Core.Tests;

/// <summary>
/// One line of text at <see cref="X"/>, <see cref="Y"/>, measured from the bottom left of the page as
/// it's shown. Stretched to <see cref="Width"/> when given, as the full lines of a justified paragraph are.
/// </summary>
internal sealed record PdfLine(string Text, double Y, double X = 72, double Size = 12, string Font = "Helvetica")
{
    public (double R, double G, double B)? Color { get; init; }
    public double? Width { get; init; }
    /// <summary>
    /// Drawn the way Arabic or Hebrew is: letter by letter in reading order, each left of the one
    /// before. (Latin letters stand in for them: the standard fonts have none.)
    /// </summary>
    public bool RightToLeft { get; init; }
}

/// <summary>
/// Writes small PDFs by hand, with the standard 14 fonts (which need no font files), so the tests
/// need neither WebView2 nor LibreOffice and nothing binary is committed.
/// </summary>
internal static class PdfMaker
{
    /// <summary>
    /// Pages of 612 × 792 points with these lines, each with a filled square that isn't text.
    /// <paramref name="rotate"/> 90 makes landscape pages the way scanners and printers store them:
    /// upright paper turned with /Rotate, its text drawn sideways.
    /// </summary>
    public static byte[] Make(IEnumerable<IReadOnlyList<PdfLine>> pages, int rotate = 0, bool image = false, string? password = null)
    {
        if (rotate is not (0 or 90)) throw new ArgumentException("only 0 and 90 degrees", nameof(rotate));
        var pageList = pages.ToList();
        var fonts = pageList.SelectMany(p => p).Select(l => l.Font).Append("Helvetica").Distinct().ToList();

        // Objects: 1 catalog, 2 page tree, then the fonts, the image, and a page + its content per page.
        var objects = new List<byte[]?> { null, null };
        int Add(byte[]? body)
        {
            objects.Add(body);
            return objects.Count;
        }
        var fontIds = fonts.ToDictionary(f => f, f => Add(Ascii($"<< /Type /Font /Subtype /Type1 /BaseFont /{f} /Encoding /WinAnsiEncoding >>")));
        var imageId = image ? Add(null) : 0;   // A stream: written below, once its number is known for encryption.
        var fontRes = string.Join(" ", fonts.Select((f, i) => $"/F{i + 1} {fontIds[f]} 0 R"));
        var xobj = image ? $" /XObject << /Im1 {imageId} 0 R >>" : "";
        var pageIds = new List<int>();
        var contents = new List<(int id, byte[] data)>();
        foreach (var lines in pageList)
        {
            var content = Content(lines, fonts, rotate, image);
            var contentId = Add(null);
            contents.Add((contentId, content));
            pageIds.Add(Add(Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]{(rotate != 0 ? $" /Rotate {rotate}" : "")} " +
                $"/Resources << /Font << {fontRes} >>{xobj} >> /Contents {contentId} 0 R >>")));
        }
        objects[0] = Ascii("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>");

        var security = password == null ? null : new Rc4Security(password);
        byte[] Stream(int id, string dict, byte[] data)
        {
            if (security != null) data = security.Encrypt(id, data);
            return [.. Ascii($"<< {dict}/Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")];
        }
        foreach (var (id, data) in contents) objects[id - 1] = Stream(id, "", data);
        if (image)
        {
            // A 2 × 2 picture: what a scanned page is to a PDF, only smaller.
            byte[] pixels = [40, 40, 40, 200, 200, 200, 200, 200, 200, 40, 40, 40];
            objects[imageId - 1] = Stream(imageId, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 ", pixels);
        }

        var pdf = new MemoryStream();
        void Write(byte[] b) => pdf.Write(b);
        Write(Ascii("%PDF-1.4\n%âãÏÓ\n", latin1: true));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write(Ascii($"{i + 1} 0 obj\n"));
            Write(objects[i]!);
            Write(Ascii("\nendobj\n"));
        }
        var xref = pdf.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) table.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        var encrypt = security == null ? "" : $" /Encrypt {security.Dictionary} /ID [<{Rc4Security.Hex(Rc4Security.Id)}> <{Rc4Security.Hex(Rc4Security.Id)}>]";
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{encrypt} >>\nstartxref\n{xref}\n%%EOF\n");
        Write(Ascii(table.ToString()));
        return pdf.ToArray();
    }

    private static byte[] Content(IReadOnlyList<PdfLine> lines, List<string> fonts, int rotate, bool image)
    {
        var s = new StringBuilder();
        s.Append("q 0 0 0 rg 10 10 20 20 re f Q\n");   // Something that isn't text.
        if (image) s.Append("q 400 0 0 500 100 150 cm /Im1 Do Q\n");
        foreach (var l in lines)
        {
            var (r, g, b) = l.Color ?? (0, 0, 0);
            var spaces = l.Text.Count(c => c == ' ');
            var tw = l.Width is { } w && spaces > 0 ? (w - Width(l.Text, l.Size, l.Font)) / spaces : 0;
            // On a page turned by 90°, a line read left to right runs up the unturned paper.
            var tm = rotate == 90 ? $"0 1 -1 0 {N(612 - l.Y)} {N(l.X)}" : $"1 0 0 1 {N(l.X)} {N(l.Y)}";
            if (l.RightToLeft)
            {
                var x = l.X + Width(l.Text, l.Size);
                foreach (var ch in l.Text)
                {
                    x -= Width(ch.ToString(), l.Size);
                    s.Append(CultureInfo.InvariantCulture, $"BT /F{fonts.IndexOf(l.Font) + 1} {N(l.Size)} Tf 1 0 0 1 {N(x)} {N(l.Y)} Tm ({Escape(ch.ToString())}) Tj ET\n");
                }
                continue;
            }
            s.Append(CultureInfo.InvariantCulture,
                $"BT /F{fonts.IndexOf(l.Font) + 1} {N(l.Size)} Tf {N(r)} {N(g)} {N(b)} rg {N(tw)} Tw {tm} Tm ({Escape(l.Text)}) Tj ET\n");
        }
        return Ascii(s.ToString(), latin1: true);
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The text in WinAnsi, as a PDF string literal's content.</summary>
    private static string Escape(string text)
    {
        var s = new StringBuilder();
        foreach (var c in text)
        {
            var code = WinAnsi(c);
            if (code is '(' or ')' or '\\') s.Append('\\').Append((char)code);
            else if (code < 32 || code > 126) s.Append('\\').Append(System.Convert.ToString(code, 8).PadLeft(3, '0'));
            else s.Append((char)code);
        }
        return s.ToString();
    }

    private static int WinAnsi(char c) => c switch
    {
        '•' => 0x95,
        '–' => 0x96,
        '—' => 0x97,
        '…' => 0x85,
        < (char)0x100 => c,
        _ => throw new ArgumentException($"'{c}' isn't in WinAnsiEncoding"),
    };

    private static byte[] Ascii(string s, bool latin1 = false) => latin1 ? Encoding.Latin1.GetBytes(s) : Encoding.ASCII.GetBytes(s);

    /// <summary>Width of the text in points: Helvetica's metrics (Adobe's AFM), used for any font.</summary>
    public static double Width(string text, double size, string font = "Helvetica") =>
        text.Sum(c => c is >= ' ' and <= '~' ? HelveticaWidths[c - ' '] : c == '•' ? 350 : 556) * size / 1000;

    private static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,   // space to /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,   // 0 to ?
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,  // @ to O
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,   // P to _
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,   // ` to o
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,        // p to ~
    ];

    /// <summary>
    /// The PDF standard security handler, revision 2 (RC4, 40-bit key): the oldest kind, but a real
    /// password-protected file that every reader asks a password for.
    /// </summary>
    private sealed class Rc4Security
    {
        public static readonly byte[] Id = [0x43, 0x6f, 0x6e, 0x76, 0x65, 0x72, 0x74, 0x54, 0x65, 0x73, 0x74, 0x50, 0x44, 0x46, 0x30, 0x31];
        private const int Permissions = -44;   // Print and copy not allowed; it doesn't matter here.
        private static readonly byte[] Padding =
        [
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        ];
        private readonly byte[] key;
        private readonly byte[] owner;
        private readonly byte[] user;

        public Rc4Security(string password)
        {
            var padded = Pad(password);
            owner = Rc4(MD5.HashData(padded)[..5], padded);   // Same owner and user password.
            key = MD5.HashData([.. padded, .. owner, .. BitConverter.GetBytes(Permissions), .. Id])[..5];
            user = Rc4(key, Padding);
        }

        public string Dictionary => $"<< /Filter /Standard /V 1 /R 2 /O <{Hex(owner)}> /U <{Hex(user)}> /P {Permissions} >>";

        public byte[] Encrypt(int objectNumber, byte[] data)
        {
            byte[] salt = [(byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16), 0, 0];
            return Rc4(MD5.HashData([.. key, .. salt])[..10], data);
        }

        public static string Hex(byte[] b) => System.Convert.ToHexString(b);

        private static byte[] Pad(string password) => [.. Encoding.Latin1.GetBytes(password).Take(32), .. Padding.Take(32 - Math.Min(32, password.Length))];

        private static byte[] Rc4(byte[] k, byte[] data)
        {
            var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + k[i % k.Length]) & 255;
                (s[i], s[j]) = (s[j], s[i]);
            }
            var out_ = new byte[data.Length];
            for (int n = 0, i = 0, j = 0; n < data.Length; n++)
            {
                i = (i + 1) & 255;
                j = (j + s[i]) & 255;
                (s[i], s[j]) = (s[j], s[i]);
                out_[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
            }
            return out_;
        }
    }
}
