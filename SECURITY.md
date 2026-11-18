# Security Policy

## Supported versions

Only the [latest release](https://github.com/newtonstudios/convert/releases/latest) gets fixes. If you're on an older version, please update first and check whether the problem is still there.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.**

Report it privately instead: go to the repository's **Security** tab and click **Report a vulnerability**, or use this link:

https://github.com/newtonstudios/convert/security/advisories/new

Only the maintainers can read what you send there. It helps to include:

- the version of Convert and of macOS
- what you did, step by step
- the file that triggers the problem, if there is one and it isn't private
- what you think someone could do with it

Convert is maintained in spare time, so a reply can take a few days. You'll be told whether the report is accepted, and you'll be credited in the release notes when it's fixed, unless you'd rather not be.

## What counts

Convert opens files that may come from anywhere, so the interesting problems are the ones a file can cause. For example, a file that makes Convert:

- write, overwrite or delete something outside the folder it's meant to write to
- run a command or a program it shouldn't
- crash or use up all memory or disk space
- send anything over the network (Convert is meant to have no network code at all)

Problems in the Finder Quick Action or in the helper process count too.

## What doesn't

- Bugs in `ffmpeg`, `cwebp` or `brotli`. Convert doesn't ship them; you install them yourself. Please report those to their own projects. (If Convert *calls* one of them in an unsafe way, that is a Convert problem and belongs here.)
- The downloaded app not being notarized by Apple. That's a known limitation, described in the README.
- A conversion that simply gives a wrong or ugly result. That's a normal bug: [open an issue](https://github.com/newtonstudios/convert/issues/new/choose).
