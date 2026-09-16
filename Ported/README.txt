OCEAN INDICATOR SUITE — SOURCE BUNDLE
=====================================

18 custom order-flow indicators for ATAS Platform 8.0.14 (C# / .NET 10), used to
trade MNQ intraday. Packaged for porting to Quantower.

START HERE:  PORTING-GUIDE.md
             - what every indicator does and how it is used in real trading
             - the exact platform capability contract (ATAS -> Quantower mapping)
             - the invariants that must survive the port
             - a suggested port order

SOURCE:      src/<project>/
             Each project also carries its own README.md (behaviour) and
             CLAUDE.md (build rules, traps, design decisions). Read the
             CLAUDE.md before changing any behaviour.

NOT INCLUDED: compiled DLLs, bin/, obj/, API keys, cached market data.

58% of this codebase (33,109 of 56,782 lines) has no platform dependency and
ports unchanged. See Appendix A of the guide for the per-file classification.

Nothing in this suite has a demonstrated, costed, out-of-sample edge. Section 8
of the guide is an honest per-project status table. Read it before trading any
of it.

PRIVACY: this bundle has been scrubbed. No names, email, usernames, machine
identifiers, account numbers, API keys or personal trading records. Design
rationale in the CLAUDE.md files is preserved but attributed neutrally.
