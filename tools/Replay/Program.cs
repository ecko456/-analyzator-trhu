using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ReversalConfirmation.Core;

namespace ReversalConfirmation.Replay
{
    /// <summary>
    /// Back-test runner: replays footprint bars through the same engine the ATAS indicator uses and writes the
    /// calibration CSV plus a summary. Usage:
    ///   Replay --data DIR --out DIR [--tick 0.25] [--minutes 5] [--from yyyy-MM] [--to yyyy-MM]
    ///          [--calibration file.json] [--set Field=Value ...]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string data = null, outDir = "out", calib = null, from = null, to = null;
            double tick = 0.25, minutes = 5;
            var settings = new EngineSettings();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data": data = args[++i]; break;
                    case "--out": outDir = args[++i]; break;
                    case "--tick": tick = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--minutes": minutes = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--from": from = args[++i]; break;
                    case "--to": to = args[++i]; break;
                    case "--calibration": calib = args[++i]; break;
                    case "--set": Apply(settings, args[++i]); break;
                    default: Console.Error.WriteLine("unknown argument " + args[i]); return 2;
                }
            }
            if (data == null) { Console.Error.WriteLine("--data required"); return 2; }

            var files = Directory.GetFiles(data, "fp_m*.txt.gz").OrderBy(x => x).Where(f =>
            {
                var m = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f)).Split('_').Last();
                return (from == null || string.CompareOrdinal(m, from) >= 0) && (to == null || string.CompareOrdinal(m, to) <= 0);
            }).ToList();
            Directory.CreateDirectory(outDir);

            Calibration cal = null;
            if (calib != null)
            {
                cal = Calibration.TryLoad(calib, out var err);
                if (cal == null) { Console.Error.WriteLine("calibration: " + err); return 2; }
            }

            var logPath = Path.Combine(outDir, "log.csv");
            var sink = new CsvLogWriter(logPath, blockWhenFull: true);
            var engine = new ReversalEngine(settings, tick, minutes, sink, cal);

            var sw = Stopwatch.StartNew();
            double engineMs = 0;
            int bars = 0, rolls = 0, nextIndex = 0;
            Bar prev = null;
            var clock = new SessionClock(settings, minutes);
            var perBar = new List<double>(200_000);
            foreach (var b in FootprintReader.Read(files, tick))
            {
                var bt = clock.Resolve(b.TimeUtc);
                double gap = prev != null ? b.Open - prev.Close : 0;
                if (prev != null && IsRoll(bt.Local, gap))
                {
                    engine.ApplyPriceOffset(gap);
                    rolls++;
                    Console.WriteLine($"roll {bt.Local:yyyy-MM-dd HH:mm} ET gap {gap:+0.00;-0.00}");
                }
                else if (prev != null && IsRoll(bt.Local, b.Close - b.Open) && b.Volume < 100)
                {
                    // the switch to the next contract happened inside a near-empty maintenance bar: drop the bar
                    double inside = b.Close - prev.Close;
                    engine.ApplyPriceOffset(inside);
                    rolls++;
                    Console.WriteLine($"roll {bt.Local:yyyy-MM-dd HH:mm} ET gap {inside:+0.00;-0.00} (inside bar, dropped)");
                    prev = b;
                    continue;
                }
                b.Index = nextIndex++;
                long t0 = Stopwatch.GetTimestamp();
                engine.OnBar(b);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                engineMs += ms;
                perBar.Add(ms);
                engine.Events.Clear();
                prev = b;
                bars++;
            }
            engine.Flush();
            sink.Dispose();
            sw.Stop();

            perBar.Sort();
            double P(double q) => perBar.Count == 0 ? 0 : perBar[Math.Min(perBar.Count - 1, (int)(q * perBar.Count))];
            var lines = new List<string>
            {
                $"files {files.Count}, bars {bars}, rolls {rolls}",
                $"wall {sw.Elapsed.TotalSeconds:0.0}s, engine {engineMs / 1000:0.00}s = {engineMs * 1000 / Math.Max(bars, 1):0.0} us/bar (p50 {P(0.5) * 1000:0} us, p99 {P(0.99) * 1000:0} us, max {P(1) :0.0} ms)",
                $"marks {engine.Marks.Count}, zones {engine.Zones.Count}",
                $"log: {logPath} {(sink.Error != null ? "ERROR " + sink.Error : "")}"
            };
            foreach (var kv in engine.Diag.OrderBy(k => k.Key)) lines.Add($"  {kv.Key}: {kv.Value}");
            foreach (var l in lines) Console.WriteLine(l);
            File.WriteAllLines(Path.Combine(outDir, "run.txt"), lines.Concat(new[] { "args: " + string.Join(" ", args) }));
            return 0;
        }

        /// <summary>
        /// Quarterly roll of the continuous front-month series: a large jump around the daily maintenance break
        /// (16:00–19:00 ET) on a weekday of the roll week. Weekend gaps are real price moves and are not adjusted.
        /// </summary>
        private static bool IsRoll(DateTime et, double gap)
        {
            bool weekday = et.DayOfWeek >= DayOfWeek.Monday && et.DayOfWeek <= DayOfWeek.Friday;
            bool window = et.TimeOfDay >= TimeSpan.FromHours(16) && et.TimeOfDay < TimeSpan.FromHours(19);
            return et.Month % 3 == 0 && et.Day >= 7 && et.Day <= 22 && weekday && window && Math.Abs(gap) >= 25;
        }

        private static void Apply(EngineSettings s, string kv)
        {
            var p = kv.Split('=', 2);
            var f = typeof(EngineSettings).GetField(p[0], BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new ArgumentException("unknown setting " + p[0]);
            object v;
            if (f.FieldType == typeof(double[])) v = p[1].Split(';').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            else if (f.FieldType.IsEnum) v = Enum.Parse(f.FieldType, p[1], true);
            else if (f.FieldType == typeof(TimeSpan)) v = TimeSpan.Parse(p[1], CultureInfo.InvariantCulture);
            else v = Convert.ChangeType(p[1], f.FieldType, CultureInfo.InvariantCulture);
            f.SetValue(s, v);
        }
    }
}
