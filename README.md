# Convert

Local "CloudConvert" for macOS. Right-click files in Finder → **Quick Actions → Convert…**, pick a format, adjust settings, convert. Output lands next to the original (`name.mp3`, `name 2.mp3`, …).

| Input | Outputs |
|---|---|
| Audio | MP3, M4A (AAC / HE-AAC), WAV, AIFF, FLAC, ALAC, Opus, OGG, WMA, AC3, WavPack, MP2 |
| Video | MP4 (H.264 / HEVC / AV1), MOV (ProRes 422), WebM (VP9), MKV, GIF, plus every audio format (extracts the soundtrack) |
| Images | JPEG, PNG, HEIC, AVIF, WebP, TIFF, GIF, BMP, PDF |
| Documents (DOCX, DOC, ODT, RTF, HTML, TXT, MD) | PDF, DOCX, DOC, ODT, RTF, TXT, HTML |
| PDF | PNG / JPEG per page, plain text |

Settings (bitrate, sample rate, quality, resolution, fps, max size, paper…) are remembered per format.

## Build & install

```bash
./build.sh --install
```

Installs `~/Applications/Convert.app` and the Finder Quick Action (`~/Library/Services/Convert.workflow`).

Needs Xcode (Swift 6) to build. Audio/video use Homebrew `ffmpeg`; WebP uses `cwebp` (`brew install ffmpeg webp`). Images, documents and PDFs use macOS frameworks only.

## Test mode

```bash
build/Convert.app/Contents/MacOS/Convert --cli mp3 song.opus
```

Converts with default settings and prints the result (`--cli` alone lists target IDs).

## Layout

- `Formats.swift` — input detection, output targets, per-format options, settings model
- `FFmpeg.swift` — audio/video argument building and probing
- `ImageConv.swift` — ImageIO image conversion (+ cwebp for WebP)
- `DocConv.swift` — textutil document conversion, document → PDF, PDF → images/text
- `Converter.swift` — dispatch; `ConvertModel.swift` — panel state; `ContentView.swift` — UI
