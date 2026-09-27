using System;
using System.Collections.Generic;
using ReversalConfirmation.Core;

namespace ReversalConfirmation.Tests
{
    /// <summary>Builds footprint bars for scenario tests (ES: tick 0.25, M5, times in UTC).</summary>
    public sealed class Synthetic
    {
        public const double Tick = 0.25;
        public readonly List<Bar> Bars = new List<Bar>();
        private readonly Random _rnd;
        private DateTime _t;
        private double _price;

        public Synthetic(int seed, DateTime startUtc, double price)
        {
            _rnd = new Random(seed);
            _t = startUtc;
            _price = price;
        }

        public double Price => _price;
        public DateTime Time => _t;

        /// <summary>Quiet random-walk bars that build the statistical baselines.</summary>
        public void Noise(int n, double stepTicks = 4, double volume = 800, double deltaStd = 120)
        {
            for (int i = 0; i < n; i++)
            {
                double move = Math.Round((_rnd.NextDouble() - 0.5) * 2 * stepTicks) * Tick;
                double open = _price, close = _price + move;
                double lo = Math.Min(open, close) - Math.Round(_rnd.NextDouble() * 3) * Tick;
                double hi = Math.Max(open, close) + Math.Round(_rnd.NextDouble() * 3) * Tick;
                // as in real order flow, delta and price change are correlated
                double delta = Math.Round(0.6 * deltaStd * (move / Tick) / stepTicks * 2 + 0.6 * Gauss() * deltaStd);
                Add(open, hi, lo, close, volume * (0.7 + 0.6 * _rnd.NextDouble()), delta, pocAt: 0.5);
            }
        }

        /// <summary>
        /// Adds one bar; volume is spread over the levels with a peak at <paramref name="pocAt"/> (0 = low, 1 = high)
        /// and the delta distributed so that its sum matches.
        /// </summary>
        public Bar Add(double open, double high, double low, double close, double volume, double delta, double pocAt = 0.5, double extremeShare = 0.02)
        {
            int n = (int)Math.Round((high - low) / Tick) + 1;
            var prices = new double[n];
            var w = new double[n];
            double sw = 0;
            int peak = (int)Math.Round(pocAt * (n - 1));
            for (int i = 0; i < n; i++)
            {
                prices[i] = low + i * Tick;
                w[i] = 1.0 / (1 + Math.Abs(i - peak));
                sw += w[i];
            }
            var bid = new double[n];
            var ask = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = volume * w[i] / sw;
                if ((i == 0 || i == n - 1) && n > 2) v = Math.Max(1, volume * extremeShare / 2);
                double d = delta * w[i] / sw;
                double a = Math.Max(0, (v + d) / 2), b = Math.Max(0, v - a);
                ask[i] = Math.Round(a);
                bid[i] = Math.Round(b);
            }
            var bar = Bar.Create(Bars.Count, _t, open, high, low, close, Tick, prices, bid, ask, n);
            Bars.Add(bar);
            _t = _t.AddMinutes(5);
            _price = close;
            return bar;
        }

        public void SkipTo(DateTime utc) => _t = utc;

        private double Gauss()
        {
            double u1 = 1 - _rnd.NextDouble(), u2 = _rnd.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>Price-mirrored copy (p -> 2c - p, bid/ask swapped): every bullish pattern becomes bearish.</summary>
        public static List<Bar> Mirror(List<Bar> bars, double centre)
        {
            var list = new List<Bar>();
            foreach (var b in bars)
            {
                int n = b.Levels;
                var prices = new double[n];
                var bid = new double[n];
                var ask = new double[n];
                for (int i = 0; i < n; i++)
                {
                    prices[i] = 2 * centre - b.PriceAt(i);
                    bid[i] = b.Ask[i];
                    ask[i] = b.Bid[i];
                }
                list.Add(Bar.Create(b.Index, b.TimeUtc, 2 * centre - b.Open, 2 * centre - b.Low, 2 * centre - b.High, 2 * centre - b.Close, b.Tick, prices, bid, ask, n));
            }
            return list;
        }
    }
}
