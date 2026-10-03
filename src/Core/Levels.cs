using System;
using System.Collections.Generic;

namespace ReversalConfirmation.Core
{
    public enum LevelKind
    {
        PrevVwap,
        SessionVwap,
        VwapBand1,
        VwapBand2,
        FirstRthBarHigh,
        FirstRthBarLow,
        OpeningRange15High,
        OpeningRange15Low,
        OpeningRange30High,
        OpeningRange30Low,
        InitialBalanceHigh,
        InitialBalanceLow,
        RthOpen,
        PrevDayHigh,
        PrevDayLow,
        PrevDayPoc,
        PrevDayVah,
        PrevDayVal,
        OvernightHigh,
        OvernightLow,
        EqualLows,
        EqualHighs,
        SwingHigh,
        SwingLow,
        Manual,
        DevVah,
        DevVal,
        DevPoc,
        WeekVah,
        WeekVal,
        WeekPoc,
        PrevWeekVah,
        PrevWeekVal,
        PrevWeekPoc,
        NakedVah,
        NakedVal,
        NakedPoc
    }

    public struct RefLevel
    {
        public LevelKind Kind;
        public double Price;
        public double Weight;
        /// <summary>Naked levels: sessions since the profile they come from (2 = the day before yesterday).</summary>
        public int Age;

        public RefLevel(LevelKind kind, double price, double weight, int age = 0)
        {
            Kind = kind;
            Price = price;
            Weight = weight;
            Age = age;
        }

        public string Name => LevelNames.Get(Kind, Age);

        /// <summary>Level from a market / volume profile (value area edge or POC of any profile).</summary>
        public bool IsProfile => LevelNames.IsProfile(Kind);
        public bool IsNaked => Kind == LevelKind.NakedVah || Kind == LevelKind.NakedVal || Kind == LevelKind.NakedPoc;
    }

    public static class LevelNames
    {
        public static bool IsProfile(LevelKind k) =>
            k == LevelKind.PrevDayPoc || k == LevelKind.PrevDayVah || k == LevelKind.PrevDayVal || k >= LevelKind.DevVah;

        public static string Get(LevelKind k, int age = 0)
        {
            switch (k)
            {
                case LevelKind.DevVah: return "Dnešní VAH (developing)";
                case LevelKind.DevVal: return "Dnešní VAL (developing)";
                case LevelKind.DevPoc: return "Dnešní POC (developing)";
                case LevelKind.WeekVah: return "VAH tohoto týdne";
                case LevelKind.WeekVal: return "VAL tohoto týdne";
                case LevelKind.WeekPoc: return "POC tohoto týdne";
                case LevelKind.PrevWeekVah: return "VAH předchozího týdne";
                case LevelKind.PrevWeekVal: return "VAL předchozího týdne";
                case LevelKind.PrevWeekPoc: return "POC předchozího týdne";
                case LevelKind.NakedVah: return $"Nahý VAH (před {age} dny, netestovaný)";
                case LevelKind.NakedVal: return $"Nahý VAL (před {age} dny, netestovaný)";
                case LevelKind.NakedPoc: return $"Nahý POC (před {age} dny, netestovaný)";
                case LevelKind.PrevVwap: return "VWAP předchozí session";
                case LevelKind.SessionVwap: return "Session VWAP";
                case LevelKind.VwapBand1: return "VWAP ±1σ";
                case LevelKind.VwapBand2: return "VWAP ±2σ";
                case LevelKind.FirstRthBarHigh: return "High 1. RTH svíčky";
                case LevelKind.FirstRthBarLow: return "Low 1. RTH svíčky";
                case LevelKind.OpeningRange15High: return "OR15 high";
                case LevelKind.OpeningRange15Low: return "OR15 low";
                case LevelKind.OpeningRange30High: return "OR30 high";
                case LevelKind.OpeningRange30Low: return "OR30 low";
                case LevelKind.InitialBalanceHigh: return "IB high";
                case LevelKind.InitialBalanceLow: return "IB low";
                case LevelKind.RthOpen: return "RTH open";
                case LevelKind.PrevDayHigh: return "Předchozí den high";
                case LevelKind.PrevDayLow: return "Předchozí den low";
                case LevelKind.PrevDayPoc: return "Předchozí den POC";
                case LevelKind.PrevDayVah: return "Předchozí den VAH";
                case LevelKind.PrevDayVal: return "Předchozí den VAL";
                case LevelKind.OvernightHigh: return "Overnight high";
                case LevelKind.OvernightLow: return "Overnight low";
                case LevelKind.EqualLows: return "Equal lows (liquidity pool)";
                case LevelKind.EqualHighs: return "Equal highs (liquidity pool)";
                case LevelKind.SwingHigh: return "Swing high dne";
                case LevelKind.SwingLow: return "Swing low dne";
                case LevelKind.Manual: return "Ruční úroveň";
                default: return k.ToString();
            }
        }
    }

    /// <summary>
    /// Tracks the session reference levels. <see cref="Snapshot"/> returns the levels that were known
    /// before the bar being evaluated (no look-ahead); <see cref="Update"/> then folds that bar in.
    /// </summary>
    public sealed class LevelTracker
    {
        private readonly EngineSettings _s;
        private readonly double _tick;
        private readonly List<double> _manual;

        // --- current session ---
        private long _session = long.MinValue;
        private bool _rthStarted;
        private double _rthOpen;
        private double _firstRthHigh, _firstRthLow;
        private bool _firstRthDone;
        private double _or15H, _or15L, _or30H, _or30L, _ibH, _ibL;
        private bool _or15Done, _or30Done, _ibDone;
        private double _onHigh = double.MinValue, _onLow = double.MaxValue;
        private Vwap _vwap = new Vwap();
        private double _prevVwap = double.NaN;
        private readonly List<double> _vwapHistory = new List<double>();
        private Profile _profile = new Profile();      // daily profile (RTH or ETH, see PriorDayProfile)
        private Profile _ethProfile = new Profile();   // Globex session so far: today's developing VA before the RTH open
        private bool _profileOpen, _dayFinal;

        // --- previous day profile ---
        private double _pdH = double.NaN, _pdL, _pdPoc, _pdVah, _pdVal;
        private Profile.Result _today;                  // today's daily profile once it closed (RTH close / session end)

        // --- weekly profiles ---
        private Profile _week = new Profile();
        private long _weekId = long.MinValue;
        private int _weekSessions;                     // completed sessions in the current week
        private double _pwVah = double.NaN, _pwVal, _pwPoc;

        // --- naked (untested) value area edges and POCs of earlier days ---
        private readonly List<(LevelKind kind, double price, int sessionNo)> _naked = new List<(LevelKind, double, int)>();
        private int _sessionNo;

        // --- recent bars for pools and swings ---
        private readonly List<(int idx, double hi, double lo, long session)> _recent = new List<(int, double, double, long)>();
        private readonly List<(int idx, double price)> _swingHighs = new List<(int, double)>();
        private readonly List<(int idx, double price)> _swingLows = new List<(int, double)>();

        private readonly double _barMinutes;

        public LevelTracker(EngineSettings s, double tick, double barMinutes)
        {
            _s = s;
            _tick = tick;
            _barMinutes = barMinutes;
            _manual = s.ParseManualLevels();
        }

        public double SessionVwap => _vwap.Value;
        public double SessionVwapStd => _vwap.Std;
        public bool HasVwap => _vwap.Volume > 0;
        public bool RthStarted => _rthStarted;

        /// <summary>VWAP slope over the last <paramref name="bars"/> bars (price units), NaN if unknown.</summary>
        public double VwapSlope(int bars)
        {
            int n = _vwapHistory.Count;
            if (n <= bars) return double.NaN;
            return _vwapHistory[n - 1] - _vwapHistory[n - 1 - bars];
        }

        public void Snapshot(List<RefLevel> dst, double atr, long session)
        {
            dst.Clear();
            var s = _s;

            if (!double.IsNaN(_prevVwap) && s.WPrevVwap > 0)
                dst.Add(new RefLevel(LevelKind.PrevVwap, _prevVwap, s.WPrevVwap));

            if (_vwap.Volume > 0 && session == _session && VwapActive())
            {
                if (s.WSessionVwap > 0) dst.Add(new RefLevel(LevelKind.SessionVwap, _vwap.Value, s.WSessionVwap));
                var sd = _vwap.Std;
                if (s.WVwapBands > 0 && sd > 0)
                {
                    dst.Add(new RefLevel(LevelKind.VwapBand1, _vwap.Value + sd, s.WVwapBands));
                    dst.Add(new RefLevel(LevelKind.VwapBand1, _vwap.Value - sd, s.WVwapBands));
                    dst.Add(new RefLevel(LevelKind.VwapBand2, _vwap.Value + 2 * sd, s.WVwapBands));
                    dst.Add(new RefLevel(LevelKind.VwapBand2, _vwap.Value - 2 * sd, s.WVwapBands));
                }
            }

            if (session == _session && _rthStarted)
            {
                if (_firstRthDone && s.WFirstRthBar > 0)
                {
                    dst.Add(new RefLevel(LevelKind.FirstRthBarHigh, _firstRthHigh, s.WFirstRthBar));
                    dst.Add(new RefLevel(LevelKind.FirstRthBarLow, _firstRthLow, s.WFirstRthBar));
                }
                if (s.WOpeningRange > 0)
                {
                    if (_or15Done)
                    {
                        dst.Add(new RefLevel(LevelKind.OpeningRange15High, _or15H, s.WOpeningRange));
                        dst.Add(new RefLevel(LevelKind.OpeningRange15Low, _or15L, s.WOpeningRange));
                    }
                    if (_or30Done)
                    {
                        dst.Add(new RefLevel(LevelKind.OpeningRange30High, _or30H, s.WOpeningRange));
                        dst.Add(new RefLevel(LevelKind.OpeningRange30Low, _or30L, s.WOpeningRange));
                    }
                    if (_ibDone)
                    {
                        dst.Add(new RefLevel(LevelKind.InitialBalanceHigh, _ibH, s.WOpeningRange));
                        dst.Add(new RefLevel(LevelKind.InitialBalanceLow, _ibL, s.WOpeningRange));
                    }
                }
                if (s.WRthOpen > 0) dst.Add(new RefLevel(LevelKind.RthOpen, _rthOpen, s.WRthOpen));
                if (s.WOvernight > 0 && _onHigh > double.MinValue)
                {
                    dst.Add(new RefLevel(LevelKind.OvernightHigh, _onHigh, s.WOvernight));
                    dst.Add(new RefLevel(LevelKind.OvernightLow, _onLow, s.WOvernight));
                }
            }

            if (!double.IsNaN(_pdH))
            {
                if (s.WPrevDayHl > 0)
                {
                    dst.Add(new RefLevel(LevelKind.PrevDayHigh, _pdH, s.WPrevDayHl));
                    dst.Add(new RefLevel(LevelKind.PrevDayLow, _pdL, s.WPrevDayHl));
                }
                if (s.WPrevDayPoc > 0) dst.Add(new RefLevel(LevelKind.PrevDayPoc, _pdPoc, s.WPrevDayPoc));
                if (s.WPrevDayVa > 0)
                {
                    dst.Add(new RefLevel(LevelKind.PrevDayVah, _pdVah, s.WPrevDayVa));
                    dst.Add(new RefLevel(LevelKind.PrevDayVal, _pdVal, s.WPrevDayVa));
                }
            }

            AddProfileLevels(dst);

            if (s.WLiquidityPool > 0) AddPools(dst, atr);

            if (s.WSwing > 0)
            {
                foreach (var sw in _swingHighs) dst.Add(new RefLevel(LevelKind.SwingHigh, sw.price, s.WSwing));
                foreach (var sw in _swingLows) dst.Add(new RefLevel(LevelKind.SwingLow, sw.price, s.WSwing));
            }

            if (s.WManual > 0)
                foreach (var m in _manual) dst.Add(new RefLevel(LevelKind.Manual, m, s.WManual));
        }

        private bool VwapActive() => _s.VwapSession == SessionMode.Eth || _rthStarted;

        /// <summary>
        /// Market / volume profile levels: today's developing value area, this week's and last week's value area and
        /// naked VAH / VAL / POC of earlier days that price has not traded at since their session closed.
        /// </summary>
        private void AddProfileLevels(List<RefLevel> dst)
        {
            var s = _s;
            if (s.WDevVa > 0)
            {
                // RTH profile once RTH runs (and after it closed), the Globex session profile before the open
                var dev = s.PriorDayProfile == SessionMode.Eth || _rthStarted ? _profile : _ethProfile;
                if (dev.Total > 0 && dev.ElapsedMinutes >= s.DevVaMinMinutes)
                    AddVa(dst, dev.Compute(s.ValueAreaShare), LevelKind.DevVah, LevelKind.DevVal, LevelKind.DevPoc, s.WDevVa);
            }
            if (s.WWeekVa > 0 && _weekSessions >= 1 && _week.Total > 0)
                AddVa(dst, _week.Compute(s.ValueAreaShare), LevelKind.WeekVah, LevelKind.WeekVal, LevelKind.WeekPoc, s.WWeekVa);
            if (s.WPrevWeekVa > 0 && !double.IsNaN(_pwVah))
            {
                dst.Add(new RefLevel(LevelKind.PrevWeekVah, _pwVah, s.WPrevWeekVa));
                dst.Add(new RefLevel(LevelKind.PrevWeekVal, _pwVal, s.WPrevWeekVa));
                dst.Add(new RefLevel(LevelKind.PrevWeekPoc, _pwPoc, s.WPrevWeekVa));
            }
            if (s.WNakedVa > 0)
                foreach (var n in _naked)
                {
                    int age = _sessionNo - n.sessionNo;
                    // age 1 = the previous day, already a level of its own (PrevDayVah/Val/Poc)
                    if (age >= 2 && age <= s.NakedMaxDays) dst.Add(new RefLevel(n.kind, n.price, s.WNakedVa, age));
                }
        }

        private void AddVa(List<RefLevel> dst, Profile.Result r, LevelKind vah, LevelKind val, LevelKind poc, double w)
        {
            dst.Add(new RefLevel(vah, r.Vah * _tick, w));
            dst.Add(new RefLevel(val, r.Val * _tick, w));
            dst.Add(new RefLevel(poc, r.Poc * _tick, w));
        }

        /// <summary>
        /// Equal lows / highs: at least two bars within the lookback whose extremes lie inside a narrow band and
        /// which have not been traded through since the first touch (resting stops = liquidity).
        /// </summary>
        private void AddPools(List<RefLevel> dst, double atr)
        {
            int n = _recent.Count;
            if (n < 2) return;
            double band = Math.Max(_s.PoolBandTicks * _tick, _s.PoolBandAtr * atr);
            int start = Math.Max(0, n - _s.PoolLookback);

            double lastLow = double.NaN, lastHigh = double.NaN;
            for (int j = n - 1; j >= start; j--)
            {
                // lows
                double p = _recent[j].lo;
                if (double.IsNaN(lastLow) || Math.Abs(p - lastLow) > band)
                {
                    int members = 0, first = j;
                    double min = p;
                    for (int k = start; k < n; k++)
                    {
                        if (Math.Abs(_recent[k].lo - p) <= band)
                        {
                            members++;
                            if (k < first) first = k;
                            if (_recent[k].lo < min) min = _recent[k].lo;
                        }
                    }
                    if (members >= 2 && Intact(first, min - band, true))
                    {
                        if (!ContainsNear(dst, LevelKind.EqualLows, min, band))
                            dst.Add(new RefLevel(LevelKind.EqualLows, min, _s.WLiquidityPool));
                        lastLow = p;
                    }
                }
                // highs
                p = _recent[j].hi;
                if (double.IsNaN(lastHigh) || Math.Abs(p - lastHigh) > band)
                {
                    int members = 0, first = j;
                    double max = p;
                    for (int k = start; k < n; k++)
                    {
                        if (Math.Abs(_recent[k].hi - p) <= band)
                        {
                            members++;
                            if (k < first) first = k;
                            if (_recent[k].hi > max) max = _recent[k].hi;
                        }
                    }
                    if (members >= 2 && Intact(first, max + band, false))
                    {
                        if (!ContainsNear(dst, LevelKind.EqualHighs, max, band))
                            dst.Add(new RefLevel(LevelKind.EqualHighs, max, _s.WLiquidityPool));
                        lastHigh = p;
                    }
                }
            }
        }

        private bool Intact(int from, double limit, bool lows)
        {
            for (int k = from; k < _recent.Count; k++)
            {
                if (lows && _recent[k].lo < limit) return false;
                if (!lows && _recent[k].hi > limit) return false;
            }
            return true;
        }

        private static bool ContainsNear(List<RefLevel> list, LevelKind kind, double price, double band)
        {
            foreach (var l in list)
                if (l.Kind == kind && Math.Abs(l.Price - price) <= band) return true;
            return false;
        }

        public void Update(Bar b, BarTime t)
        {
            if (t.SessionId != _session) NewSession(t.SessionId);

            if (t.IsRth && !_rthStarted)
            {
                _rthStarted = true;
                _rthOpen = b.Open;
                _firstRthHigh = b.High;
                _firstRthLow = b.Low;
                _or15H = _or30H = _ibH = b.High;
                _or15L = _or30L = _ibL = b.Low;
                if (_s.VwapSession == SessionMode.Rth) _vwap = new Vwap();
            }

            if (_rthStarted && t.IsRth)
            {
                double m = t.MinutesFromRth;
                // a window is complete once a bar that ends at/after its end has closed
                double end = m + Math.Max(_barMinutes, 0);
                if (m < 15) { _or15H = Math.Max(_or15H, b.High); _or15L = Math.Min(_or15L, b.Low); }
                if (m < 30) { _or30H = Math.Max(_or30H, b.High); _or30L = Math.Min(_or30L, b.Low); }
                if (m < 60) { _ibH = Math.Max(_ibH, b.High); _ibL = Math.Min(_ibL, b.Low); }
                if (end >= 15 - 1e-6) _or15Done = true;
                if (end >= 30 - 1e-6) _or30Done = true;
                if (end >= 60 - 1e-6) _ibDone = true;
                _firstRthDone = true;
            }

            if (!_rthStarted)
            {
                _onHigh = Math.Max(_onHigh, b.High);
                _onLow = Math.Min(_onLow, b.Low);
            }

            bool vwapOn = _s.VwapSession == SessionMode.Eth || t.IsRth;
            bool profileOn = _s.PriorDayProfile == SessionMode.Eth || t.IsRth;
            if (vwapOn)
                for (int i = 0; i < b.Levels; i++)
                {
                    double v = b.Bid[i] + b.Ask[i];
                    if (v > 0) _vwap.Add(b.PriceAt(i), v);
                }
            var src = _s.ProfileType;
            long period = t.SessionId * 1000 + (long)Math.Floor(t.MinutesFromEth / Math.Max(1, _s.TpoMinutes) + 1e-9);
            if (profileOn)
            {
                _profile.AddBar(b, _tick, src, period, t.MinutesFromEth, _barMinutes);
                _profileOpen = true;
            }
            else if (_profileOpen && !_dayFinal) FinalizeDay();   // RTH closed: from now on its levels can stay untested
            _ethProfile.AddBar(b, _tick, src, period, t.MinutesFromEth, _barMinutes);
            if (_s.WeekProfile == SessionMode.Eth || t.IsRth) _week.AddBar(b, _tick, src, period, t.MinutesFromEth, _barMinutes);
            // a naked level that price traded at is no longer naked
            for (int i = _naked.Count - 1; i >= 0; i--)
                if (b.Low <= _naked[i].price + 1e-9 && b.High >= _naked[i].price - 1e-9) _naked.RemoveAt(i);
            if (_s.VwapSession == SessionMode.Rth && !t.IsRth && _rthStarted && _vwap.Volume > 0 && double.IsNaN(_closedRthVwap))
                _closedRthVwap = _vwap.Value;
            if (_vwap.Volume > 0) _vwapHistory.Add(_vwap.Value);

            _recent.Add((b.Index, b.High, b.Low, t.SessionId));
            int keep = Math.Max(_s.PoolLookback, 2 * _s.SwingStrength + 1) + 2;
            if (_recent.Count > keep) _recent.RemoveAt(0);
            UpdateSwings(b);
        }

        private double _closedRthVwap = double.NaN;

        /// <summary>Today's daily profile is complete: keep its result and start watching its levels for a first touch.</summary>
        private void FinalizeDay()
        {
            _dayFinal = true;
            _today = default;
            if (_profile.Total <= 0) return;
            _today = _profile.Compute(_s.ValueAreaShare);
            _naked.Add((LevelKind.NakedVah, _today.Vah * _tick, _sessionNo));
            _naked.Add((LevelKind.NakedVal, _today.Val * _tick, _sessionNo));
            _naked.Add((LevelKind.NakedPoc, _today.Poc * _tick, _sessionNo));
        }

        private void UpdateSwings(Bar b)
        {
            int st = _s.SwingStrength;
            int n = _recent.Count;
            // drop swings that have been traded through
            _swingLows.RemoveAll(x => b.Low < x.price - _tick * 0.5);
            _swingHighs.RemoveAll(x => b.High > x.price + _tick * 0.5);
            if (n < 2 * st + 1) return;
            int c = n - 1 - st;
            var mid = _recent[c];
            if (mid.session != _session) return;
            bool isLow = true, isHigh = true;
            for (int k = c - st; k <= c + st; k++)
            {
                if (k == c) continue;
                if (_recent[k].session != _session) { isLow = isHigh = false; break; }
                if (k < c)
                {
                    if (_recent[k].lo <= mid.lo) isLow = false;
                    if (_recent[k].hi >= mid.hi) isHigh = false;
                }
                else
                {
                    if (_recent[k].lo < mid.lo) isLow = false;
                    if (_recent[k].hi > mid.hi) isHigh = false;
                }
            }
            if (isLow) { _swingLows.Add((mid.idx, mid.lo)); if (_swingLows.Count > 4) _swingLows.RemoveAt(0); }
            if (isHigh) { _swingHighs.Add((mid.idx, mid.hi)); if (_swingHighs.Count > 4) _swingHighs.RemoveAt(0); }
        }

        private void NewSession(long session)
        {
            if (_session != long.MinValue)
            {
                if (_s.VwapSession == SessionMode.Eth && _vwap.Volume > 0) _prevVwap = _vwap.Value;
                else if (_s.VwapSession == SessionMode.Rth)
                {
                    if (!double.IsNaN(_closedRthVwap)) _prevVwap = _closedRthVwap;
                    else if (_rthStarted && _vwap.Volume > 0) _prevVwap = _vwap.Value;
                }
                if (_profileOpen && !_dayFinal) FinalizeDay();
                if (_dayFinal && _today.Valid)
                {
                    _pdH = _today.High * _tick; _pdL = _today.Low * _tick; _pdPoc = _today.Poc * _tick;
                    _pdVah = _today.Vah * _tick; _pdVal = _today.Val * _tick;
                }
            }
            // CME week: the Sunday evening session belongs to Monday; DateTime.MinValue is a Monday
            long week = session / 7;
            if (week != _weekId)
            {
                if (_weekId != long.MinValue && _week.Total > 0)
                {
                    var w = _week.Compute(_s.ValueAreaShare);
                    _pwVah = w.Vah * _tick; _pwVal = w.Val * _tick; _pwPoc = w.Poc * _tick;
                }
                _week = new Profile();
                _weekId = week;
                _weekSessions = 0;
            }
            else _weekSessions++;
            _sessionNo++;
            _naked.RemoveAll(n => _sessionNo - n.sessionNo > _s.NakedMaxDays);
            _session = session;
            _rthStarted = _firstRthDone = _or15Done = _or30Done = _ibDone = false;
            _onHigh = double.MinValue;
            _onLow = double.MaxValue;
            _closedRthVwap = double.NaN;
            _vwap = new Vwap();
            _vwapHistory.Clear();
            _profile = new Profile();
            _ethProfile = new Profile();
            _profileOpen = _dayFinal = false;
            _swingHighs.Clear();
            _swingLows.Clear();
        }

        /// <summary>Contract roll in a continuous back-test series: shift everything by the roll gap.</summary>
        public void Shift(double offset)
        {
            if (!double.IsNaN(_prevVwap)) _prevVwap += offset;
            if (!double.IsNaN(_pdH)) { _pdH += offset; _pdL += offset; _pdPoc += offset; _pdVah += offset; _pdVal += offset; }
            if (!double.IsNaN(_pwVah)) { _pwVah += offset; _pwVal += offset; _pwPoc += offset; }
            long dk = (long)Math.Round(offset / _tick);
            _profile.Shift(dk);
            _ethProfile.Shift(dk);
            _week.Shift(dk);
            if (_today.Valid) _today = _today.Shifted(dk);
            for (int i = 0; i < _naked.Count; i++) _naked[i] = (_naked[i].kind, _naked[i].price + offset, _naked[i].sessionNo);
            for (int i = 0; i < _recent.Count; i++) _recent[i] = (_recent[i].idx, _recent[i].hi + offset, _recent[i].lo + offset, _recent[i].session);
            for (int i = 0; i < _swingHighs.Count; i++) _swingHighs[i] = (_swingHighs[i].idx, _swingHighs[i].price + offset);
            for (int i = 0; i < _swingLows.Count; i++) _swingLows[i] = (_swingLows[i].idx, _swingLows[i].price + offset);
        }

        private sealed class Vwap
        {
            public double Pv, V, P2v;
            public double Volume => V;
            public double Value => V > 0 ? Pv / V : double.NaN;
            public double Std
            {
                get
                {
                    if (V <= 0) return 0;
                    var m = Pv / V;
                    var var = P2v / V - m * m;
                    return var > 0 ? Math.Sqrt(var) : 0;
                }
            }
            public void Add(double p, double v)
            {
                Pv += p * v;
                P2v += p * p * v;
                V += v;
            }
        }

        /// <summary>
        /// Price profile in ticks. Volume mode adds the traded volume at each price; TPO mode adds one TPO for every price
        /// a 30-minute period traded through (period low to high, as in Market Profile), counted incrementally per bar.
        /// </summary>
        private sealed class Profile
        {
            public struct Result
            {
                public long High, Low, Poc, Vah, Val;
                public bool Valid;
                public Result Shifted(long dk) => new Result { High = High + dk, Low = Low + dk, Poc = Poc + dk, Vah = Vah + dk, Val = Val + dk, Valid = Valid };
            }

            private Dictionary<long, double> _vol = new Dictionary<long, double>();
            public double Total;
            private double _firstMinute = double.NaN, _lastMinute, _barMinutes;
            private long _period = long.MinValue, _pLo, _pHi;

            /// <summary>Minutes of trading the profile covers.</summary>
            public double ElapsedMinutes => double.IsNaN(_firstMinute) ? 0 : _lastMinute - _firstMinute + _barMinutes;

            public void AddBar(Bar b, double tick, ProfileSource src, long period, double minuteOfSession, double barMinutes)
            {
                if (double.IsNaN(_firstMinute) || minuteOfSession < _firstMinute) _firstMinute = minuteOfSession;
                _lastMinute = Math.Max(_lastMinute, minuteOfSession);
                _barMinutes = barMinutes;
                if (src == ProfileSource.Volume)
                {
                    for (int i = 0; i < b.Levels; i++)
                    {
                        double v = b.Bid[i] + b.Ask[i];
                        if (v > 0) Add((long)Math.Round(b.PriceAt(i) / tick), v);
                    }
                    return;
                }
                long lo = (long)Math.Round(b.Low / tick), hi = (long)Math.Round(b.High / tick);
                if (period != _period)
                {
                    _period = period;
                    for (long k = lo; k <= hi; k++) Add(k, 1);
                    _pLo = lo;
                    _pHi = hi;
                    return;
                }
                // same period: only prices the period has not printed yet get a TPO
                for (long k = lo; k < _pLo; k++) Add(k, 1);
                for (long k = _pHi + 1; k <= hi; k++) Add(k, 1);
                _pLo = Math.Min(_pLo, lo);
                _pHi = Math.Max(_pHi, hi);
            }

            private void Add(long key, double v)
            {
                _vol.TryGetValue(key, out var x);
                _vol[key] = x + v;
                Total += v;
            }

            public void Shift(long dk)
            {
                if (dk == 0) return;
                var moved = new Dictionary<long, double>(_vol.Count);
                foreach (var kv in _vol) moved[kv.Key + dk] = kv.Value;
                _vol = moved;
                _pLo += dk;
                _pHi += dk;
            }

            /// <summary>POC and the value area grown from it, one price at a time toward the bigger neighbour.</summary>
            public Result Compute(double share)
            {
                long lo = long.MaxValue, hi = long.MinValue, poc = 0;
                foreach (var k in _vol.Keys)
                {
                    if (k < lo) lo = k;
                    if (k > hi) hi = k;
                }
                double best = -1;
                foreach (var kv in _vol)
                {
                    // ties (common with TPO counts): the price closer to the middle of the range wins
                    if (kv.Value > best || (kv.Value == best && Math.Abs(2 * kv.Key - lo - hi) < Math.Abs(2 * poc - lo - hi))) { best = kv.Value; poc = kv.Key; }
                }
                if (best < 0) return default;
                double acc = best, target = Total * share;
                long a = poc, b = poc;
                while (acc < target && (a > lo || b < hi))
                {
                    double up = b < hi ? Get(b + 1) : -1;
                    double dn = a > lo ? Get(a - 1) : -1;
                    if (up >= dn) { b++; acc += up; }
                    else { a--; acc += dn; }
                }
                return new Result { High = hi, Low = lo, Poc = poc, Vah = b, Val = a, Valid = true };
            }

            private double Get(long k) => _vol.TryGetValue(k, out var v) ? v : 0;
        }
    }
}
