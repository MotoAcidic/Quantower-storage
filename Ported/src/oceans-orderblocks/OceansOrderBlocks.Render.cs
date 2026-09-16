using System;
using System.Collections.Generic;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansOrderBlocks
{
    public partial class OceansOrderBlocks
    {
        /// <summary>
        /// A zone frozen for drawing. OnRender runs on every repaint and a Zone is state the
        /// chart thread mutates; copying a few numbers per update removes the race and any
        /// temptation to compute in the renderer.
        /// </summary>
        private struct ZoneView
        {
            public ObTf Tf;
            public bool Bull, Faded, Fresh, HasPoc;
            public decimal Top, Bottom, Mid, Poc;
            public int StartBar, EndBar;
            public string Label;
        }

        private struct PanelRow
        {
            public string Name, Value;
            public int Tone;   // 1 demand, -1 supply, 0 neutral note
        }

        private List<ZoneView> _renderZones = new List<ZoneView>();
        private List<PanelRow> _renderRows = new List<PanelRow>();
        private string _clockError;

        private readonly RenderFont _labelFont = new RenderFont("Arial", 8f);
        private readonly RenderFont _panelFont = new RenderFont("Consolas", 9f);

        private void CacheForRender()
        {
            var views = new List<ZoneView>();
            var rows = new List<PanelRow>();
            var clockError = _clock != null && !_clock.Valid ? _clock.Error : null;

            if (clockError == null && _clock == null && CurrentBar >= 3)
                clockError = "Ocean's Order Blocks: bar clock not settled yet.";

            var eng = _engine;
            if (eng != null)
            {
                foreach (var z in eng.Book.Faded) views.Add(View(z, true));

                // Weekly first so the finer blocks paint over it.
                for (var tf = (int)ObTf.Weekly; tf >= 0; tf--)
                    foreach (var z in eng.Book.Active)
                        if ((int)z.Tf == tf) views.Add(View(z, false));

                foreach (var z in eng.Book.Active)
                    if (z.Tf == ObTf.Session) views.Add(View(z, false));

                Rows(eng, rows);
            }

            lock (_sync)
            {
                _renderZones = views;
                _renderRows = rows;
                _clockError = clockError;
            }
        }

        private ZoneView View(Zone z, bool faded)
        {
            var v = new ZoneView
            {
                Tf = z.Tf,
                Bull = z.Bull,
                Faded = faded,
                Fresh = z.Status == ZoneStatus.Fresh,
                Top = z.Top,
                Bottom = z.Bottom,
                Mid = z.Mid,
                Poc = z.Poc,
                HasPoc = z.PocVolume > 0m,
                StartBar = z.StartBar,
                EndBar = faded ? z.EndBar : -1
            };

            if (faded) return v;

            var label = Fmt.Tag(z.Tf) + (z.Bull ? " ▲ " : " ▼ ") + Fmt.Price(z.Mid) +
                        " · brk " + Fmt.Signed(z.BreakerDelta);

            if (ShowZoneFlow)
            {
                var vol = z.FlowVolume;
                var delta = z.FlowDelta;

                decimal[] live;
                if (_liveFlow.TryGetValue(z.Id, out live))
                {
                    vol += live[0];
                    delta += live[1];
                }

                if (vol > 0m) label += " · zΔ " + Fmt.Signed(delta) + " / " + Fmt.Compact(vol);
            }

            label += z.Touches == 0 ? " · fresh" : " · t" + z.Touches;

            v.Label = label;
            return v;
        }

        /// <summary>
        /// The panel: live bar delta, CVD, then anything that explains a missing timeframe. A
        /// weekly block that never appears because the chart holds four days of history must
        /// say so, not just be absent.
        /// </summary>
        private void Rows(ObEngine eng, List<PanelRow> rows)
        {
            if (_hasLive)
            {
                rows.Add(new PanelRow { Name = "Δ BAR", Value = Fmt.Signed(_liveDelta), Tone = Math.Sign(_liveDelta) });
                rows.Add(new PanelRow
                {
                    Name = CvdMode == CvdAnchor.Session ? "CVD RTH" : "CVD DAY",
                    Value = Fmt.Signed(_liveCvd),
                    Tone = Math.Sign(_liveCvd)
                });
            }

            var shown = new[] { Show1H, Show4H, ShowDaily, ShowWeekly };

            for (var i = 0; i < 4; i++)
            {
                if (!shown[i]) continue;

                var tf = (ObTf)i;
                var tag = Fmt.Tag(tf);

                if (_config != null && !_config.Enabled[i])
                {
                    Note(rows, tag + " off: chart bars are coarser than " + tag);
                    continue;
                }

                var built = eng.CompleteBars(tf);
                if (built < 2)
                    Note(rows, tag + ": " + built + " full bar" + (built == 1 ? "" : "s") +
                               " loaded, needs 2 - load more days");
                else if (MinDisplacementAtr > 0m && eng.AtrCount(tf) < 14)
                    Note(rows, tag + ": ATR " + eng.AtrCount(tf) + "/14 - displacement filter holds " + tag);
            }

            if (_footprintHits == 0 && _footprintMisses > 0)
                Note(rows, "no footprint on this feed - zone Δ unavailable");
        }

        private static void Note(List<PanelRow> rows, string text)
        {
            rows.Add(new PanelRow { Name = text, Value = string.Empty, Tone = 0 });
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null || context == null) return;

            var container = ChartInfo.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;

            List<ZoneView> zones;
            List<PanelRow> rows;
            string clockError;

            lock (_sync)
            {
                zones = _renderZones;
                rows = _renderRows;
                clockError = _clockError;
            }

            // An unsettled clock draws the reason instead of blocks. Blocks built on the wrong
            // clock would be plausible, precisely wrong, and impossible to tell apart by eye.
            if (clockError != null)
            {
                try
                {
                    context.DrawString(clockError, _panelFont, Conv(SupplyColor), region.Left + 6, region.Top + 6);
                }
                catch (Exception) { }

                return;
            }

            // Every layer catches separately and prints its own failure. There is no debugger on
            // the render thread, and a layer that throws takes every later layer down with it.
            var failures = new List<string>();

            if (zones != null)
            {
                foreach (var z in zones)
                {
                    try { DrawZone(context, container, region, z); }
                    catch (Exception ex) { failures.Add("zone " + Fmt.Price(z.Mid) + ": " + ex.Message); }
                }

                if (ShowZoneLabels)
                {
                    try { DrawLabels(context, container, region, zones); }
                    catch (Exception ex) { failures.Add("labels: " + ex.Message); }
                }
            }

            if (ShowDeltaPanel && rows != null && rows.Count > 0)
            {
                try { DrawPanel(context, region, rows); }
                catch (Exception ex) { failures.Add("panel: " + ex.Message); }
            }

            if (failures.Count == 0) return;

            try
            {
                var y = region.Bottom - 16 * failures.Count;
                foreach (var f in failures)
                {
                    context.DrawString("OB render " + f, _panelFont, Conv(SupplyColor), region.Left + 6, y);
                    y += 16;
                }
            }
            catch (Exception) { }
        }

        private void DrawZone(RenderContext context, IChartContainer container, Rectangle region, ZoneView z)
        {
            var yTop = container.GetYByPrice(z.Top, false);
            var yBottom = container.GetYByPrice(z.Bottom, false);
            if (yTop > yBottom) { var swap = yTop; yTop = yBottom; yBottom = swap; }

            if (yBottom < region.Top || yTop > region.Bottom) return;

            var x1 = container.GetXByBar(z.StartBar, false);
            var x2 = z.EndBar >= 0 ? container.GetXByBar(z.EndBar, false) : region.Right;

            if (x2 < region.Left || x1 > region.Right) return;
            if (x1 < region.Left) x1 = region.Left;
            if (x2 > region.Right) x2 = region.Right;

            var rect = new Rectangle(x1, yTop, Math.Max(1, x2 - x1), Math.Max(1, yBottom - yTop));
            var color = z.Faded ? FadedColor : z.Bull ? DemandColor : SupplyColor;

            // Fresh blocks full strength, tested ones dimmer, mitigated ones barely there.
            var fill = z.Faded ? Math.Max(3, FillOpacity / 3)
                     : z.Fresh ? FillOpacity
                     : Math.Max(4, FillOpacity * 2 / 3);

            context.FillRectangle(Fade(color, fill), rect);
            context.DrawRectangle(new RenderPen(Fade(color, z.Faded ? 30 : 70), z.Faded ? 1 : Border(z.Tf)), rect);

            if (ShowMidlines)
            {
                var ym = container.GetYByPrice(z.Mid, false);
                var dash = new RenderPen(Fade(color, z.Faded ? 20 : 45), 1, System.Drawing.Drawing2D.DashStyle.Dash);
                context.DrawLine(dash, rect.Left, ym, rect.Right, ym);
            }

            if (ShowZonePoc && z.HasPoc && !z.Faded)
            {
                var yp = container.GetYByPrice(z.Poc, false);
                if (yp >= rect.Top && yp <= rect.Bottom)
                    context.DrawLine(new RenderPen(Fade(color, 90), 2), rect.Left, yp, rect.Right, yp);
            }
        }

        /// <summary>Weight by timeframe, as in the Pine: weekly 3, daily 2, the rest 1.</summary>
        private static int Border(ObTf tf)
        {
            return tf == ObTf.Weekly ? 3 : tf == ObTf.Daily ? 2 : 1;
        }

        /// <summary>
        /// Right-edge labels at each block's midline, pushed apart vertically so stacked
        /// blocks stay readable instead of printing over each other.
        /// </summary>
        private void DrawLabels(RenderContext context, IChartContainer container, Rectangle region,
                                List<ZoneView> zones)
        {
            var items = new List<KeyValuePair<int, ZoneView>>();
            foreach (var z in zones)
            {
                if (z.Faded || z.Label == null) continue;

                var y = container.GetYByPrice(z.Mid, false);
                if (y < region.Top || y > region.Bottom) continue;

                items.Add(new KeyValuePair<int, ZoneView>(y, z));
            }

            items.Sort((a, b) => a.Key.CompareTo(b.Key));

            var nextFree = int.MinValue;

            foreach (var item in items)
            {
                var z = item.Value;
                var size = context.MeasureString(z.Label, _labelFont);
                var h = size.Height + 2;

                var y = item.Key - h / 2;
                if (y < nextFree) y = nextFree;
                if (y + h > region.Bottom) break;

                var x = region.Right - size.Width - 10;
                var bg = z.Bull ? DemandColor : SupplyColor;

                context.FillRectangle(Fade(bg, 80), new Rectangle(x - 4, y, size.Width + 8, h));
                context.DrawString(z.Label, _labelFont, Color.White, x, y + 1);

                nextFree = y + h + 1;
            }
        }

        private void DrawPanel(RenderContext context, Rectangle region, List<PanelRow> rows)
        {
            var inner = 0;
            var lineHeight = 0;

            foreach (var r in rows)
            {
                var n = context.MeasureString(r.Name, _panelFont);
                var w = n.Width;
                if (r.Value.Length > 0) w += 16 + context.MeasureString(r.Value, _panelFont).Width;

                if (w > inner) inner = w;
                if (n.Height > lineHeight) lineHeight = n.Height;
            }

            var width = inner + 12;
            var height = rows.Count * (lineHeight + 2) + 8;

            var left = DeltaPanelCorner == PanelCorner.TopLeft || DeltaPanelCorner == PanelCorner.BottomLeft
                ? region.Left + 6
                : region.Right - width - 6;
            var top = DeltaPanelCorner == PanelCorner.TopLeft || DeltaPanelCorner == PanelCorner.TopRight
                ? region.Top + 6
                : region.Bottom - height - 6;

            context.FillRectangle(Color.FromArgb(205, 13, 17, 23), new Rectangle(left, top, width, height));
            context.DrawRectangle(new RenderPen(Color.FromArgb(90, 128, 128, 128), 1), new Rectangle(left, top, width, height));

            var y = top + 4;
            foreach (var r in rows)
            {
                var nameColor = r.Value.Length > 0 ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(220, 230, 190, 90);
                context.DrawString(r.Name, _panelFont, nameColor, left + 6, y);

                if (r.Value.Length > 0)
                {
                    var v = context.MeasureString(r.Value, _panelFont);
                    var tone = r.Tone > 0 ? Conv(DemandColor) : r.Tone < 0 ? Conv(SupplyColor) : Color.White;
                    context.DrawString(r.Value, _panelFont, tone, left + width - 6 - v.Width, y);
                }

                y += lineHeight + 2;
            }
        }

        private static Color Conv(MColor c) { return Color.FromArgb(255, c.R, c.G, c.B); }

        private static Color Fade(MColor c, int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            return Color.FromArgb((int)Math.Round(255 * percent / 100.0), c.R, c.G, c.B);
        }
    }
}
