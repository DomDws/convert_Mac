# Contributing to Convert

Thanks for wanting to help. Convert is a small Mac app that a handful of people work on in their spare time, so this guide is short.

By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to help

- **Something broke.** [Open a bug report](../../issues/new/choose). Say what you converted (from which format to which), what happened, and which versions of Convert and macOS you use. If the file isn't private, attach it: a file that fails is the fastest way to a fix.
- **You miss a format.** [Open a format request](../../issues/new/choose) and say what you'd use it for.
- **You want to fix or add something.** Read on.
- **You found a security problem.** Don't open a public issue; see the [security policy](SECURITY.md).

## Before you write code

Small fixes (a bug, a typo, a wrong ffmpeg option) can go straight to a pull request.

For anything bigger, such as a new kind of file, a new engine, a change to how the window works or support for another platform, please open an issue first and describe what you have in mind. It saves you from building something that then can't be merged.

A few things that hold for every change:

- **Originals are never touched.** Convert writes a new file next to the original and never overwrites anything. A half-finished output is removed when a conversion fails or is stopped.
- **Nothing leaves the Mac.** No network code, no analytics, no accounts.
- **No dependencies.** The app uses what macOS ships with, plus `ffmpeg` and `cwebp` if the user has installed them. Please don't add Swift packages or bundle tools.
- **Keep it readable.** The code is small enough to read in an evening, and it should stay that way. Comments say why, not what.

## Setting up

You need a Mac with macOS 14 or later and Xcode 26.6 (or its Command Line Tools). CI uses that version, and its `swift-format` decides the formatting.

```bash
git clone https://github.com/newtonstudios/convert.git
cd convert
brew install ffmpeg webp      # needed for audio, video and WebP, and for the full smoke test
./build.sh --install          # builds the app and puts it in ~/Applications
```

The README has [a map of the source files](README.md#how-it-works-for-the-curious) and explains the command-line mode (`--cli`), which is handy for trying a conversion without clicking through the window.

## Checks

One script runs everything CI runs:

```bash
scripts/check.sh                 # all of it
scripts/check.sh format --fix    # reformat the code
scripts/check.sh build           # build and unit tests (warnings are errors)
scripts/check.sh smoke           # convert sample files to every format the app offers
```

Please run `scripts/check.sh` before you open a pull request.

- **Tests** live in `Tests/ConvertTests` and use Swift Testing (`@Test`, `#expect`). A bug fix should come with a test that fails without it, where that's possible.
- **The smoke test** converts every sample file to every format offered for it, so a new output format is covered automatically. A new *kind of input* needs a sample added in `scripts/smoke-test.sh`.
- **If formats change,** update the tables in the README too.

## Pull requests

- One topic per pull request. Two unrelated fixes are easier to review as two pull requests.
- Say what changed, why, and how you tested it. Link the issue it belongs to (`Fixes #12`).
- Try the change in the real app, not only in the tests, when it affects what people see.
- Code written with an AI assistant is welcome (the app started that way). You are still the author: read what you send, run the checks, and be ready to answer questions about it.

Reviews can take a few days. If a pull request has been quiet for a week, a friendly ping is fine.

## License

Convert is released under the [MIT License](LICENSE). By contributing, you agree that your contribution is released under the same license.
