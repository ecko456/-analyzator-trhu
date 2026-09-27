using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ReversalConfirmation.Core
{
    /// <summary>
    /// Output of calibration/calibrate.py: component weights and an empirical probability map per score bucket.
    /// A probability is shown only when its bucket holds at least <see cref="MinSamples"/> samples.
    /// </summary>
    public sealed class Calibration
    {
        public struct Bucket
        {
            public double Lo, Hi, P;
            public int N;
        }

        /// <summary>
        /// Logistic model over logged columns: p = 1 / (1 + exp(-(b0 + sum coef_i * clamp(x_i, lo_i, hi_i)))).
        /// The displayed probability is not the raw model output but the out-of-sample hit rate of the
        /// model bucket the signal falls into (measured, not estimated), and only with enough samples.
        /// </summary>
        public sealed class Model
        {
            public double Intercept;
            public readonly List<(string col, double coef, double lo, double hi, double fill)> Terms = new List<(string, double, double, double, double)>();
            public readonly List<Bucket> Reliability = new List<Bucket>();

            public double Raw(LogRow row)
            {
                double z = Intercept;
                foreach (var t in Terms)
                {
                    var sv = row.Get(t.col);
                    double x = t.fill;
                    if (!string.IsNullOrEmpty(sv) && double.TryParse(sv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        x = v;
                    x = Math.Max(t.lo, Math.Min(t.hi, x));
                    z += t.coef * x;
                }
                return 1 / (1 + Math.Exp(-Math.Max(-30, Math.Min(30, z))));
            }
        }

        public Model ReversalModel, ZoneModel;

        /// <summary>Measured probability for a logged signal, NaN when the model or its bucket lacks samples.</summary>
        public double Probability(Model m, LogRow row, int minSamples)
        {
            if (m == null) return double.NaN;
            double p = m.Raw(row);
            foreach (var b in m.Reliability)
                if (p >= b.Lo && p < b.Hi)
                    return b.N >= minSamples ? b.P : double.NaN;
            return double.NaN;
        }

        public double[] Weights;           // null = keep settings
        public readonly List<Bucket> Reversal = new List<Bucket>();
        public readonly List<Bucket> Zone = new List<Bucket>();
        public int MinSamples = 30;
        public string Source;

        public double ReversalProbability(double score) => Lookup(Reversal, score);
        public double ZoneProbability(double score) => Lookup(Zone, score);

        private double Lookup(List<Bucket> list, double score)
        {
            foreach (var b in list)
                if (score >= b.Lo && score < b.Hi)
                    return b.N >= MinSamples ? b.P : double.NaN;
            return double.NaN;
        }

        public static Calibration TryLoad(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var c = new Calibration { Source = path };
                if (root.TryGetProperty("min_samples", out var ms) && ms.TryGetInt32(out var msv)) c.MinSamples = msv;
                if (root.TryGetProperty("weights", out var w) && w.ValueKind == JsonValueKind.Object)
                {
                    var arr = new double[9];
                    bool any = false;
                    foreach (Comp comp in Enum.GetValues(typeof(Comp)))
                    {
                        if (w.TryGetProperty(comp.ToString(), out var v) && v.TryGetDouble(out var d))
                        {
                            arr[(int)comp] = Math.Max(0, d);
                            any = true;
                        }
                        else arr[(int)comp] = double.NaN;
                    }
                    if (any) c.Weights = arr;
                }
                ReadBuckets(root, "reversal", c.Reversal);
                ReadBuckets(root, "zone", c.Zone);
                c.ReversalModel = ReadModel(root, "reversal_model");
                c.ZoneModel = ReadModel(root, "zone_model");
                return c;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static Model ReadModel(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var m) || m.ValueKind != JsonValueKind.Object) return null;
            var model = new Model();
            if (m.TryGetProperty("intercept", out var ic)) model.Intercept = ic.GetDouble();
            if (m.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Array)
                foreach (var t in terms.EnumerateArray())
                {
                    string col = t.GetProperty("col").GetString();
                    double coef = t.GetProperty("coef").GetDouble();
                    double lo = t.TryGetProperty("lo", out var l) ? l.GetDouble() : double.MinValue;
                    double hi = t.TryGetProperty("hi", out var h) ? h.GetDouble() : double.MaxValue;
                    double fill = t.TryGetProperty("fill", out var f) ? f.GetDouble() : 0;
                    model.Terms.Add((col, coef, lo, hi, fill));
                }
            ReadBuckets(m, "reliability", model.Reliability);
            return model.Terms.Count > 0 ? model : null;
        }

        private static void ReadBuckets(JsonElement root, string name, List<Bucket> dst)
        {
            if (!root.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return;
            foreach (var e in a.EnumerateArray())
            {
                var b = new Bucket();
                if (e.TryGetProperty("lo", out var lo)) b.Lo = lo.GetDouble();
                if (e.TryGetProperty("hi", out var hi)) b.Hi = hi.GetDouble();
                if (e.TryGetProperty("p", out var p)) b.P = p.GetDouble();
                if (e.TryGetProperty("n", out var n)) b.N = n.GetInt32();
                dst.Add(b);
            }
        }

        /// <summary>Applies calibrated weights (missing components keep their current value).</summary>
        public void ApplyTo(EngineSettings s)
        {
            if (Weights == null) return;
            for (int i = 0; i < 9; i++)
                if (!double.IsNaN(Weights[i])) s.Weights[i] = Weights[i];
        }
    }
}
