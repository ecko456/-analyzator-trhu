using System;
using ReversalConfirmation.Core;
using Xunit;

namespace ReversalConfirmation.Tests
{
    public class StatsTests
    {
        [Fact]
        public void RollingStats_MeanStdAndWindow()
        {
            var r = new RollingStats(3);
            foreach (var x in new double[] { 100, 1, 2, 3 }) r.Add(x);   // 100 falls out of the window
            Assert.Equal(2, r.Mean, 9);
            Assert.Equal(1, r.Std, 9);
            Assert.Equal(50, r.PercentileRank(2), 9);
        }

        [Fact]
        public void SortedWindow_PercentileRankWithTiesAndEviction()
        {
            var w = new SortedWindow(10);
            for (int i = 1; i <= 10; i++) w.Add(i);
            Assert.Equal(45, w.PercentileRank(5), 9);     // 4 below + half of the tie
            w.Add(11);                                    // evicts 1
            Assert.Equal(0, w.PercentileRank(1.5), 9);
            Assert.Equal(100, w.PercentileRank(100), 9);
        }

        [Fact]
        public void Spearman_IsRankBased()
        {
            double[] x = { 0, 1, 2, 3, 4 }, y = { 1, 10, 100, 1000, 10000 };
            Assert.Equal(1, MathUtil.Spearman(x, y, 5), 9);
            double[] z = { 5, 4, 3, 2, 1 };
            Assert.Equal(-1, MathUtil.Spearman(x, z, 5), 9);
        }

        [Fact]
        public void Atr_IsWilder()
        {
            var a = new Atr(3);
            a.Add(10, 8, 9);   // TR 2
            a.Add(11, 9, 10);  // TR 2
            a.Add(14, 10, 13); // TR 4
            Assert.Equal(8.0 / 3, a.Value, 9);
            a.Add(13, 12, 12.5); // TR 1 -> (8/3*2 + 1)/3
            Assert.Equal((8.0 / 3 * 2 + 1) / 3, a.Value, 9);
        }

        [Fact]
        public void TodStats_UsesSameSlotOfPreviousSessions_ThenFallsBack()
        {
            var t = new TodStats(days: 10, neighbors: 1, minSamples: 5, fallbackBars: 50);
            for (long s = 1; s <= 3; s++)
                for (int slot = 0; slot < 10; slot++)
                    t.Add(slot, s, slot == 5 ? 1000 : 10);
            var b = t.Query(5, 4);             // slots 4..6 of sessions 1..3 = 9 samples
            Assert.True(b.TimeOfDay);
            Assert.Equal(9, b.Count);
            Assert.Equal((3 * 1000 + 6 * 10) / 9.0, b.Mean, 9);
            var fb = t.Query(-1, 4);           // non time-based chart: rolling fallback
            Assert.False(fb.TimeOfDay);
            Assert.Equal(30, fb.Count);
        }
    }

    public class SessionTests
    {
        private static SessionClock Clock() => new SessionClock(new EngineSettings(), 5);

        [Theory]
        // US and EU change DST on different dates: between 2026-10-25 and 2026-11-01 the RTH open is 14:30 in Prague
        [InlineData("2026-10-27T13:30:00Z", true, 0)]     // 09:30 EDT
        [InlineData("2026-11-03T14:30:00Z", true, 0)]     // 09:30 EST
        [InlineData("2026-11-03T13:30:00Z", false, -60)]  // 08:30 EST, pre-market
        [InlineData("2026-09-24T14:00:00Z", true, 30)]    // 16:00 Prague = 10:00 EDT (Day 1 of the spec)
        public void RthIsResolvedInNewYorkTime(string utc, bool rth, double minutesFromOpen)
        {
            var bt = Clock().Resolve(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
            Assert.Equal(rth, bt.IsRth);
            Assert.Equal(minutesFromOpen, bt.MinutesFromRth, 6);
        }

        [Fact]
        public void EveningSessionBelongsToNextTradeDate()
        {
            var c = Clock();
            var sundayOpen = c.Resolve(new DateTime(2026, 2, 1, 23, 0, 0, DateTimeKind.Utc));   // Sun 18:00 EST
            var mondayRth = c.Resolve(new DateTime(2026, 2, 2, 15, 0, 0, DateTimeKind.Utc));    // Mon 10:00 EST
            Assert.Equal(sundayOpen.SessionId, mondayRth.SessionId);
            Assert.Equal(0, sundayOpen.MinutesFromEth, 6);
            Assert.Equal(0, sundayOpen.Slot);
        }

        [Fact]
        public void NewsWindowFlagsBarsAroundReleaseTimes()
        {
            var c = Clock();
            Assert.True(c.Resolve(new DateTime(2026, 2, 6, 13, 25, 0, DateTimeKind.Utc)).News);   // 08:25 bar overlaps 08:30 ± 5
            Assert.True(c.Resolve(new DateTime(2026, 2, 6, 15, 0, 0, DateTimeKind.Utc)).News);    // 10:00
            Assert.False(c.Resolve(new DateTime(2026, 2, 6, 15, 10, 0, DateTimeKind.Utc)).News);  // 10:10
        }
    }

    public class BarTests
    {
        [Fact]
        public void OrientedBearishView_MirrorsPricesAndSwapsAggressors()
        {
            var b = Bar.Create(0, DateTime.UtcNow, 100.5, 101, 100, 100.25, 0.25,
                new[] { 100.0, 100.25, 100.5, 100.75, 101.0 }, new double[] { 5, 1, 1, 1, 1 }, new double[] { 0, 2, 2, 2, 9 }, 5);
            var up = b.Oriented(1);
            var dn = b.Oriented(-1);
            Assert.Equal(-101, dn.L);
            Assert.Equal(-100, dn.H);
            Assert.Equal(9, dn.Against[0]);     // buyers at the high are "against" a bearish reversal
            Assert.Equal(-b.Delta, dn.Delta);
            Assert.Equal(up.Volume, dn.Volume);
            Assert.Equal(b.PocIndex, b.Levels - 1 - dn.PocIndex);
        }

        [Fact]
        public void Merge_SumsLevelsAndKeepsOpenOfFirstCloseOfLast()
        {
            var a = Bar.Create(0, DateTime.UtcNow, 10, 10.5, 10, 10.25, 0.25, new[] { 10.0, 10.25, 10.5 }, new double[] { 1, 2, 3 }, new double[] { 1, 1, 1 }, 3);
            var b = Bar.Create(1, DateTime.UtcNow, 10.25, 10.25, 9.75, 10, 0.25, new[] { 9.75, 10.0, 10.25 }, new double[] { 4, 4, 4 }, new double[] { 1, 1, 1 }, 3);
            var m = OBar.Merge(new[] { a.Oriented(1), b.Oriented(1) }, 0, 1);
            Assert.Equal(10, m.O);
            Assert.Equal(10, m.C);
            Assert.Equal(9.75, m.L);
            Assert.Equal(10.5, m.H);
            Assert.Equal(a.Volume + b.Volume, m.Volume);
            Assert.Equal(a.Delta + b.Delta, m.Delta);
            Assert.Equal(1 + 1 + 4 + 1, m.LevelVolume(1));   // price 10.0 from both bars
        }
    }
}
