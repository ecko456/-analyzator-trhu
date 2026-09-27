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
            public int RevBar, ConfBar;
        }

        /// <summary>Initiative sell-off into a level, flush below it, reclaim (2-bar), confirmation, pullback fill, rally.</summary>
        private static Scenario FlushReclaim(Action<Synthetic, double> afterReclaim = null, bool reclaim = true)
        {
            var s = new Synthetic(7, SessionStart, 5000);
            while (s.Time < PatternTime) s.Noise(1);
            double p = s.Price;
            var sc = new Scenario { S = s, P = p, Level = p - 6.0 };
            s.Add(p, p + 1.5, p - 0.25, p + 1.25, 900, 100);
            s.Add(p + 1.25, p + 3, p + 1, p + 2.75, 900, 150);
            s.Add(p + 2.75, p + 3, p + 0.5, p + 0.75, 2500, -700, 0.3);        // origin: first red bar, open = T1
            s.Add(p + 0.75, p + 1, p - 1.5, p - 1.25, 2500, -700, 0.3);
            s.Add(p - 1.25, p - 1, p - 3.5, p - 3.25, 2500, -700, 0.3);
            s.Add(p - 3.25, p - 3, p - 5.5, p - 5.25, 2500, -600, 0.3);
            s.Add(p - 5.25, p - 5, p - 6.75, p - 6.5, 4000, -1600, 0.15);       // flush through the level, close below
            var rev = s.Add(p - 6.5, p - 4.75, p - 6.75, reclaim ? p - 5.0 : p - 6.25, 3000, 700, 0.2); // reclaim
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
        }

        private static ReversalEngine Run(IEnumerable<Bar> bars, EngineSettings st, MemorySink sink = null)
        {
            var e = new ReversalEngine(st, Synthetic.Tick, 5, sink);
            foreach (var b in bars) e.OnBar(b);
            e.Flush();
            return e;
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
                s.Add(p - 3.0, p - 2.75, p - 6.5, p - 5.75, 1500, -150, 0.5);  // retest: higher low, quiet
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
            Assert.Contains("I=", rev.Tooltip);
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
            var st = new EngineSettings { ManualLevels = (p - 6.0).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            Assert.Contains(Run(s.Bars, st).Marks, m => m.Type == MarkType.Reversal && m.Bar == rev.Index);
            st.SignalSession = SessionMode.Rth;
            Assert.DoesNotContain(Run(s.Bars, st).Marks, m => m.Type == MarkType.Reversal && m.Bar == rev.Index);
        }
    }
}
