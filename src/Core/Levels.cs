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
        Manual
    }

    public struct RefLevel
    {
        public LevelKind Kind;
        public double Price;
        public double Weight;

        public RefLevel(LevelKind kind, double price, double weight)
        {
            Kind = kind;
            Price = price;
            Weight = weight;
        }

        public string Name => LevelNames.Get(Kind);
    }

    public static class LevelNames
    {
        public static string Get(LevelKind k)
        {
            switch (k)
            {
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
        private Profile _profile = new Profile();
        private bool _profileOpen;

        // --- previous day profile ---
        private double _pdH = double.NaN, _pdL, _pdPoc, _pdVah, _pdVal;

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
            for (int i = 0; i < b.Levels; i++)
            {
                double v = b.Bid[i] + b.Ask[i];
                if (v <= 0) continue;
                double p = b.PriceAt(i);
                if (vwapOn) _vwap.Add(p, v);
                if (profileOn) _profile.Add((long)Math.Round(p / _tick), v);
            }
            if (profileOn) _profileOpen = true;
            if (_s.VwapSession == SessionMode.Rth && !t.IsRth && _rthStarted && _vwap.Volume > 0 && double.IsNaN(_closedRthVwap))
                _closedRthVwap = _vwap.Value;
            if (_vwap.Volume > 0) _vwapHistory.Add(_vwap.Value);

            _recent.Add((b.Index, b.High, b.Low, t.SessionId));
            int keep = Math.Max(_s.PoolLookback, 2 * _s.SwingStrength + 1) + 2;
            if (_recent.Count > keep) _recent.RemoveAt(0);
            UpdateSwings(b);
        }

        private double _closedRthVwap = double.NaN;

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
                if (_profileOpen && _profile.Total > 0)
                {
                    var r = _profile.Compute(0.7);
                    _pdH = r.high * _tick; _pdL = r.low * _tick; _pdPoc = r.poc * _tick; _pdVah = r.vah * _tick; _pdVal = r.val * _tick;
                }
            }
            _session = session;
            _rthStarted = _firstRthDone = _or15Done = _or30Done = _ibDone = false;
            _onHigh = double.MinValue;
            _onLow = double.MaxValue;
            _closedRthVwap = double.NaN;
            _vwap = new Vwap();
            _vwapHistory.Clear();
            _profile = new Profile();
            _profileOpen = false;
            _swingHighs.Clear();
            _swingLows.Clear();
        }

        /// <summary>Contract roll in a continuous back-test series: shift everything by the roll gap.</summary>
        public void Shift(double offset)
        {
            if (!double.IsNaN(_prevVwap)) _prevVwap += offset;
            if (!double.IsNaN(_pdH)) { _pdH += offset; _pdL += offset; _pdPoc += offset; _pdVah += offset; _pdVal += offset; }
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

        private sealed class Profile
        {
            private readonly Dictionary<long, double> _vol = new Dictionary<long, double>();
            public double Total;

            public void Add(long key, double v)
            {
                _vol.TryGetValue(key, out var x);
                _vol[key] = x + v;
                Total += v;
            }

            public (long high, long low, long poc, long vah, long val) Compute(double share)
            {
                long lo = long.MaxValue, hi = long.MinValue, poc = 0;
                double best = -1;
                foreach (var kv in _vol)
                {
                    if (kv.Key < lo) lo = kv.Key;
                    if (kv.Key > hi) hi = kv.Key;
                    if (kv.Value > best) { best = kv.Value; poc = kv.Key; }
                }
                double acc = best, target = Total * share;
                long a = poc, b = poc;
                while (acc < target && (a > lo || b < hi))
                {
                    double up = b < hi ? Get(b + 1) : -1;
                    double dn = a > lo ? Get(a - 1) : -1;
                    if (up >= dn) { b++; acc += up; }
                    else { a--; acc += dn; }
                }
                return (hi, lo, poc, b, a);
            }

            private double Get(long k) => _vol.TryGetValue(k, out var v) ? v : 0;
        }
    }
}
