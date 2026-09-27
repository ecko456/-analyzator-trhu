using System;

namespace ReversalConfirmation.Core
{
    /// <summary>
    /// One closed footprint bar. Volumes are stored densely per tick from Low (index 0) to High.
    /// Bid = aggressive sells (traded on the bid), Ask = aggressive buys (traded on the offer).
    /// </summary>
    public sealed class Bar
    {
        public int Index;
        public DateTime TimeUtc;
        public double Open, High, Low, Close;
        public double Tick;
        public double[] Bid;
        public double[] Ask;
        public double Volume;
        public double Delta;
        public int PocIndex;

        private OBar _up, _down;

        public int Levels => Bid.Length;
        public double Poc => Low + PocIndex * Tick;
        public double Range => High - Low;
        public double PriceAt(int i) => Low + i * Tick;

        /// <summary>Creates a bar from sparse price levels. Prices are snapped to the tick grid.</summary>
        public static Bar Create(int index, DateTime timeUtc, double open, double high, double low, double close, double tick,
            double[] prices, double[] bids, double[] asks, int count)
        {
            if (tick <= 0) throw new ArgumentOutOfRangeException(nameof(tick));

            // levels may extend beyond OHLC by rounding noise; take the envelope
            double lo = low, hi = high;
            for (int i = 0; i < count; i++)
            {
                if (prices[i] < lo) lo = prices[i];
                if (prices[i] > hi) hi = prices[i];
            }

            lo = Math.Round(lo / tick) * tick;
            hi = Math.Round(hi / tick) * tick;
            int n = (int)Math.Round((hi - lo) / tick) + 1;
            if (n < 1) n = 1;

            var b = new Bar
            {
                Index = index, TimeUtc = timeUtc, Tick = tick,
                Open = open, High = hi, Low = lo, Close = close,
                Bid = new double[n], Ask = new double[n]
            };

            for (int i = 0; i < count; i++)
            {
                int k = (int)Math.Round((prices[i] - lo) / tick);
                if (k < 0 || k >= n) continue;
                b.Bid[k] += bids[i];
                b.Ask[k] += asks[i];
            }

            b.Finish();
            return b;
        }

        internal void Finish()
        {
            double v = 0, d = 0, best = -1;
            int poc = 0;
            double mid = (Open + Close) / 2;
            for (int i = 0; i < Bid.Length; i++)
            {
                double lv = Bid[i] + Ask[i];
                v += lv;
                d += Ask[i] - Bid[i];
                // tie-break toward the body centre, which is how footprint charts usually resolve equal POCs
                if (lv > best || (lv == best && Math.Abs(PriceAt(i) - mid) < Math.Abs(PriceAt(poc) - mid)))
                {
                    best = lv;
                    poc = i;
                }
            }
            Volume = v;
            Delta = d;
            PocIndex = poc;
        }

        public void Shift(double offset)
        {
            Open += offset; High += offset; Low += offset; Close += offset;
            _up = _down = null;
        }

        /// <summary>View in which the reversal direction is always "up" (bullish logic works for both sides).</summary>
        public OBar Oriented(int dir) => dir > 0 ? (_up ??= OBar.From(this, 1)) : (_down ??= OBar.From(this, -1));
    }

    /// <summary>
    /// Direction-normalised bar. For dir = -1 prices are negated so that a bearish reversal at a high
    /// becomes a bullish reversal at a low; "Against" is the aggressive volume against the reversal
    /// (sellers for a bullish reversal, buyers for a bearish one), "With" the aggressive volume in its favour.
    /// Index 0 is always the extreme the reversal starts from.
    /// </summary>
    public sealed class OBar
    {
        public int Dir;
        public int First, Last;           // source bar indexes (Last == First for a single bar)
        public double O, H, L, C;
        public double Tick;
        public double[] Against;
        public double[] With;
        public double Volume, Delta;
        public int PocIndex;

        public int Levels => Against.Length;
        public double Poc => L + PocIndex * Tick;
        public double Range => H - L;
        public double PriceAt(int i) => L + i * Tick;
        public double LevelVolume(int i) => Against[i] + With[i];
        public int Count => Last - First + 1;

        /// <summary>Close location value 0..1 (1 = close at the high).</summary>
        public double Clv => Range > 0 ? (C - L) / Range : 0.5;

        public static OBar From(Bar b, int dir)
        {
            int n = b.Levels;
            var o = new OBar { Dir = dir, First = b.Index, Last = b.Index, Tick = b.Tick, Against = new double[n], With = new double[n] };
            if (dir > 0)
            {
                o.O = b.Open; o.H = b.High; o.L = b.Low; o.C = b.Close;
                Array.Copy(b.Bid, o.Against, n);
                Array.Copy(b.Ask, o.With, n);
                o.PocIndex = b.PocIndex;
            }
            else
            {
                o.O = -b.Open; o.H = -b.Low; o.L = -b.High; o.C = -b.Close;
                for (int i = 0; i < n; i++)
                {
                    o.Against[i] = b.Ask[n - 1 - i];
                    o.With[i] = b.Bid[n - 1 - i];
                }
                o.PocIndex = n - 1 - b.PocIndex;
            }
            o.Volume = b.Volume;
            o.Delta = dir * b.Delta;
            return o;
        }

        /// <summary>Merges consecutive oriented bars into one candle (Open of first, Close of last, summed levels).</summary>
        public static OBar Merge(OBar[] bars, int from, int to)
        {
            if (from == to) return bars[from];
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = from; i <= to; i++)
            {
                if (bars[i].L < lo) lo = bars[i].L;
                if (bars[i].H > hi) hi = bars[i].H;
            }
            var t = bars[from].Tick;
            int n = (int)Math.Round((hi - lo) / t) + 1;
            var m = new OBar
            {
                Dir = bars[from].Dir, First = bars[from].First, Last = bars[to].Last, Tick = t,
                O = bars[from].O, C = bars[to].C, H = hi, L = lo,
                Against = new double[n], With = new double[n]
            };
            for (int i = from; i <= to; i++)
            {
                var b = bars[i];
                int off = (int)Math.Round((b.L - lo) / t);
                for (int k = 0; k < b.Levels; k++)
                {
                    m.Against[off + k] += b.Against[k];
                    m.With[off + k] += b.With[k];
                }
                m.Volume += b.Volume;
                m.Delta += b.Delta;
            }
            double best = -1, mid = (m.O + m.C) / 2;
            for (int k = 0; k < n; k++)
            {
                double v = m.Against[k] + m.With[k];
                if (v > best || (v == best && Math.Abs(m.PriceAt(k) - mid) < Math.Abs(m.PriceAt(m.PocIndex) - mid)))
                {
                    best = v;
                    m.PocIndex = k;
                }
            }
            return m;
        }

        /// <summary>Value area (default 70 %) around the POC, returned as oriented (low, high) prices.</summary>
        public (double val, double vah) ValueArea(double share = 0.7)
        {
            int lo = PocIndex, hi = PocIndex;
            double acc = LevelVolume(PocIndex), target = Volume * share;
            while (acc < target && (lo > 0 || hi < Levels - 1))
            {
                double up = hi < Levels - 1 ? LevelVolume(hi + 1) : -1;
                double dn = lo > 0 ? LevelVolume(lo - 1) : -1;
                if (up >= dn) { hi++; acc += up; }
                else { lo--; acc += dn; }
            }
            return (PriceAt(lo), PriceAt(hi));
        }

        /// <summary>Price in real (non-oriented) terms.</summary>
        public double Real(double orientedPrice) => Dir * orientedPrice;
    }
}
