using System.Text.Json;
using System.Text.Json.Nodes;
using Convert.Core;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>Saved settings must survive an app update that adds a setting, and the CLI must accept partial overrides.</summary>
public sealed class SettingsTests
{
    private static ConvSettings Decode(string json) => JsonSerializer.Deserialize<ConvSettings>(json, ConvSettings.Json)!;
    private static string Encode(ConvSettings s) => JsonSerializer.Serialize(s, ConvSettings.Json);

    [Fact]
    public void RoundTripKeepsEveryValue()
    {
        var s = new ConvSettings
        {
            AudioBitrate = 192,
            SampleRate = 48000,
            Channels = 1,
            BitDepth = 24,
            VideoQuality = 0.3,
            VideoHeight = 720,
            Fps = 30,
            KeepAudio = false,
            Hardware = false,
            GifWidth = 640,
            GifFps = 20,
            ImageQuality = 0.5,
            MaxSize = 2048,
            PaperA4 = false,
            PdfDpi = 300,
        };
        Assert.Equal(s, Decode(Encode(s)));
    }

    [Fact]
    public void TheNamesAreTheMacs()
    {
        // CONVERT_SETTINGS and the smoke tests use these names on both platforms.
        string[] expected =
        [
            "audioBitrate", "sampleRate", "channels", "bitDepth", "videoQuality", "videoHeight", "fps", "keepAudio", "hardware",
            "gifWidth", "gifFps", "imageQuality", "maxSize", "paperA4", "pdfDPI",
        ];
        Assert.Equal(expected.Order(), JsonNode.Parse(Encode(new ConvSettings()))!.AsObject().Select(p => p.Key).Order());
    }

    /// <summary>What an older version saved: every key but one. The missing one gets its default, the rest are kept.</summary>
    [Fact]
    public void SettingsSavedBeforeAFieldExistedStillLoad()
    {
        var saved = new ConvSettings { AudioBitrate = 128, PaperA4 = false, ImageQuality = 0.4 };
        var all = JsonNode.Parse(Encode(saved))!.AsObject();
        foreach (var missing in all.Select(p => p.Key).ToList())
        {
            var older = JsonNode.Parse(all.ToJsonString())!.AsObject();
            older.Remove(missing);
            var decoded = JsonNode.Parse(Encode(Decode(older.ToJsonString())))!.AsObject();
            // Every other key keeps the saved value…
            foreach (var (key, value) in older) Assert.Equal(value!.ToJsonString(), decoded[key]!.ToJsonString());
            // …and the missing key falls back to its default.
            Assert.Equal(JsonNode.Parse(Encode(new ConvSettings()))![missing]!.ToJsonString(), decoded[missing]!.ToJsonString());
        }
    }

    [Fact]
    public void PartialObjectKeepsGivenValuesAndDefaultsTheRest()
    {
        Assert.Equal(new ConvSettings { BitDepth = 24 }, Decode("""{"bitDepth": 24}"""));
        Assert.Equal(new ConvSettings(), Decode("{}"));
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        Assert.Equal(new ConvSettings { Hardware = false }, Decode("""{"hardware": false, "removedInALaterVersion": 7}"""));
    }

    [Fact]
    public void AValueOfTheWrongTypeIsAnError()
    {
        Assert.Throws<JsonException>(() => Decode("""{"bitDepth": "deep"}"""));
    }

    [Fact]
    public void SettingsSavedByTheFirstWindowsVersionStillLoad()
    {
        // It wrote the property names as they are in the code.
        var old = """{"AudioBitrate": 160, "Hardware": false, "PaperA4": false, "PdfDpi": 300}""";
        Assert.Equal(new ConvSettings { AudioBitrate = 160, Hardware = false, PaperA4 = false, PdfDpi = 300 }, Decode(old));
    }

    // CONVERT_SETTINGS (the CLI)

    [Fact]
    public void CliOverridesOnlyTheNamedSettings()
    {
        Assert.Equal(new ConvSettings { BitDepth = 16, Hardware = false }, ConvSettings.WithOverrides("""{"bitDepth": 16, "hardware": false}"""));
        Assert.Equal(new ConvSettings { PdfDpi = 72 }, ConvSettings.WithOverrides("""{"pdfDPI": 72}"""));
    }

    [Fact]
    public void CliRejectsInvalidJsonAndUnknownNames()
    {
        Assert.Throws<ConvertException>(() => ConvSettings.WithOverrides("not json"));
        Assert.Throws<ConvertException>(() => ConvSettings.WithOverrides("[1, 2]"));
        Assert.Throws<ConvertException>(() => ConvSettings.WithOverrides("""{"bitDepth": "deep"}"""));
        // A typo shouldn't silently convert with the defaults.
        var error = Assert.Throws<ConvertException>(() => ConvSettings.WithOverrides("""{"bitdepth": 16}"""));
        Assert.StartsWith("unknown setting \"bitdepth\"; known: audioBitrate, bitDepth, channels, fps, ", error.Message);
    }
}
