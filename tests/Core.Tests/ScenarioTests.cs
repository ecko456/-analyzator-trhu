using System;
using System.Collections.Generic;
using System.Linq;
using ReversalConfirmation.Core;
using Xunit;

namespace ReversalConfirmation.Tests
{
    /// <summary>
    /// Scenario tests modelled on the acceptance cases of the specification. Prices are relative to P (price after
    /// the noise warm-up); the reference level is a manual level so that the scenarios are deterministic.
    /// </summary>
    public class ScenarioTests
    {
        private static readonly DateTime SessionStart = new DateTime(2026, 2, 1, 23, 0, 0, DateTimeKind.Utc); // Sun 18:00 EST
        private static readonly DateTime PatternTime = new DateTime(2026, 2, 4, 15, 30, 0, DateTimeKind.Utc);  // Wed 10:30 EST

        private sealed class Scenario
        {
            public Synthetic S;
            public double P, Level;
            public int RevBar, ConfBar, PivotBar;
        }

        /// <summary>Initiative sell-off into a level, flush below it, reclaim (2-bar), confirmation, pullback fill, rally.</summary>
        /// <param name="anomaly">true: the reclaim bar is an outside bar of the flush bar (new low and higher high), so the BOS level
        /// is that high and C1 breaks it; false: the BOS level is the last lower high (p + 3) far above C1.</param>
        private static Scenario FlushReclaim(Action<Synthetic, double> afterReclaim = null, bool reclaim = true, bool anomaly = true, bool equalLow = false)
        {
            var s = new Synthetic(7, SessionStart, 5000);
            while (s.Time < PatternTime) s.Noise(1);
            double p = s.Price;
            var sc = new Scenario { S = s, P = p, Level = p - 6.0 };
            s.Add(p, p + 1.5, p - 0.25, p + 1.25, 900, 100);
            sc.PivotBar = s.Add(p + 1.25, p + 3, p + 1, p + 2.75, 900, 150).Index;   // last lower high left of the low
            s.Add(p + 2.75, p + 3, p + 0.5, p + 0.75, 2500, -700, 0.3);        // origin: first red bar, open = T1
            s.Add(p + 0.75, p + 1, p - 1.5, p - 1.25, 2500, -700, 0.3);
            s.Add(p - 1.25, p - 1, p - 3.5, p - 3.25, 2500, -700, 0.3);
            s.Add(p - 3.25, p - 3, p - 5.5, p - 5.25, 2500, -600, 0.3);
            s.Add(p - 5.25, p - 5, p - 6.75, p - 6.5, 4000, -1600, 0.15);       // flush through the level, close below
            // anomaly: an outside bar, one tick beyond the flush bar on both sides
            var rev = s.Add(p - 6.5, anomaly ? p - 4.75 : p - 5.0, anomaly && !equalLow ? p - 7.0 : p - 6.75, reclaim ? p - 5.0 : p - 6.25, 3000, 700, 0.2); // reclaim
            sc.RevBar = rev.Index;
            if (afterReclaim != null)
            {
                afterReclaim(s, p);
                return sc;
            }
            var conf = s.Add(p - 5.0, p - 2.75, p - 5.25, p - 3.0, 3000, 300, 0.4);   // confirmation C1
            sc.ConfBar = conf.Index;
            s.Add(p - 3.0, p - 2.75, p - 4.5, p - 3.5, 1500, -200, 0.5);             // pullback into the C1 VPOC
            for (int i = 0; i < 5; i++)
            {
                double o = s.Price;
                s.Add(o, o + 2.25, o - 0.25, o + 2.0, 2000, 300, 0.6);
            }
            s.Noise(40);
            return sc;
        }

        private static EngineSettings Settings(Scenario sc, bool onlyManual = false, int dir = 1)
        {
            var st = new EngineSettings { ManualLevels = sc.Level.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (onlyManual) OnlyManual(st);
            return st;
        }

        private static void OnlyManual(EngineSettings st)
        {
            st.WPrevVwap = st.WSessionVwap = st.WVwapBands = st.WFirstRthBar = st.WOpeningRange = st.WRthOpen = 0;
            st.WPrevDayHl = st.WPrevDayPoc = st.WPrevDayVa = st.WOvernight = st.WLiquidityPool = st.WSwing = 0;
            st.WDevVa = st.WWeekVa = st.WPrevWeekVa = st.WNakedVa = 0;
        }

        private static ReversalEngine Run(IEnumerable<Bar> bars, EngineSettings st, MemorySink sink = null)
        {
            var e = new ReversalEngine(st, Synthetic.Tick, 5, sink);
            foreach (var b in bars) e.OnBar(b);
            e.Flush();
            return e;
        }

        [Fact]
        public void Confirmation_WithoutBreakOfStructure_WaitsAndBecomesValidOnTheBreak()
        {
            var sc = FlushReclaim(anomaly: false);
            var sink = new MemorySink();
            var e = Run(sc.S.Bars, Settings(sc, onlyManual: true), sink);

            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            var line = e.Structures.Single(l => l.ContextId == rev.ContextId);
            Assert.Equal(sc.PivotBar, line.PivotBar);
            Assert.Equal(sc.P + 3, line.Price, 6);
            Assert.False(line.Anomaly);
            // ring tooltip: limit one tick before F5/F7, stop one tick below A, size for ES and MES
            var ring = e.Marks.Single(m => m.Type == MarkType.StructureBreak && m.ContextId == rev.ContextId);
            Assert.Contains("PLÁN VSTUPU", ring.Tooltip);
            Assert.Contains("SL 1 t pod A", ring.Tooltip);
            Assert.Contains("MES", ring.Tooltip);
            Assert.Contains("C v F7", ring.Tooltip);

            // order flow on the C1 candle, the close above the pivot comes 5 bars later (25 min < 30 min)
            int breakBar = sc.ConfBar + 5;
            Assert.Equal(breakBar, line.BreakBar);
            var c1 = e.Marks.Single(m => m.Type == MarkType.Confirmation && m.ContextId == rev.ContextId && m.Label == "C1");
            Assert.Equal(sc.ConfBar, c1.Bar);
            Assert.False(c1.Pending);
            Assert.False(c1.Expired);
            Assert.Equal(breakBar, c1.ValidBar);
            Assert.Contains("PLATNÉ", c1.Tooltip);
            Assert.Contains(e.Events, ev => ev.Type == EngineEventType.ConfirmationPending && ev.Bar == sc.ConfBar);
            Assert.Contains(e.Events, ev => ev.Type == EngineEventType.Confirmation && ev.Bar == breakBar);
            Assert.DoesNotContain(e.Events, ev => ev.Type == EngineEventType.Confirmation && ev.Bar < breakBar);
            var bos = e.Marks.Single(m => m.Type == MarkType.StructureBreak && m.ContextId == rev.ContextId);
            Assert.Equal(breakBar, bos.Bar);
            Assert.Equal(sc.P + 3, bos.Price, 6);

            // statistics enter on the close of the breaking candle, not on the order-flow candle
            var conf = sink.Rows.First(r => r.Get("kind") == "CONF");
            Assert.Equal(breakBar.ToString(), conf.Get("bar"));
            Assert.Equal("5", conf.Get("conf_wait_bars"));
        }

        [Fact]
        public void BreakMode_TheBreakItselfIsTheSignal_RatedByItsOrderFlow()
        {
            var sc = FlushReclaim(anomaly: false);
            var st = Settings(sc, onlyManual: true);
            st.BreakModeFrom = new TimeSpan(16, 0, 0);   // the pattern runs ~16:30 Prague
            var sink = new MemorySink();
            var e = Run(sc.S.Bars, st, sink);

            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            int breakBar = sc.ConfBar + 5;
            Assert.DoesNotContain(e.Marks, m => m.Type == MarkType.Confirmation && m.ContextId == rev.ContextId);
            var b = e.Marks.Single(m => m.Type == MarkType.Break && m.ContextId == rev.ContextId);
            Assert.Equal(breakBar, b.Bar);
            Assert.Equal(breakBar, b.ValidBar);
            Assert.InRange(b.Score, 0, 100);
            Assert.Contains("BREAK STRUKTURY", b.Tooltip);
            Assert.Contains(e.Events, ev => ev.Type == EngineEventType.Confirmation && ev.Bar == breakBar && ev.Text.StartsWith("B break"));
            Assert.DoesNotContain(e.Events, ev => ev.Type == EngineEventType.ConfirmationPending);
            Assert.Contains(sink.Rows, r => r.Get("kind") == "BRK" && r.Get("bar") == breakBar.ToString());
        }

        [Fact]
        public void OutsideBar_NeedsATickBeyondBothSides_ToBeTheAnomaly()
        {
            foreach (var (equalLow, expected) in new[] { (false, true), (true, false) })
            {
                var sc = FlushReclaim((s, p) => s.Noise(30), equalLow: equalLow);
                var e = Run(sc.S.Bars, Settings(sc, onlyManual: true));
                var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
                var line = e.Structures.Single(l => l.ContextId == rev.ContextId);
                Assert.Equal(expected, line.Anomaly);
                if (expected) Assert.Equal(sc.P - 4.75, line.Price, 6);   // high of the outside bar A
            }
        }

        [Fact]
        public void Confirmation_ExpiresWhenTheBreakDoesNotComeInTime()
        {
            var sc = FlushReclaim((s, p) =>
            {
                s.Add(p - 5.0, p - 2.75, p - 5.25, p - 3.0, 3000, 300, 0.4);   // order-flow confirmation, below the pivot p + 3
                for (int i = 0; i < 10; i++) s.Add(p - 3.0, p - 2.5, p - 3.5, p - 3.0, 1200, 0, 0.5);   // no break
                s.Noise(20);
            }, anomaly: false);
            var sink = new MemorySink();
            var e = Run(sc.S.Bars, Settings(sc, onlyManual: true), sink);

            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            var c1 = e.Marks.Single(m => m.Type == MarkType.Confirmation && m.ContextId == rev.ContextId);
            Assert.True(c1.Expired);
            Assert.Equal(-1, c1.ValidBar);
            Assert.Equal(c1.Bar + 6, c1.ExpiredBar);
            Assert.Contains("NEPLATNÉ", c1.Tooltip);
            Assert.DoesNotContain(e.Events, ev => ev.Type == EngineEventType.Confirmation);
            Assert.DoesNotContain(sink.Rows, r => r.Get("kind") == "CONF");
            var line = e.Structures.Single(l => l.ContextId == rev.ContextId);
            Assert.Equal(-1, line.BreakBar);
            Assert.False(line.Live);
        }

        [Fact]
        public void FlushAndReclaim_IsStrongTwoBarReversal_ThenConfirmationZoneFillAndTarget()
        {
            var sc = FlushReclaim();
            var sink = new MemorySink();
            var e = Run(sc.S.Bars, Settings(sc), sink);

            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            Assert.Equal(1, rev.Dir);
            Assert.True(rev.Strong, $"score {rev.Score:0.0}\n{rev.Tooltip}");
            Assert.Contains("2-bar", rev.Tooltip);
            Assert.Contains("Ruční úroveň", rev.Tooltip);

            var c1 = e.Marks.Single(m => m.Type == MarkType.Confirmation && m.ContextId == rev.ContextId);
            Assert.Equal(sc.ConfBar, c1.Bar);
            Assert.Equal("C1", c1.Label);

            var zone = e.Zones.Single(z => z.ContextId == rev.ContextId && z.Type == ZoneTypes.ConfirmationVpoc);
            Assert.Equal(sc.ConfBar, zone.StartBar);
            Assert.Equal(ZoneState.Filled, zone.State);
            Assert.Equal(sc.ConfBar + 1, zone.FillBar);
            // stop below the flush low, T1 = open of the first red bar of the initiative move
            Assert.True(zone.Stop < sc.P - 6.75);
            var t1 = zone.Targets.First(t => t.Name.StartsWith("T1"));
            Assert.Equal(sc.P + 2.75, t1.Price, 6);
            Assert.True(t1.HitBar > zone.FillBar);

            var revRow = sink.Rows.Single(r => r.Get("kind") == "REV" && r.Get("bar") == sc.RevBar.ToString());
            Assert.Equal("2-bar", revRow.Get("variant"));
            Assert.Equal("1", revRow.Get("hit_T1"));
        }

        [Fact]
        public void CloseBelowTheLevel_IsRejectedAsNoReclaim()   // Day 2 16:45
        {
            var sc = FlushReclaim((s, p) => s.Noise(30), reclaim: false);
            var sink = new MemorySink();
            var e = Run(sc.S.Bars, Settings(sc, onlyManual: true), sink);
            Assert.DoesNotContain(e.Marks, m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            var rej = sink.Rows.Where(r => r.Get("kind") == "REJ" && r.Get("bar") == sc.RevBar.ToString()).ToList();
            Assert.Contains(rej, r => r.Get("reject") == "no reclaim");
        }

        [Fact]
        public void NoReferenceLevel_IsRejectedAsNoLevel()   // Day 3 18:45
        {
            var sc = FlushReclaim((s, p) => s.Noise(30));
            var st = Settings(sc, onlyManual: true);
            st.ManualLevels = "";
            var sink = new MemorySink();
            var e = Run(sc.S.Bars, st, sink);
            Assert.DoesNotContain(e.Marks, m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            Assert.Contains(sink.Rows, r => r.Get("kind") == "REJ" && r.Get("bar") == sc.RevBar.ToString() && r.Get("reject") == "no level");
        }

        [Fact]
        public void NewLowBelowReversal_CancelsContext()
        {
            var sc = FlushReclaim((s, p) =>
            {
                s.Add(p - 5.0, p - 4.75, p - 7.5, p - 7.25, 3000, -900, 0.3);   // trades through the reversal low
                s.Noise(30);
            });
            var e = Run(sc.S.Bars, Settings(sc));
            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            Assert.Contains(e.Marks, m => m.Type == MarkType.ContextCancelled && m.ContextId == rev.ContextId && m.Bar == sc.RevBar + 1);
            Assert.DoesNotContain(e.Marks, m => m.Type == MarkType.Confirmation && m.ContextId == rev.ContextId);
        }

        [Fact]
        public void Retest_WithHigherLowAndWeakDelta_IsMarked_AndAllowsSecondConfirmation()   // Day 3 19:35 / 19:40
        {
            var sc = FlushReclaim((s, p) =>
            {
                s.Add(p - 5.0, p - 2.75, p - 5.25, p - 3.0, 3000, 300, 0.4);   // C1
                s.Add(p - 3.0, p - 2.75, p - 6.75, p - 5.75, 1500, -150, 0.5); // retest: higher low (A = p - 7), quiet
                s.Add(p - 5.75, p - 2.0, p - 6.0, p - 2.25, 3500, 450, 0.4);   // C2 after the retest
                for (int i = 0; i < 5; i++)
                {
                    double o = s.Price;
                    s.Add(o, o + 2.25, o - 0.25, o + 2.0, 2000, 300, 0.6);
                }
                s.Noise(40);
            });
            // only the manual level: a sharp pullback into a random level could legitimately be a bearish reversal
            var e = Run(sc.S.Bars, Settings(sc, onlyManual: true));
            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            Assert.Contains(e.Marks, m => m.Type == MarkType.Retest && m.ContextId == rev.ContextId && m.Bar == sc.RevBar + 2);
            Assert.Contains(e.Zones, z => z.ContextId == rev.ContextId && z.Type == ZoneTypes.Retest);
            var confs = e.Marks.Where(m => m.Type == MarkType.Confirmation && m.ContextId == rev.ContextId).OrderBy(m => m.Bar).ToList();
            Assert.Equal(new[] { "C1", "C2" }, confs.Select(c => c.Label).ToArray());
            Assert.Equal(sc.RevBar + 3, confs[1].Bar);
        }

        /// <summary>Stall (component I): selling absorbed at the level for several bars, then an up-thrust trigger.</summary>
        private static Scenario Stall()
        {
            var s = new Synthetic(11, SessionStart, 5000);
            while (s.Time < PatternTime) s.Noise(1);
            double p = s.Price;
            var sc = new Scenario { S = s, P = p, Level = p - 6.0 };
            s.Add(p, p + 3, p - 0.25, p + 2.75, 900, 150);
            s.Add(p + 2.75, p + 3, p + 0.5, p + 0.75, 2500, -700, 0.3);
            s.Add(p + 0.75, p + 1, p - 1.5, p - 1.25, 2500, -700, 0.3);
            s.Add(p - 1.25, p - 1, p - 3.5, p - 3.25, 2500, -700, 0.3);
            s.Add(p - 3.25, p - 3, p - 5.75, p - 5.25, 2500, -700, 0.3);
            // stall: lows pinned at the level, heavy selling absorbed, but no single bar looks like a reversal yet
            for (int i = 0; i < 4; i++)
                s.Add(p - 5.5, p - 5.0, p - 6.0, p - 5.75, 2600, -450, 0.5);
            var trig = s.Add(p - 5.75, p - 3.75, p - 5.75, p - 4.0, 2800, 400, 0.4);   // trigger: close above previous high
            sc.RevBar = trig.Index;
            for (int i = 0; i < 4; i++)
            {
                double o = s.Price;
                s.Add(o, o + 2.25, o - 0.25, o + 2.0, 2000, 300, 0.6);
            }
            s.Noise(40);
            return sc;
        }

        [Fact]
        public void StallAtLevel_IsDetectedByComponentI()   // Day 2 16:20 / Day 3 20:00 (bearish mirror below)
        {
            var sc = Stall();
            var st = Settings(sc, onlyManual: true);
            st.MaxMergedBars = 1;   // only the single-bar and the stall variant compete
            var e = Run(sc.S.Bars, st);
            var rev = e.Marks.Single(m => m.Type == MarkType.Reversal && m.Bar == sc.RevBar);
            Assert.Contains("stall", rev.Tooltip);
            Assert.Contains("● I stall (absorpce)", rev.Tooltip);
        }

        [Fact]
        public void BearishIsAnExactMirrorOfBullish()
        {
            foreach (var sc in new[] { FlushReclaim(), Stall() })
            {
                const double centre = 5000;
                var bull = Settings(sc, onlyManual: true);
                bull.MaxMergedBars = 2;
                var bear = bull.Clone();
                bear.ManualLevels = (2 * centre - sc.Level).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var a = Run(sc.S.Bars, bull);
                var b = Run(Synthetic.Mirror(sc.S.Bars, centre), bear);
                Assert.NotEmpty(a.Marks);
                Assert.Equal(a.Marks.Count, b.Marks.Count);
                for (int i = 0; i < a.Marks.Count; i++)
                {
                    Assert.Equal(a.Marks[i].Bar, b.Marks[i].Bar);
                    Assert.Equal(a.Marks[i].Type, b.Marks[i].Type);
                    Assert.Equal(-a.Marks[i].Dir, b.Marks[i].Dir);
                    Assert.Equal(a.Marks[i].Score, b.Marks[i].Score, 6);
                }
                Assert.Equal(a.Zones.Count, b.Zones.Count);
                for (int i = 0; i < a.Zones.Count; i++)
                {
                    Assert.Equal(2 * centre - a.Zones[i].Price, b.Zones[i].Price, 6);
                    Assert.Equal(a.Zones[i].State, b.Zones[i].State);
                    Assert.Equal(a.Zones[i].FillBar, b.Zones[i].FillBar);
                }
            }
        }

        [Fact]
        public void NoRepaint_MarksAreAppendOnly_AndBarsAreProcessedOnce()
        {
            var sc = FlushReclaim();
            var st = Settings(sc);
            var e = new ReversalEngine(st, Synthetic.Tick, 5);
            var seen = new List<(int bar, MarkType type, double score)>();
            foreach (var b in sc.S.Bars)
            {
                e.OnBar(b);
                e.OnBar(b);   // duplicate delivery (tick updates of the same bar) must be ignored
                for (int i = 0; i < seen.Count; i++)
                    Assert.Equal(seen[i], (e.Marks[i].Bar, e.Marks[i].Type, e.Marks[i].Score));
                for (int i = seen.Count; i < e.Marks.Count; i++)
                {
                    Assert.True(e.Marks[i].Bar <= b.Index, "a mark may only be placed on an already closed bar");
                    seen.Add((e.Marks[i].Bar, e.Marks[i].Type, e.Marks[i].Score));
                }
            }
            var again = Run(sc.S.Bars, st);
            Assert.Equal(seen, again.Marks.Select(m => (m.Bar, m.Type, m.Score)).ToList());
        }

        [Fact]
        public void OvernightSignal_ByDefault_ButNotWithRthOnlySetting()
        {
            var s = new Synthetic(3, SessionStart, 5000);
            while (s.Time < new DateTime(2026, 2, 4, 8, 0, 0, DateTimeKind.Utc)) s.Noise(1);   // 03:00 ET, overnight
            double p = s.Price;
            s.Add(p, p + 3, p - 0.25, p + 2.75, 900, 150);
            s.Add(p + 2.75, p + 3, p + 0.5, p + 0.75, 2500, -700, 0.3);
            s.Add(p + 0.75, p + 1, p - 1.5, p - 1.25, 2500, -700, 0.3);
            s.Add(p - 1.25, p - 1, p - 3.5, p - 3.25, 2500, -700, 0.3);
            s.Add(p - 3.25, p - 3, p - 5.5, p - 5.25, 2500, -600, 0.3);
            s.Add(p - 5.25, p - 5, p - 6.75, p - 6.5, 4000, -1600, 0.15);
            var rev = s.Add(p - 6.5, p - 4.75, p - 6.75, p - 5.0, 3000, 700, 0.2);
            s.Noise(20);
            var st = new EngineSettings { ManualLevels = (p - 6.0).ToString(System.Globalization.CultureInfo.InvariantCulture), Window = TradeWindow.All };
            Assert.Contains(Run(s.Bars, st).Marks, m => m.Type == MarkType.Reversal && m.Bar == rev.Index);
            // 03:00 ET = 09:00 Prague: inside the European (ETH) window, outside RTH
            st.Window = TradeWindow.Eth;
            Assert.Contains(Run(s.Bars, st).Marks, m => m.Type == MarkType.Reversal && m.Bar == rev.Index);
            st.Window = TradeWindow.Rth;
            Assert.DoesNotContain(Run(s.Bars, st).Marks, m => m.Type == MarkType.Reversal && m.Bar == rev.Index);
        }
    }
}
