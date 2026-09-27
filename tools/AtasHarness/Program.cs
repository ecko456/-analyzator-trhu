using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;
using ReversalConfirmation.Atas;
using ReversalConfirmation.Replay;

namespace ReversalConfirmation.AtasHarness
{
    /// <summary>Exposes the protected ATAS callbacks of the real indicator class.</summary>
    internal sealed class Driver : ReversalConfirmationEntry
    {
        public void Recalc() => OnRecalculate();
        public void Calc(int bar) => OnCalculate(bar, 0m);
        public void Render(RenderContext g) => OnRender(g, DrawingLayouts.Final);
        public void Dispose() => OnDispose();
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            var dir = args.Length > 0 ? args[0] : "/home/user/data/fp5";
            int months = args.Length > 1 ? int.Parse(args[1]) : 2;
            var files = Directory.GetFiles(dir, "fp_m5_*.txt.gz").OrderBy(x => x).TakeLast(months).ToList();
            var candles = new List<IndicatorCandle>();
            foreach (var b in FootprintReader.Read(files, 0.25))
            {
                var c = new IndicatorCandle { Open = (decimal)b.Open, High = (decimal)b.High, Low = (decimal)b.Low, Close = (decimal)b.Close, Time = b.TimeUtc };
                for (int i = 0; i < b.Levels; i++)
                    if (b.Bid[i] + b.Ask[i] > 0)
                        c.HostLevels.Add(new PriceVolumeInfo { Price = (decimal)b.PriceAt(i), Bid = (decimal)b.Bid[i], Ask = (decimal)b.Ask[i], Volume = (decimal)(b.Bid[i] + b.Ask[i]) });
                candles.Add(c);
            }
            int total = candles.Count, history = total - 300;   // last 300 bars arrive "live"
            Console.WriteLine($"candles {total} ({months} months), history {history}, live {total - history}");

            var ind = new Driver { LogFolder = Path.Combine(Path.GetTempPath(), "rc_harness") };
            ind.HostGetCandle = i => candles[i];
            var g = new RenderContext();

            // ---- history load: ATAS calls OnRecalculate, then OnCalculate for every bar (the last one is forming)
            ind.HostCurrentBar = history;
            var sw = Stopwatch.StartNew();
            ind.Recalc();
            for (int bar = 0; bar < history; bar++) ind.Calc(bar);
            sw.Stop();
            Console.WriteLine($"history load: {sw.Elapsed.TotalMilliseconds:0} ms for {history} bars = {sw.Elapsed.TotalMilliseconds * 1000 / history:0.0} us/bar, alerts {ind.HostAlerts.Count}");

            // ---- live: each bar receives 50 tick updates, then a new bar opens
            var tickTimes = new List<double>();
            var newBarTimes = new List<double>();
            var renderTimes = new List<double>();
            for (int bar = history; bar < total; bar++)
            {
                ind.HostCurrentBar = bar + 1;
                for (int tick = 0; tick < 50; tick++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    ind.Calc(bar);
                    double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                    (tick == 0 ? newBarTimes : tickTimes).Add(us);
                }
                // a repaint per bar with the last 150 bars visible and the mouse over the chart
                ind.HostFirstVisible = Math.Max(0, bar - 150);
                ind.HostLastVisible = bar;
                ind.HostChart.FirstBar = ind.HostFirstVisible;
                ind.HostChart.Top = candles[bar].High + 40;
                ind.HostChart.MouseLocationInfo.BarBelowMouse = bar - 5;
                long r0 = Stopwatch.GetTimestamp();
                ind.Render(g);
                renderTimes.Add((Stopwatch.GetTimestamp() - r0) * 1e6 / Stopwatch.Frequency);
            }
            ind.Dispose();

            static string P(List<double> v, double q) { var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(q * s.Count))].ToString("0.0"); }
            Console.WriteLine($"live tick update:  p50 {P(tickTimes, 0.5)} us, p99 {P(tickTimes, 0.99)} us, max {P(tickTimes, 1)} us");
            Console.WriteLine($"live new bar:      p50 {P(newBarTimes, 0.5)} us, p99 {P(newBarTimes, 0.99)} us, max {P(newBarTimes, 1)} us");
            Console.WriteLine($"render (150 bars): p50 {P(renderTimes, 0.5)} us, p99 {P(renderTimes, 0.99)} us, max {P(renderTimes, 1)} us");
            Console.WriteLine($"alerts raised live: {ind.HostAlerts.Count}");
            foreach (var a in ind.HostAlerts.Take(8)) Console.WriteLine("   " + a);
            long mem = GC.GetTotalMemory(true);
            Console.WriteLine($"managed memory after run: {mem / 1024 / 1024} MB");
            var log = Directory.GetFiles(Path.Combine(Path.GetTempPath(), "rc_harness"), "*.csv").FirstOrDefault();
            Console.WriteLine($"csv: {log} ({(log != null ? new FileInfo(log).Length / 1024 : 0)} KB)");
            return 0;
        }
    }
}
