# Building ORB-IX from source

Three projects, no third-party packages beyond one NuGet reference, and the platform assemblies
come from **your own Quantower install** — nothing vendor-owned is redistributed here.

---

## What you need

- **.NET SDK 10.0** or later. `dotnet --version` to check.
- **Quantower**, installed. The build reads `TradingPlatform.BusinessLayer.dll` from it.

---

## Build

```bash
dotnet build src/OrbIx.Quantower.Indicator/OrbIx.Quantower.Indicator.csproj -c Release \
  -p:Share=true \
  -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\<version>\bin\TradingPlatform.BusinessLayer.dll"
```

Point `QuantowerSdkPath` at the `TradingPlatform.BusinessLayer.dll` inside your own Quantower
folder. `<version>` is whatever directory you find under `C:\Quantower\TradingPlatform` — it is
a version number and it moves when Quantower updates, so it is left for you to fill in rather
than written down here and quietly going stale.

`-p:Share=true` matters. It does two things: embeds the neutral configuration
(`orbix.share.json`) rather than expecting one beside the assembly, and strips the build path
out of the produced DLL so it does not carry your machine's directory layout.

The output is `OrbIxIndicator.dll`. Copy it to:

```
C:\Quantower\Settings\Scripts\Indicators\ORB-IX\OrbIxIndicator.dll
```

Close Quantower before copying. It loads scripts at start-up and keeps what it loaded, so a
copy over a running platform appears to succeed and changes nothing.

---

## The three projects

| project | what it is |
|---|---|
| `OrbIx.Core` | Every rule and measurement. **No platform types at all** — deliberately, so the rules can be tested without a chart. |
| `OrbIx.Quantower.Shared` | The small bridge: chart period, instrument identity. |
| `OrbIx.Quantower.Indicator` | The indicator itself and its overlays. Targets `net10.0-windows`. |

`OrbIx.Core` carrying no platform reference is the load-bearing decision in the layout. It is
why a rule like "which aggregations are time bars" lives in Core as a *name* comparison rather
than a type check — a rule that could only run inside the chart shell is a rule that cannot be
tested.

---

## Configuration

`orbix.share.json` is the whole document, and it is **embedded** under `-p:Share=true`. There is
no file to place and no folder to create; every setting is reachable as a chart input from the
indicator's settings panel.

If you want to change a default rather than a chart input, edit the JSON and rebuild. The loader
validates the whole document on load and refuses it with a list of what is wrong, rather than
starting with half a configuration.

---

## A note on the comments

The source is heavily commented, and the comments are the point. Most of them record **why** a
thing is the way it is, and a good number record a measurement that contradicted the obvious
approach. Where a comment says a number was measured, it was measured; where it says something
is unverified, it is.

Attributions have been rewritten — which data vendor, which broker, which prop firm — because
those name third parties and are nobody's business but the author's. The reasoning and the
numbers are untouched.

---

## Tests

Not included here. The suite is around 1,900 tests and leans on a committed fixture of real
captured market data, which is large and not the author's to hand on.

Two things worth knowing about how the code is checked, because they shape how it is written:

- **Rules are tested by calling them.** Anything that decides something lives in `OrbIx.Core`
  for that reason.
- **Mutation testing is the gate.** A test that passes when the behaviour it names is deleted
  is not a test, and several in this codebase were rewritten after a mutation survived them.
