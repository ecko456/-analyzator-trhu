using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using ReversalConfirmation.Core;

namespace ReversalConfirmation.Replay
{
    /// <summary>Streams bars from the gzip text files written by tools/data/build_footprint.py.</summary>
    public static class FootprintReader
    {
        public static IEnumerable<Bar> Read(IEnumerable<string> files, double tick)
        {
            int index = 0;
            var prices = new List<double>();
            var bids = new List<double>();
            var asks = new List<double>();
            foreach (var f in files)
            {
                using var fs = File.OpenRead(f);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var r = new StreamReader(gz);
                string line;
                long t = 0;
                double o = 0, h = 0, l = 0, c = 0;
                bool have = false;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var p = line.Split(',');
                    if (p[0] == "B")
                    {
                        if (have) yield return Make(index++, t, o, h, l, c, tick, prices, bids, asks);
                        t = long.Parse(p[1], CultureInfo.InvariantCulture);
                        o = D(p[2]); h = D(p[3]); l = D(p[4]); c = D(p[5]);
                        prices.Clear(); bids.Clear(); asks.Clear();
                        have = true;
                    }
                    else
                    {
                        prices.Add(D(p[1]));
                        bids.Add(D(p[2]));
                        asks.Add(D(p[3]));
                    }
                }
                if (have) yield return Make(index++, t, o, h, l, c, tick, prices, bids, asks);
            }
        }

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        private static Bar Make(int index, long t, double o, double h, double l, double c, double tick, List<double> p, List<double> b, List<double> a)
            => Bar.Create(index, DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime, o, h, l, c, tick, p.ToArray(), b.ToArray(), a.ToArray(), p.Count);
    }
}
