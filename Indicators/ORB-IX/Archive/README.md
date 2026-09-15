# Archive

Superseded pre-built binaries, kept for historical reference only. Neither is used by
anything - the real, current build comes from `../src/` (see `../BUILD.md`), and the DLL
actually deployed to Quantower is built from that source, not from either of these.

- **`OrbIxIndicator-original-binary-drop.dll`** - the very first thing shared: just this
  DLL plus a `.deps.json` and a `README.md`, no source at all.
- **`OrbIxIndicator-TFinch-bundled-prebuilt.dll`** - a second pre-built copy that arrived
  bundled alongside the full source (in a folder originally named after the person who sent
  it, since merged into this `ORB-IX/` folder proper).

Both are superseded by a clean, reproducible `dotnet build` from `../src/` - see `../BUILD.md`.
Safe to delete outright if the history isn't worth keeping; kept here instead of discarded
per the user's call to archive rather than lose them.
