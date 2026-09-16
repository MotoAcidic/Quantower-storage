using System;
using System.Globalization;

namespace OceansCurrent
{
    /// <summary>How a panel row reads. Absent is its own thing and must never collapse into Neutral.</summary>
    public enum RowKind
    {
        Long = 0,
        Short = 1,
        Neutral = 2,

        /// <summary>The factor could not be computed, so it is not voting at all.</summary>
        Absent = 3
    }

    /// <summary>One factor's line: what it is, how much of the vote it carries, which way it points.</summary>
    public struct PanelRow
    {
        public string Name;

        /// <summary>Its share of the vote as an integer percent, or null when it is not voting.</summary>
        public string Weight;

        public string Direction;
        public RowKind Kind;

        /// <summary>The subscore, -100..+100. Only meaningful when <see cref="HasBar"/>.</summary>
        public decimal Value;

        /// <summary>False for an absent factor: it draws no bar, because it made no reading.</summary>
        public bool HasBar;

        /// <summary>Why it is absent, in the words the panel prints. Null when it voted.</summary>
        public string Absent;
    }

    /// <summary>One line of the lower sections: a short label, the text, and what colour it reads as.</summary>
    public struct PanelLine
    {
        public string Label;
        public string Text;
        public RowKind Kind;

        /// <summary>The label takes the colour of <see cref="Kind"/>. False reads in the plain label colour.</summary>
        public bool Coloured;

        /// <summary>Muted: a fact worth showing that is not a call to act (no change, not voting).</summary>
        public bool Muted;

        /// <summary>Warning colour: an event printing now, a feed problem.</summary>
        public bool Warn;
    }

    /// <summary>
    /// The AlphaXtrade "Bias Lite" reading of the engine: a factor per row, its weight, the
    /// direction it points, a bar for how hard, and one bias value underneath.
    ///
    /// The layout is theirs. Two things in it are deliberately not:
    ///
    /// **Absent is a fourth state.** Bias Lite has three -- Long, Short, Neutral -- because a
    /// person fills it in and a person always has an opinion. This engine does not: a factor
    /// with nothing to read abstains and drops out of the weight normalisation. Drawing that as
    /// a grey "Neutral" chip would be the exact lie the rest of the codebase is arranged to
    /// prevent, so it gets its own hollow chip and no bar.
    ///
    /// **The weight column is the effective weight, not the setting.** It is what the factor is
    /// actually worth on this bar after the gamma regime has transformed it and the absent
    /// factors have dropped out, as a share of the vote. Bias Lite shows a constant because
    /// nothing there moves it. Here the regime moves it, and a column that did not show that
    /// would be describing a blend that is not the one being run.
    /// </summary>
    public sealed class PanelModel
    {
        public const string Title = "OCEAN'S CURRENT";

        public PanelRow[] Rows;

        /// <summary>The bias value as AlphaXtrade prints it: signed, a percentage.</summary>
        public string Bias;
        public bool BiasKnown;
        public RowKind BiasKind;

        /// <summary>The forming bar's read, in brackets, or null. Never blended into the value.</summary>
        public string Provisional;

        public string State;
        public RowKind StateKind;
        public string Confidence;
        public string Flip;
        public string Regime;
        public bool RegimeKnown;
        public string Feed;
        public bool FeedWarn;
        public string Notes;

        /// <summary>Set when there is no reading at all, and then Rows is empty.</summary>
        public string Waiting;

        /// <summary>WHAT CHANGES IT: header suffix, then one line per trigger.</summary>
        public string ChangesHeader;
        public System.Collections.Generic.List<PanelLine> Changes = new System.Collections.Generic.List<PanelLine>();

        /// <summary>The next scheduled release, the track record, and the open call.</summary>
        public System.Collections.Generic.List<PanelLine> Context = new System.Collections.Generic.List<PanelLine>();

        private static readonly string[] Names =
        {
            "VWAP",
            "Session Delta",
            "Value Area Shift",
            "Overnight Inventory",
            "Key Levels",
            "One-Timeframing"
        };

        public static string NameOf(int factor) =>
            factor >= 0 && factor < Names.Length ? Names[factor] : "F" + factor;

        public static PanelModel NotReady(string why) =>
            new PanelModel { Waiting = why, Rows = new PanelRow[0], Bias = "-", BiasKnown = false };

        public static PanelModel Build(BiasRecord r, FactorConfig cfg, bool hasProvisional,
                                       decimal provisional, GexSnapshot gex, bool gexPresent,
                                       bool gexStale, int gexAgeSeconds, decimal tick)
        {
            var model = new PanelModel();
            var subs = r.Subs ?? new Subscore[0];
            var count = Math.Max(subs.Length, BiasEngine.FactorCount);

            // The denominator is the same one Blend used: only the factors that may actually
            // vote. Normalising against the settings instead would print shares that do not add
            // up to the number underneath them.
            var total = 0m;
            for (var i = 0; i < subs.Length; i++)
                total += Scoring.WeightOf(i, subs[i], cfg, r.Regime);

            model.Rows = new PanelRow[count];

            for (var i = 0; i < count; i++)
            {
                var sub = i < subs.Length ? subs[i] : Subscore.Missing("not computed");
                var weight = i < subs.Length ? Scoring.WeightOf(i, sub, cfg, r.Regime) : 0m;
                var votes = weight > 0m && sub.Available;

                var row = new PanelRow { Name = NameOf(i) };

                if (!votes)
                {
                    row.Kind = RowKind.Absent;
                    row.Direction = "n/a";
                    row.Weight = "-";
                    row.HasBar = false;
                    row.Absent = sub.Available ? DisabledReason(i, cfg) : sub.Absent;
                }
                else
                {
                    row.Kind = sub.Value > 0m ? RowKind.Long
                             : sub.Value < 0m ? RowKind.Short
                             : RowKind.Neutral;
                    row.Direction = row.Kind == RowKind.Long ? "Long"
                                  : row.Kind == RowKind.Short ? "Short"
                                  : "Neutral";
                    row.Value = sub.Value;
                    row.HasBar = true;
                    row.Weight = total > 0m
                        ? Math.Round(weight / total * 100m, 0, MidpointRounding.AwayFromZero)
                              .ToString("0", CultureInfo.InvariantCulture)
                        : "0";
                }

                model.Rows[i] = row;
            }

            model.BiasKnown = r.ScoreKnown;
            model.Bias = r.ScoreKnown ? Signed(r.Score) : "no vote";
            model.BiasKind = !r.ScoreKnown ? RowKind.Absent
                           : r.State == BiasState.Long ? RowKind.Long
                           : r.State == BiasState.Short ? RowKind.Short
                           : RowKind.Neutral;

            model.Provisional = hasProvisional ? Signed(provisional) : null;

            model.State = BiasEngine.StateText(r.State);
            model.StateKind = r.State == BiasState.Long ? RowKind.Long
                            : r.State == BiasState.Short ? RowKind.Short
                            : RowKind.Neutral;

            model.Confidence = r.ScoreKnown
                ? ((int)r.Confidence).ToString(CultureInfo.InvariantCulture)
                : "-";

            model.Flip = r.Flip.Known ? BadgeModel.Price(r.Flip.Value, tick)
                       : r.State == BiasState.Short ? "none above"
                       : r.State == BiasState.Long ? "none below"
                       : "-";

            model.Regime = RegimeText(r, gex, gexPresent, gexStale, tick);
            model.RegimeKnown = gexPresent && !gexStale && r.Regime != RegimeMode.None;

            model.Feed = FeedText(gexPresent, gexStale, gexAgeSeconds, gex);
            model.FeedWarn = !gexPresent || gexStale || (gex != null && gex.Problem != null);

            model.Notes = string.IsNullOrEmpty(r.Notes) ? null : r.Notes;

            return model;
        }

        /// <summary>
        /// Writes WHAT CHANGES IT from a trigger report. Every line names the state it would go
        /// TO, coloured as that state, because "a close below 29,095" means nothing until you
        /// know whether it ends a long or starts a short.
        /// </summary>
        public void AddTriggers(TriggerReport t, decimal tick)
        {
            Changes.Clear();

            if (t == null)
            {
                ChangesHeader = "working it out";
                return;
            }

            if (t.Problem != null)
            {
                ChangesHeader = t.Problem;
                return;
            }

            ChangesHeader = t.DwellLeft > 0
                ? "earliest in " + t.DwellLeft + (t.DwellLeft == 1 ? " bar" : " bars")
                : "on the next close";

            if (t.Here.Found)
            {
                Changes.Add(new PanelLine
                {
                    Label = To(t.Here.To),
                    Text = "if this bar closes here",
                    Kind = KindOf(t.Here.To),
                    Coloured = true
                });
            }
            else
            {
                Changes.Add(PriceLine(t.Above, "above", t, tick));
                Changes.Add(PriceLine(t.Below, "below", t, tick));
            }

            // The outright reversal, where it adds something: from LONG, SHORT is further away
            // than the NEUTRAL the next close reaches, and that distance is the point.
            if (t.HoldBelow.Found && (!t.Below.Found || t.Below.To != t.HoldBelow.To))
                Changes.Add(new PanelLine
                {
                    Label = To(t.HoldBelow.To),
                    Text = "holds below " + BadgeModel.Price(t.HoldBelow.Price, tick) +
                           " (" + Pts(t.HoldBelow.Price - t.Close) + ")",
                    Kind = KindOf(t.HoldBelow.To),
                    Coloured = true
                });

            if (t.HoldAbove.Found && (!t.Above.Found || t.Above.To != t.HoldAbove.To))
                Changes.Add(new PanelLine
                {
                    Label = To(t.HoldAbove.To),
                    Text = "holds above " + BadgeModel.Price(t.HoldAbove.Price, tick) +
                           " (" + Pts(t.HoldAbove.Price - t.Close) + ")",
                    Kind = KindOf(t.HoldAbove.To),
                    Coloured = true
                });

            if (!t.DeltaVoting)
            {
                Changes.Add(new PanelLine { Label = "DELTA", Text = "not voting yet", Muted = true });
            }
            else
            {
                Changes.Add(DeltaLine(t.BuyDelta, "buying"));
                Changes.Add(DeltaLine(t.SellDelta, "selling"));
            }

            Changes.Add(SweepLine(t.SweepHigh, t.State, tick));
            Changes.Add(SweepLine(t.SweepLow, t.State, tick));

            if (t.GammaFlip.Known)
            {
                var above = t.Close > t.GammaFlip.Value;
                Changes.Add(new PanelLine
                {
                    Label = "GAMMA",
                    Text = "flip " + BadgeModel.Price(t.GammaFlip.Value, tick) + " (" +
                           Pts(t.GammaFlip.Value - t.Close) + ") " +
                           (above ? "above: damped" : "below: moves run"),
                    Muted = true
                });
            }

            // Drop the sweep lines that had nothing to report.
            Changes.RemoveAll(l => l.Text == null);
        }

        private static PanelLine PriceLine(Trigger tr, string side, TriggerReport t, decimal tick)
        {
            if (!tr.Found)
                return new PanelLine
                {
                    Label = "-",
                    Text = "nothing " + side + " within " + Pts(t.Range).TrimStart('+') + " pts",
                    Muted = true
                };

            return new PanelLine
            {
                Label = To(tr.To),
                Text = "close " + side + " " + BadgeModel.Price(tr.Price, tick) +
                       " (" + Pts(tr.Price - t.Close) + ")",
                Kind = KindOf(tr.To),
                Coloured = true
            };
        }

        private static PanelLine DeltaLine(Trigger tr, string side)
        {
            if (!tr.Found)
                return new PanelLine
                {
                    Label = "-",
                    Text = "no one bar of " + side + " does it",
                    Muted = true
                };

            return new PanelLine
            {
                Label = To(tr.To),
                Text = "one bar of " + Contracts(tr.Delta) + " delta",
                Kind = KindOf(tr.To),
                Coloured = true
            };
        }

        private static PanelLine SweepLine(Trigger tr, BiasState now, decimal tick)
        {
            if (!tr.Found) return new PanelLine { Text = null };

            var changes = tr.To != now;
            return new PanelLine
            {
                Label = changes ? To(tr.To) : "-",
                Text = tr.Reference + " " + BadgeModel.Price(tr.Price, tick) + " swept+reclaimed" +
                       (changes ? "" : ": no change"),
                Kind = changes ? KindOf(tr.To) : RowKind.Neutral,
                Coloured = changes,
                Muted = !changes
            };
        }

        /// <summary>
        /// The next scheduled release, in Houston time. Inside the window around a print it turns
        /// to a warning: every level above was computed from a market that has not seen it yet.
        /// </summary>
        public void AddEvent(MacroEvent next, string problem, DateTime nowUtc, TimeZoneInfo zone,
                             bool haveFeed)
        {
            if (next == null)
            {
                Context.Add(new PanelLine
                {
                    Label = "NEXT",
                    Text = !haveFeed ? (problem ?? "calendar loading") : "nothing more in this week's feed",
                    Muted = true,
                    Warn = !haveFeed && problem != null
                });
                return;
            }

            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(next.WhenUtc, DateTimeKind.Utc), zone);
            var until = next.WhenUtc - nowUtc;
            var title = next.Title + (next.AlsoAtSameTime > 0 ? " +" + next.AlsoAtSameTime : "");

            string when;
            var warn = false;

            if (until <= TimeSpan.Zero)
            {
                when = "printed " + Span(-until) + " ago, levels predate it";
                warn = true;
            }
            else
            {
                when = local.ToString("ddd HH:mm", CultureInfo.InvariantCulture) + " CT (" + Span(until) + ")";
                warn = until <= TimeSpan.FromMinutes(30);
            }

            Context.Add(new PanelLine { Label = "NEXT", Text = title + "  " + when, Warn = warn });
        }

        /// <summary>
        /// How the calls on this chart played out -- and, below thirty of them, a plain statement
        /// that the number cannot be trusted yet.
        /// </summary>
        public void AddTrack(Scorecard card)
        {
            if (card == null) return;

            if (card.OpenCall)
            {
                Context.Add(new PanelLine
                {
                    Label = "NOW",
                    Text = BiasEngine.StateText(card.OpenState) + " since " +
                           card.OpenSince.ToString("HH:mm", CultureInfo.InvariantCulture) + " at " +
                           card.OpenEntry.ToString("0.00", CultureInfo.InvariantCulture) + "  " +
                           Pts(card.OpenPoints) + " pts",
                    Kind = card.OpenState == BiasState.Long ? RowKind.Long : RowKind.Short,
                    Coloured = true
                });
            }

            if (card.Calls == 0)
            {
                Context.Add(new PanelLine { Label = "TRACK", Text = "no finished calls on this chart yet", Muted = true });
                return;
            }

            var text = card.Calls + " calls  " +
                       Math.Round(card.HitRate * 100m, 0, MidpointRounding.AwayFromZero)
                           .ToString("0", CultureInfo.InvariantCulture) + "% right  avg " +
                       Pts(card.AvgPoints) + "  net " + Pts(card.NetPoints);

            Context.Add(new PanelLine
            {
                Label = "TRACK",
                Text = text + (card.Calls < Scorecard.TooFew ? "  (too few)" : ""),
                Muted = card.Calls < Scorecard.TooFew
            });

            if (card.TodayCalls > 0)
                Context.Add(new PanelLine
                {
                    Label = "TODAY",
                    Text = card.TodayCalls + (card.TodayCalls == 1 ? " call  " : " calls  ") + Pts(card.TodayPoints) + " pts"
                });
        }

        private static string To(BiasState s) => BiasEngine.StateText(s);

        private static RowKind KindOf(BiasState s) =>
            s == BiasState.Long ? RowKind.Long : s == BiasState.Short ? RowKind.Short : RowKind.Neutral;

        private static string Pts(decimal v)
        {
            var r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            return (r > 0m ? "+" : "") + r.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Contracts(decimal v)
        {
            var r = Math.Round(v, 0, MidpointRounding.AwayFromZero);
            return (r > 0m ? "+" : "") + r.ToString("#,0", CultureInfo.InvariantCulture);
        }

        private static string Span(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h " + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        /// <summary>A factor that could have voted and was switched off says so, rather than "n/a".</summary>
        private static string DisabledReason(int i, FactorConfig cfg)
        {
            if (cfg == null || cfg.Enabled == null || i >= cfg.Enabled.Length) return "off";
            if (!cfg.Enabled[i]) return "switched off";
            if (cfg.Weight != null && i < cfg.Weight.Length && cfg.Weight[i] <= 0m) return "weight 0";
            return "off";
        }

        private static string RegimeText(BiasRecord r, GexSnapshot gex, bool present, bool stale,
                                         decimal tick)
        {
            if (!present) return "no feed";
            if (stale) return "stale";
            if (r.Regime == RegimeMode.None) return "mixed";

            var text = r.Regime == RegimeMode.Positive ? "+GAMMA" : "-GAMMA";

            if (gex != null && gex.GammaFlip.Known)
                text += "  flip " + Signed(BiasEngine.Round(r.Close - gex.GammaFlip.Value, tick));

            return text;
        }

        private static string FeedText(bool present, bool stale, int ageSeconds, GexSnapshot gex)
        {
            if (!present) return "GEX -";

            var source = gex != null && gex.Source != null ? gex.Source : "GEX";

            // TradeGEX problems already name TradeGEX; don't print it twice.
            if (gex != null && gex.Problem != null)
                return gex.Problem.StartsWith(source, StringComparison.OrdinalIgnoreCase)
                    ? gex.Problem
                    : source + " " + gex.Problem;

            if (gex != null && gex.LiveStream) return source + " live";
            if (stale) return source + " STALE " + BadgeModel.Age(ageSeconds);
            return source + " " + BadgeModel.Age(ageSeconds);
        }

        private static string Signed(decimal value)
        {
            var rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
            return (rounded > 0m ? "+" : "") + rounded.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
