using System;
using System.Collections.Generic;

namespace ReversalConfirmation.Core
{
    /// <summary>Fixed-size FIFO with O(1) mean / std.</summary>
    public sealed class RollingStats
    {
        private readonly double[] _buf;
        private int _pos, _count;
        private double _sum, _sumSq;

        public RollingStats(int capacity) => _buf = new double[Math.Max(2, capacity)];

        public int Count => _count;

        public void Add(double x)
        {
            if (_count == _buf.Length)
            {
                var old = _buf[_pos];
                _sum -= old;
                _sumSq -= old * old;
            }
            else _count++;
            _buf[_pos] = x;
            _pos = (_pos + 1) % _buf.Length;
            _sum += x;
            _sumSq += x * x;
        }

        public double Mean => _count > 0 ? _sum / _count : 0;

        public double Std
        {
            get
            {
                if (_count < 2) return 0;
                var m = Mean;
                var v = _sumSq / _count - m * m;
                return v > 0 ? Math.Sqrt(v * _count / (_count - 1)) : 0;
            }
        }

        /// <summary>Percentile rank 0..100 of x among the stored values (linear scan; windows are small).</summary>
        public double PercentileRank(double x)
        {
            if (_count == 0) return 50;
            int below = 0, equal = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_buf[i] < x) below++;
                else if (_buf[i] == x) equal++;
            }
            return 100.0 * (below + 0.5 * equal) / _count;
        }

        public void Clear()
        {
            _pos = _count = 0;
            _sum = _sumSq = 0;
        }
    }

    /// <summary>Sliding window kept sorted for O(log n) percentile queries.</summary>
    public sealed class SortedWindow
    {
        private readonly int _capacity;
        private readonly Queue<double> _fifo;
        private readonly List<double> _sorted;

        public SortedWindow(int capacity)
        {
            _capacity = Math.Max(10, capacity);
            _fifo = new Queue<double>(_capacity + 1);
            _sorted = new List<double>(_capacity + 1);
        }

        public int Count => _sorted.Count;

        public void Add(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return;
            _fifo.Enqueue(x);
            int i = _sorted.BinarySearch(x);
            _sorted.Insert(i < 0 ? ~i : i, x);
            if (_fifo.Count > _capacity)
            {
                var old = _fifo.Dequeue();
                int j = _sorted.BinarySearch(old);
                if (j >= 0) _sorted.RemoveAt(j);
            }
        }

        public double PercentileRank(double x)
        {
            int n = _sorted.Count;
            if (n == 0) return 50;
            int lo = LowerBound(x), hi = UpperBound(x);
            return 100.0 * (lo + 0.5 * (hi - lo)) / n;
        }

        private int LowerBound(double x)
        {
            int lo = 0, hi = _sorted.Count;
            while (lo < hi)
            {
                int m = (lo + hi) >> 1;
                if (_sorted[m] < x) lo = m + 1; else hi = m;
            }
            return lo;
        }

        private int UpperBound(double x)
        {
            int lo = 0, hi = _sorted.Count;
            while (lo < hi)
            {
                int m = (lo + hi) >> 1;
                if (_sorted[m] <= x) lo = m + 1; else hi = m;
            }
            return lo;
        }

        public void Clear()
        {
            _fifo.Clear();
            _sorted.Clear();
        }
    }

    /// <summary>Summary of a distribution used for z-scores and percentile ranks.</summary>
    public struct Baseline
    {
        public double Mean, Std;
        public int Samples;
        public bool TimeOfDay;
        public double[] Values;   // shared scratch buffer, valid until the next query
        public int Count;

        public double Z(double x) => Std > 1e-9 ? (x - Mean) / Std : 0;

        public double Pct(double x)
        {
            if (Count == 0) return 50;
            int below = 0, equal = 0;
            for (int i = 0; i < Count; i++)
            {
                if (Values[i] < x) below++;
                else if (Values[i] == x) equal++;
            }
            return 100.0 * (below + 0.5 * equal) / Count;
        }

        public double Median()
        {
            if (Count == 0) return 0;
            var tmp = new double[Count];
            Array.Copy(Values, tmp, Count);
            Array.Sort(tmp);
            return Count % 2 == 1 ? tmp[Count / 2] : 0.5 * (tmp[Count / 2 - 1] + tmp[Count / 2]);
        }
    }

    /// <summary>
    /// Time-of-day baseline: for every intraday slot keep the values of the last D sessions.
    /// A query pools the slot with its neighbours (default ±1) for stability and falls back to a
    /// rolling window of recent bars when there is not enough history (or the chart is not time based).
    /// </summary>
    public sealed class TodStats
    {
        private readonly int _days, _neighbors, _minSamples;
        private readonly Dictionary<int, Ring> _slots = new Dictionary<int, Ring>();
        private readonly RollingStats _fallback;
        private readonly double[] _scratchTod;
        private readonly double[] _scratchFb;
        private readonly Queue<double> _fbValues;
        private readonly int _fbCapacity;

        private sealed class Ring
        {
            public readonly double[] V;
            public readonly long[] Session;
            public int Pos, Count;
            public Ring(int n) { V = new double[n]; Session = new long[n]; }
        }

        public TodStats(int days, int neighbors, int minSamples, int fallbackBars)
        {
            _days = Math.Max(1, days);
            _neighbors = Math.Max(0, neighbors);
            _minSamples = Math.Max(3, minSamples);
            _fbCapacity = Math.Max(10, fallbackBars);
            _fallback = new RollingStats(_fbCapacity);
            _fbValues = new Queue<double>(_fbCapacity + 1);
            _scratchTod = new double[_days * (2 * _neighbors + 1)];
            _scratchFb = new double[_fbCapacity];
        }

        /// <summary>Baseline for a value in <paramref name="slot"/> of session <paramref name="session"/> (history only).</summary>
        public Baseline Query(int slot, long session)
        {
            var b = new Baseline();
            if (slot >= 0)
            {
                int n = 0;
                for (int s = slot - _neighbors; s <= slot + _neighbors; s++)
                {
                    if (!_slots.TryGetValue(s, out var r)) continue;
                    for (int i = 0; i < r.Count; i++)
                        if (r.Session[i] != session) _scratchTod[n++] = r.V[i];
                }
                if (n >= _minSamples)
                {
                    Fill(ref b, _scratchTod, n);
                    b.TimeOfDay = true;
                    return b;
                }
            }

            int k = 0;
            foreach (var v in _fbValues) _scratchFb[k++] = v;
            Fill(ref b, _scratchFb, k);
            return b;
        }

        private static void Fill(ref Baseline b, double[] vals, int n)
        {
            double s = 0, ss = 0;
            for (int i = 0; i < n; i++) { s += vals[i]; ss += vals[i] * vals[i]; }
            b.Values = vals;
            b.Count = n;
            b.Samples = n;
            if (n > 0)
            {
                b.Mean = s / n;
                var var = ss / n - b.Mean * b.Mean;
                b.Std = n > 1 && var > 0 ? Math.Sqrt(var * n / (n - 1)) : 0;
            }
        }

        public void Add(int slot, long session, double value)
        {
            if (slot >= 0)
            {
                if (!_slots.TryGetValue(slot, out var r))
                    _slots[slot] = r = new Ring(_days);
                // one value per session per slot: a second bar in the same slot (odd timeframes) replaces it
                int last = (r.Pos - 1 + _days) % _days;
                if (r.Count > 0 && r.Session[last] == session)
                    r.V[last] = value;
                else
                {
                    r.V[r.Pos] = value;
                    r.Session[r.Pos] = session;
                    r.Pos = (r.Pos + 1) % _days;
                    if (r.Count < _days) r.Count++;
                }
            }

            _fallback.Add(value);
            _fbValues.Enqueue(value);
            if (_fbValues.Count > _fbCapacity) _fbValues.Dequeue();
        }

        public void Clear()
        {
            _slots.Clear();
            _fallback.Clear();
            _fbValues.Clear();
        }
    }

    /// <summary>Wilder ATR.</summary>
    public sealed class Atr
    {
        private readonly int _period;
        private int _n;
        private double _prevClose = double.NaN;
        public double Value { get; private set; }

        public Atr(int period) => _period = Math.Max(1, period);

        public void Add(double high, double low, double close)
        {
            double tr = high - low;
            if (!double.IsNaN(_prevClose))
                tr = Math.Max(tr, Math.Max(Math.Abs(high - _prevClose), Math.Abs(low - _prevClose)));
            _prevClose = close;
            _n++;
            Value = _n <= _period ? Value + (tr - Value) / _n : Value + (tr - Value) / _period;
        }

        public void Shift(double offset)
        {
            if (!double.IsNaN(_prevClose)) _prevClose += offset;
        }

        public void Clear()
        {
            _n = 0;
            Value = 0;
            _prevClose = double.NaN;
        }
    }

    public static class MathUtil
    {
        public static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

        /// <summary>Linear ramp: 0 at <paramref name="zero"/>, 1 at <paramref name="one"/> (works for either order).</summary>
        public static double Ramp(double x, double zero, double one)
        {
            if (one == zero) return x >= one ? 1 : 0;
            return Clamp01((x - zero) / (one - zero));
        }

        /// <summary>Spearman rank correlation of two short series (average ranks for ties).</summary>
        public static double Spearman(double[] x, double[] y, int n)
        {
            if (n < 3) return 0;
            var rx = Ranks(x, n);
            var ry = Ranks(y, n);
            double mx = 0, my = 0;
            for (int i = 0; i < n; i++) { mx += rx[i]; my += ry[i]; }
            mx /= n; my /= n;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = rx[i] - mx, dy = ry[i] - my;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
        }

        private static double[] Ranks(double[] v, int n)
        {
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => v[a].CompareTo(v[b]));
            var r = new double[n];
            int k = 0;
            while (k < n)
            {
                int j = k;
                while (j + 1 < n && v[idx[j + 1]] == v[idx[k]]) j++;
                double avg = (k + j) / 2.0 + 1;
                for (int m = k; m <= j; m++) r[idx[m]] = avg;
                k = j + 1;
            }
            return r;
        }

        public static double RoundToTick(double price, double tick) => Math.Round(price / tick) * tick;
    }
}
