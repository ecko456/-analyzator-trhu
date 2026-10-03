using System;
using System.Collections.Generic;
using System.Linq;
using ReversalConfirmation.Core;
using Xunit;

namespace ReversalConfirmation.Tests
{
    /// <summary>Market / volume profile levels: value area maths, TPO counting, naked levels, weekly roll-over.</summary>
    public class ProfileTests
    {
        private const double Tick = 0.25;
        private int _index;

        private static EngineSettings Settings(ProfileSource src = ProfileSource.Volume) => new EngineSettings
        {
            PriorDayProfile = SessionMode.Eth, WeekProfile = SessionMode.Eth, ProfileType = src, NakedMaxDays = 10
        };

        /// <summary>One bar with all its volume at the given prices (bid = ask = v / 2).</summary>
        private Bar MakeBar(DateTime utc, params (double price, double vol)[] levels)
        {
            var p = levels.Select(l => l.price).ToArray();
            var half = levels.Select(l => l.vol / 2).ToArray();
            double hi = p.Max(), lo = p.Min();
            return Bar.Create(_index++, utc, lo, hi, lo, hi, Tick, p, half, (double[])half.Clone(), p.Length);
        }

        private static void Feed(LevelTracker lt, SessionClock clock, Bar b) => lt.Update(b, clock.Resolve(b.TimeUtc));

        private static List<RefLevel> Snap(LevelTracker lt, SessionClock clock, DateTime utc)
        {
            var list = new List<RefLevel>();
            lt.Snapshot(list, 2.0, clock.Resolve(utc).SessionId);
            return list;
        }

        private static double Of(List<RefLevel> l, LevelKind k) => l.Single(x => x.Kind == k).Price;

        [Fact]
        public void ValueArea_GrowsFromThePocTowardTheBiggerNeighbour_AndUntouchedEdgesStayNaked()
        {
            var st = Settings();
            var clock = new SessionClock(st, 5);
            var lt = new LevelTracker(st, Tick, 5);
            // day 1 (trade date Tue 3 Feb 2026, opens Mon 18:00 EST = 23:00 UTC): 100:10, 101:50, 102:30, 103:10
            var d1 = new DateTime(2026, 2, 2, 23, 0, 0, DateTimeKind.Utc);
            Feed(lt, clock, MakeBar(d1, (100, 10), (101, 50)));
            Feed(lt, clock, MakeBar(d1.AddMinutes(5), (102, 30), (103, 10)));
            // day 2 trades far above, never at 100-103
            var d2 = new DateTime(2026, 2, 3, 23, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 3; i++) Feed(lt, clock, MakeBar(d2.AddMinutes(5 * i), (110, 40), (111, 20)));

            var day2 = Snap(lt, clock, d2.AddMinutes(20));
            // POC 101 (50); +102 (30) beats +100 (10): 80 % >= 70 % -> VA 101-102
            Assert.Equal(101, Of(day2, LevelKind.PrevDayPoc));
            Assert.Equal(102, Of(day2, LevelKind.PrevDayVah));
            Assert.Equal(101, Of(day2, LevelKind.PrevDayVal));
            Assert.DoesNotContain(day2, x => x.IsNaked);   // the previous day is not "naked", it is the previous day
            // second session of the week: developing weekly VA over both days (day 2's 110 with 120 lots is the POC)
            Assert.Equal(110, Of(day2, LevelKind.WeekPoc));

            // day 3: day 1's levels are now naked (2 sessions back)
            var d3 = new DateTime(2026, 2, 4, 23, 0, 0, DateTimeKind.Utc);
            Feed(lt, clock, MakeBar(d3, (110, 10)));
            var day3 = Snap(lt, clock, d3.AddMinutes(5));
            var naked = day3.Where(x => x.IsNaked).ToList();
            Assert.Equal(3, naked.Count);
            Assert.All(naked, x => Assert.Equal(2, x.Age));
            Assert.Equal(102, Of(day3, LevelKind.NakedVah));
            Assert.Contains("před 2 dny", day3.Single(x => x.Kind == LevelKind.NakedVah).Name);

            // price trades at 102: the naked VAH is gone, VAL and POC at 101 stay naked
            Feed(lt, clock, MakeBar(d3.AddMinutes(5), (102.5, 10), (102, 10)));
            var after = Snap(lt, clock, d3.AddMinutes(10));
            Assert.DoesNotContain(after, x => x.Kind == LevelKind.NakedVah);
            Assert.Equal(101, Of(after, LevelKind.NakedVal));
            Assert.Equal(101, Of(after, LevelKind.NakedPoc));
        }

        [Fact]
        public void DevelopingValueArea_AppearsAfterAnHourOfProfile()
        {
            var st = Settings();
            var clock = new SessionClock(st, 5);
            var lt = new LevelTracker(st, Tick, 5);
            var d1 = new DateTime(2026, 2, 2, 23, 0, 0, DateTimeKind.Utc);
            // 200: 30, 201: 20 per bar -> POC 200 holds 60 %, the value area needs 201 too
            for (int i = 0; i < 11; i++) Feed(lt, clock, MakeBar(d1.AddMinutes(5 * i), (200, 30), (201, 20)));
            Assert.DoesNotContain(Snap(lt, clock, d1.AddMinutes(55)), x => x.Kind == LevelKind.DevPoc);   // 55 min
            Feed(lt, clock, MakeBar(d1.AddMinutes(55), (200, 30), (201, 20)));
            var l = Snap(lt, clock, d1.AddMinutes(60));
            Assert.Equal(200, Of(l, LevelKind.DevPoc));
            Assert.Equal(201, Of(l, LevelKind.DevVah));
        }

        [Fact]
        public void Tpo_CountsEachPriceOncePerThirtyMinutePeriod()
        {
            var st = Settings(ProfileSource.Tpo);
            var clock = new SessionClock(st, 5);
            var lt = new LevelTracker(st, Tick, 5);
            var d1 = new DateTime(2026, 2, 2, 23, 0, 0, DateTimeKind.Utc);
            // period 1: 100-101 then 100.5-102 -> one TPO on 100 ... 102 (the overlap is not counted twice)
            Feed(lt, clock, MakeBar(d1, (100, 1000), (101, 1)));
            Feed(lt, clock, MakeBar(d1.AddMinutes(5), (100.5, 1), (102, 1)));
            // period 2 and 3: 101-101.5 -> 101 and 101.25, 101.5 get more TPOs, huge volume at 100 does not matter
            Feed(lt, clock, MakeBar(d1.AddMinutes(30), (101, 1), (101.5, 1)));
            Feed(lt, clock, MakeBar(d1.AddMinutes(60), (101, 1), (101.25, 1)));
            var d2 = new DateTime(2026, 2, 3, 23, 0, 0, DateTimeKind.Utc);
            Feed(lt, clock, MakeBar(d2, (150, 1)));
            var l = Snap(lt, clock, d2.AddMinutes(5));
            // TPOs: 100-100.75 and 101.75-102 one each, 101.5 two, 101 and 101.25 three (tie -> nearer the middle, 101)
            Assert.Equal(101, Of(l, LevelKind.PrevDayPoc));
            // 70 % of 14 TPOs: 101 -> 101.25 -> 101.5 -> 101.75 -> 102 (up wins ties) = 10
            Assert.Equal(102, Of(l, LevelKind.PrevDayVah));
            Assert.Equal(101, Of(l, LevelKind.PrevDayVal));
        }

        [Fact]
        public void NewWeek_TurnsTheWeeklyProfileIntoPreviousWeekLevels()
        {
            var st = Settings();
            var clock = new SessionClock(st, 5);
            var lt = new LevelTracker(st, Tick, 5);
            var fri = new DateTime(2026, 2, 5, 23, 0, 0, DateTimeKind.Utc);    // trade date Fri 6 Feb
            Feed(lt, clock, MakeBar(fri, (300, 10), (301, 80), (302, 10)));
            var sun = new DateTime(2026, 2, 8, 23, 0, 0, DateTimeKind.Utc);    // Sunday evening = Monday 9 Feb, new week
            Feed(lt, clock, MakeBar(sun, (320, 10)));
            var l = Snap(lt, clock, sun.AddMinutes(5));
            Assert.Equal(301, Of(l, LevelKind.PrevWeekPoc));
            Assert.DoesNotContain(l, x => x.Kind == LevelKind.WeekPoc);  // first session of the week: no weekly VA yet
        }
    }
}

namespace ReversalConfirmation.Tests
{
    public class FiboTradeTests
    {
        private static OBar B(int i, double o, double h, double l, double c) =>
            Bar.Create(i, new DateTime(2026, 2, 4, 15, 0, 0, DateTimeKind.Utc).AddMinutes(5 * i), o, h, l, c, 0.25,
                new[] { l, h }, new[] { 10.0, 10.0 }, new[] { 10.0, 10.0 }, 2).Oriented(1);

        [Fact]
        public void Op_FollowsTheDepthOfTheCorrection()
        {
            var row = new LogRow();
            var tr = new Tracker
            {
                Row = row, Dir = 1, Kind = "FIB", EntryBar = 0, Entry = 100, Stop = 90, R = 10, Tick = 0.25, TolTarget = 0.5,
                Horizons = new[] { 3, 6, 12, 24, 36 }, VBars = 6, Primary = 0, AdaptiveRange = 20
            };
            tr.Targets[0] = 120; tr.Targets[1] = tr.Targets[2] = double.NaN; tr.Targets[3] = 115; tr.Targets[4] = 120;
            tr.OnEntryBar(B(0, 101, 101, 98, 99), 0);      // C = 98 -> OP 118
            Assert.Equal(118, tr.Targets[0]);
            tr.Update(B(1, 99, 99.5, 95, 96), 1, 50);       // deeper correction: C = 95 -> OP 115
            Assert.Equal(115, tr.Targets[0]);
            tr.Update(B(2, 96, 115.25, 96, 115), 2, 50);    // reaches the adapted OP
            Assert.True(tr.PrimaryHit);
            Assert.Equal(1.5, tr.ResultR, 6);
        }
    }
}
