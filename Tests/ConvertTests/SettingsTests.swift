import Foundation
import Testing

@testable import Convert

/// Saved settings must survive an app update that adds a setting, and the CLI must accept partial overrides.
struct SettingsTests {
    private func decode(_ json: String) throws -> ConvSettings {
        try JSONDecoder().decode(ConvSettings.self, from: Data(json.utf8))
    }

    @Test func roundTripKeepsEveryValue() throws {
        var s = ConvSettings()
        s.audioBitrate = 192
        s.sampleRate = 48000
        s.channels = 1
        s.bitDepth = 24
        s.videoQuality = 0.3
        s.videoHeight = 720
        s.fps = 30
        s.keepAudio = false
        s.hardware = false
        s.gifWidth = 640
        s.gifFps = 20
        s.imageQuality = 0.5
        s.maxSize = 2048
        s.paperA4 = false
        s.pdfDPI = 300
        let decoded = try JSONDecoder().decode(ConvSettings.self, from: JSONEncoder().encode(s))
        #expect(decoded == s)
    }

    /// What an older version saved: every key but one. The missing one gets its default, the rest are kept.
    @Test func settingsSavedBeforeAFieldExistedStillLoad() throws {
        var saved = ConvSettings()
        saved.audioBitrate = 128
        saved.paperA4 = false
        saved.imageQuality = 0.4
        let data = try JSONEncoder().encode(saved)
        var object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        for missing in object.keys.sorted() {
            var older = object
            older.removeValue(forKey: missing)
            let decoded = try JSONDecoder().decode(
                ConvSettings.self, from: JSONSerialization.data(withJSONObject: older))
            let expectedData = try JSONEncoder().encode(decoded)
            var expected = try #require(JSONSerialization.jsonObject(with: expectedData) as? [String: Any])
            // Every other key keeps the saved value…
            expected.removeValue(forKey: missing)
            #expect(NSDictionary(dictionary: expected) == NSDictionary(dictionary: older), "without \(missing)")
        }
        // …and the missing key falls back to its default.
        object.removeValue(forKey: "paperA4")
        let decoded = try JSONDecoder().decode(ConvSettings.self, from: JSONSerialization.data(withJSONObject: object))
        #expect(decoded.paperA4 == ConvSettings().paperA4)
        #expect(decoded.audioBitrate == 128)
        #expect(decoded.imageQuality == 0.4)
    }

    @Test func partialObjectKeepsGivenValuesAndDefaultsTheRest() throws {
        var expected = ConvSettings()
        expected.bitDepth = 24
        #expect(try decode(#"{"bitDepth": 24}"#) == expected)
        #expect(try decode("{}") == ConvSettings())
    }

    @Test func unknownKeysAreIgnored() throws {
        var expected = ConvSettings()
        expected.hardware = false
        #expect(try decode(#"{"hardware": false, "removedInALaterVersion": 7}"#) == expected)
    }

    @Test func aValueOfTheWrongTypeIsAnError() {
        #expect(throws: (any Error).self) { try decode(#"{"bitDepth": "deep"}"#) }
    }

    // MARK: CONVERT_SETTINGS (the CLI)

    @Test func cliOverridesOnlyTheNamedSettings() throws {
        var expected = ConvSettings()
        expected.bitDepth = 16
        expected.hardware = false
        #expect(try settingsWithOverrides(#"{"bitDepth": 16, "hardware": false}"#) == expected)
    }

    @Test func cliRejectsInvalidJSONAndUnknownNames() {
        #expect(throws: (any Error).self) { try settingsWithOverrides("not json") }
        #expect(throws: (any Error).self) { try settingsWithOverrides("[1, 2]") }
        // A typo shouldn't silently convert with the defaults.
        #expect(throws: (any Error).self) { try settingsWithOverrides(#"{"bitdepth": 16}"#) }
    }
}
