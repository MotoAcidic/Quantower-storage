using System;
using System.Collections.Generic;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansAnchor
{
    public partial class OceansAnchor
    {
        /// <summary>
        /// A zone frozen for drawing. OnRender runs on every repaint -- pans, scrolls, crosshair
        /// moves -- and a Zone is live state the chart thread mutates. Copying five numbers once
        /// per closed bar removes both the race and any temptation to compute in the renderer.
        /// </summary>
        private struct ZoneView
        {
            public decimal Top, Bottom, Poc;
            public int Rank, StartBar;
            public bool Spent, TooFar;
            public SignalState State;
            public string Label;
        }

        private List<ZoneView> _renderZones = new List<ZoneView>();
        private List<string> _renderStatus;
        private string _clockError;

        private string _signalLine;

        /// <summary>
        /// Folds everything the renderer needs into plain fields. Called at bar close and on the
        /// live bar, never from OnRender.
        /// </summary>
        private void CacheForRender()
        {
            var views = new List<ZoneView>();
            var status = new List<string>();

            List<Zone> zones;
            lock (_sync) zones = _zones;

            _clockError = _clock != null && !_clock.Valid ? _clock.Error : null;

            if (zones != null)
            {
                foreach (var z in zones)
                {
                    views.Add(new ZoneView
                    {
                        Top = z.Top,
                        Bottom = z.Bottom,
                        Poc = z.Poc,
                        Rank = z.Rank,
                        StartBar = z.StartBar,
                        Spent = z.Spent,
                        TooFar = z.TooFar,
                        State = z.State,
                        Label = Kind(z.Kind) + " r" + z.Rank + " " + Num(z.Poc, 2)
                    });

                    var line = StatusFor(z);
                    if (line != null) status.Add(line);
                }
            }

            if (_tapeNote != null) status.Add("tape " + Dot + " " + _tapeNote);
            if (_log != null && _log.LastError != null) status.Add("log " + Dot + " " + _log.LastError);

            lock (_sync)
            {
                _renderZones = views;
                _renderStatus = status;
            }
        }

        /// <summary>
        /// One line per zone that is doing something. Dormant zones say nothing -- a status
        /// panel listing four dormant levels is a panel nobody reads by the third day.
        /// </summary>
        private string StatusFor(Zone z)
        {
            var head = "ANCHOR r" + z.Rank + " " + Num(z.Bottom, 2) + "-" + Num(z.Top, 2) + " " + Dot + " ";

            switch (z.State)
            {
                case SignalState.Armed:
                    return head + "ARMED " + (z.Side == TestSide.SupportLong ? "long" : "short");

                case SignalState.Triggered:
                    var n = z.Live != null ? z.Live.StackedEvents : 1;
                    var elapsed = Math.Max(0, CurrentBar - 1 - z.TriggeredBar);

                    return head + "TRIGGERED " + n + " evt" + (n == 1 ? "" : "s") + " " + Dot +
                           " clock " + Math.Min(elapsed, ClockBars) + "/" + ClockBars;

                case SignalState.Confirmed:
                    return head + "CONFIRMED " + Arrow + (_signalLine ?? string.Empty);

                case SignalState.Broken:
                    return head + "BROKEN";

                default:
                    return null;
            }
        }

        /// <summary>The stop and 1.5R readout, computed once when the signal confirms.</summary>
        private void CacheSignal(Zone zone, BarFacts facts)
        {
            decimal stop, target;

            _signalLine = Targets(zone, facts, out stop, out target)
                ? " stop " + Num(stop, 2) + ", 1.5R " + Num(target, 2) + "+"
                : string.Empty;
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null || context == null) return;

            var container = ChartInfo.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;

            List<ZoneView> zones;
            List<string> status;
            string clockError;

            lock (_sync)
            {
                zones = _renderZones;
                status = _renderStatus;
                clockError = _clockError;
            }

            // A clock that could not be settled draws the reason instead of the zones. Drawing
            // shelves anyway would put plausible, precisely wrong levels on the chart, and there
            // is no way to tell those from correct ones by looking.
            if (clockError != null)
            {
                context.DrawString(clockError, _statusFont, Conv(BrokenColor),
                                   region.Left + 6, region.Top + 6);
                return;
            }

            // Silent calibration draws nothing -- except a log that has stopped writing, which
            // is the one failure the run exists to prevent and would otherwise go unseen for days.
            if (SilentCalibration)
            {
                var log = _log;
                var logError = log != null ? log.LastError : null;

                if (logError != null)
                {
                    try
                    {
                        context.DrawString("ANCHOR log " + Dot + " " + logError, _statusFont,
                                           Conv(BrokenColor), region.Left + 6, region.Top + 6);
                    }
                    catch (Exception) { }
                }

                return;
            }

            // Every layer catches separately, and a failure prints itself on the chart.
            //
            // There is no debugger on the render thread. A layer that throws takes down every
            // layer after it silently -- including the status line that would have told you
            // something was wrong -- so the indicator just quietly stops drawing. Isolating
            // them costs two try blocks and is the only reason a bad zone is ever debuggable.
            var failures = new List<string>();

            if (zones != null)
            {
                foreach (var z in zones)
                {
                    try { DrawZone(context, container, region, z); }
                    catch (Exception ex) { failures.Add("zone " + Num(z.Poc, 2) + ": " + ex.Message); }
                }
            }

            if (ShowStatus && status != null)
            {
                try { DrawStatus(context, region, status); }
                catch (Exception ex) { failures.Add("status: " + ex.Message); }
            }

            if (failures.Count == 0) return;

            try
            {
                var y = region.Bottom - 16 * failures.Count;
                foreach (var f in failures)
                {
                    context.DrawString("ANCHOR render " + f, _statusFont, Conv(BrokenColor),
                                       region.Left + 6, y);
                    y += 16;
                }
            }
            catch (Exception) { }
        }

        private void DrawZone(RenderContext context, IChartContainer container, Rectangle region,
                              ZoneView z)
        {
            var yTop = container.GetYByPrice(z.Top, false);
            var yBottom = container.GetYByPrice(z.Bottom, false);

            if (yTop > yBottom) { var swap = yTop; yTop = yBottom; yBottom = swap; }

            // Entirely off-screen vertically: nothing to draw and no reason to measure a label.
            if (yBottom < region.Top || yTop > region.Bottom) return;

            var x = container.GetXByBar(z.StartBar, false);
            if (x < region.Left) x = region.Left;

            var height = Math.Max(1, yBottom - yTop);
            var rect = new Rectangle(x, yTop, Math.Max(1, region.Right - x), height);

            var baseColor = z.State == SignalState.Broken ? BrokenColor : ZoneColor;
            var opacity = Opacity(z);

            context.FillRectangle(Fade(baseColor, opacity), rect);
            context.DrawRectangle(new RenderPen(Fade(baseColor, Math.Min(100, opacity + 45)), 1), rect);

            DrawPocLine(context, container, rect, z, baseColor, opacity);

            if (ShowLabels) DrawLabel(context, region, yTop, yBottom, z, baseColor, opacity);
        }

        /// <summary>
        /// The node peak as a dashed line inside its shelf. The shelf says where the trade is;
        /// this says where the volume actually was.
        /// </summary>
        private void DrawPocLine(RenderContext context, IChartContainer container, Rectangle rect,
                                 ZoneView z, MColor baseColor, int opacity)
        {
            var y = container.GetYByPrice(z.Poc, false);
            if (y < rect.Top || y > rect.Bottom) return;

            var pen = new RenderPen(Fade(baseColor, Math.Min(100, opacity + 55)), 1,
                                    System.Drawing.Drawing2D.DashStyle.Dash);

            context.DrawLine(pen, rect.Left, y, rect.Right, y);
        }

        private void DrawLabel(RenderContext context, Rectangle region, int yTop, int yBottom,
                               ZoneView z, MColor baseColor, int opacity)
        {
            var size = context.MeasureString(z.Label, _labelFont);

            var x = region.Right - size.Width - 4;
            var y = yTop + Math.Max(0, (yBottom - yTop - size.Height) / 2);

            if (y < region.Top) y = region.Top;
            if (y + size.Height > region.Bottom) y = region.Bottom - size.Height;

            context.DrawString(z.Label, _labelFont, Fade(baseColor, Math.Min(100, opacity + 65)), x, y);
        }

        /// <summary>
        /// Rank drives opacity, and that is the whole visual hierarchy: the strongest zone on
        /// the chart should be the one the eye lands on without reading anything. Spent and
        /// out-of-range zones fade almost out -- they still render, because knowing a level got
        /// used up is information, but they must never compete with a live rank 1.
        /// </summary>
        private int Opacity(ZoneView z)
        {
            if (z.Spent || z.TooFar) return Math.Max(6, ZoneOpacity / 4);

            var boost = z.State == SignalState.Triggered || z.State == SignalState.Confirmed ? 18 : 0;

            var byRank = ZoneOpacity - (z.Rank - 1) * 4;
            if (byRank < 6) byRank = 6;

            return Math.Min(100, byRank + boost);
        }

        private void DrawStatus(RenderContext context, Rectangle region, List<string> lines)
        {
            var y = region.Top + 4;

            foreach (var line in lines)
            {
                var size = context.MeasureString(line, _statusFont);

                context.FillRectangle(Color.FromArgb(190, 16, 16, 20),
                                      new Rectangle(region.Left + 4, y, size.Width + 8, size.Height + 2));

                context.DrawString(line, _statusFont, Conv(ZoneColor), region.Left + 8, y + 1);

                y += size.Height + 3;
                if (y > region.Bottom - 12) return;
            }
        }

        private static Color Conv(MColor c) { return Color.FromArgb(c.A, c.R, c.G, c.B); }

        private static Color Fade(MColor c, int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            return Color.FromArgb((int)Math.Round(255 * percent / 100.0), c.R, c.G, c.B);
        }

        private static string Kind(ZoneKind kind)
        {
            switch (kind)
            {
                case ZoneKind.PriorRthPoc: return "RTH POC";
                case ZoneKind.SecondaryPoc: return "2nd POC";
                case ZoneKind.NakedPoc: return "NAKED";
                case ZoneKind.CompositeHvn: return "HVN";
                default: return "ON POC";
            }
        }
    }
}
