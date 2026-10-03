using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReversalConfirmation.Core
{
    /// <summary>One CSV record (fixed schema, see <see cref="Columns"/>).</summary>
    public sealed class LogRow
    {
        // declared before Columns: static initialisers run in textual order
        public static readonly string[] TargetNames = { "T1", "T2", "T3", "R15", "R2" };
        public static readonly string[] Columns = BuildColumns();
        private static readonly Dictionary<string, int> Index = BuildIndex();
        private readonly string[] _v = new string[Columns.Length];

        private static string[] BuildColumns()
        {
            var c = new List<string>
            {
                "id", "kind", "context_id", "dir", "variant", "bar", "time_utc", "time_et", "session", "min_from_rth", "rth", "window", "news",
                "score", "strong", "reject",
                "level", "level_price", "level_weight", "level_dist_ticks", "confluence", "bars_beyond", "test_order",
                "level_profile", "level_naked", "level_age", "profile_confluence",
                "atr", "atr_classic", "tod_baseline", "volume", "volume_pct", "delta", "delta_z",
                "s_A", "s_B", "s_C", "s_D", "s_E", "s_F", "s_G", "s_H", "s_I", "h_applicable", "i_applicable",
                "drop_atr", "min_z", "sweep", "overshoot_atr", "div1", "div2", "flip", "poc_pos", "third_pct", "pattern_vol_pct",
                "rho", "fin_auction", "clv", "flush_z", "flush_vol_pct", "stall_bars", "stall_z", "stall_eff_pct",
                "vwap_side", "vwap_slope_atr", "vwap_dist_atr",
                "rev_score", "conf_score", "conf_n", "conf_delta_z", "conf_clv", "conf_eff_pct", "conf_imbalances", "conf_vol_pct", "conf_warning",
                "bos_level", "bos_anomaly", "bos_bars", "bos_dist_atr", "conf_bar", "conf_wait_bars",
                "fib_level", "fib_a", "fib_b", "fib_range_atr", "fib_bars", "had_conf",
                "zone_type", "zone_price", "zone_score", "zone_retest", "p_model", "filled", "bars_to_fill", "fill_delta_z",
                "entry", "stop", "r_ticks", "t1", "t2", "t3", "t_first", "rr_first"
            };
            foreach (var h in new[] { 3, 6, 12, 24, 36 }) c.Add("mfe_" + h);
            foreach (var h in new[] { 3, 6, 12, 24, 36 }) c.Add("mae_" + h);
            foreach (var t in TargetNames)
            {
                c.Add("hit_" + t);
                c.Add("hit_tol_" + t);
                c.Add("bars_" + t);
                c.Add("mindist_" + t);
            }
            c.AddRange(new[] { "stop_hit", "bars_stop", "result_r", "result_r15", "class", "breakout_vol_pct", "with_trend", "complete" });
            return c.ToArray();
        }

        private static Dictionary<string, int> BuildIndex()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Columns.Length; i++) d[Columns[i]] = i;
            return d;
        }

        public LogRow Set(string col, string v)
        {
            if (Index.TryGetValue(col, out var i)) _v[i] = v;
            return this;
        }

        public LogRow Set(string col, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return Set(col, "");
            return Set(col, v.ToString("0.####", CultureInfo.InvariantCulture));
        }

        public LogRow Set(string col, int v) => Set(col, v.ToString(CultureInfo.InvariantCulture));
        public LogRow Set(string col, long v) => Set(col, v.ToString(CultureInfo.InvariantCulture));
        public LogRow Set(string col, bool v) => Set(col, v ? "1" : "0");

        public string Get(string col) => Index.TryGetValue(col, out var i) ? _v[i] : null;

        public LogRow Clone()
        {
            var r = new LogRow();
            Array.Copy(_v, r._v, _v.Length);
            return r;
        }

        public static string Header() => string.Join(",", Columns);

        public string ToCsv()
        {
            var sb = new StringBuilder(1024);
            for (int i = 0; i < _v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var v = _v[i];
                if (string.IsNullOrEmpty(v)) continue;
                if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0)
                    sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                else sb.Append(v);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Follows one hypothetical trade (oriented prices) for up to the longest horizon and fills the outcome
    /// columns. Conservative: when a bar touches both the stop and a target, the stop counts first; targets are
    /// not credited on the entry bar.
    /// </summary>
    public sealed class Tracker
    {
        public LogRow Row;
        public int Dir;
        public string Kind;
        public int EntryBar;
        public double Entry, Stop, R;
        public double Tick;
        public double TolTarget;
        public double[] Targets = new double[5];     // T1, T2, T3, R1.5, R2 (NaN if not applicable)
        public int Primary = -1;                     // index of the target used for result / class
        public Zone Zone;                            // for trade drawing (optional)
        public int[] Horizons;
        public int VBars;
        public Action<Tracker> OnDone;
        /// <summary>
        /// Fibo trade: target T1 = OP = C + this range (A→B), where C is the lowest low of the correction so far.
        /// C deepens with every new low of the pullback, so OP adapts to the depth of the correction.
        /// </summary>
        public double AdaptiveRange = double.NaN;
        private double _c = double.NaN;

        private int _k;
        private double _mfe, _mae;
        private bool _stopHit, _resolved, _done;
        private readonly int[] _hitBar = { -1, -1, -1, -1, -1 };
        private readonly int[] _hitTolBar = { -1, -1, -1, -1, -1 };
        private readonly double[] _minDist = { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
        private double _breakoutVolPct = double.NaN;

        public bool Done => _done;
        public double ResultR { get; private set; } = double.NaN;
        /// <summary>Fixed 1.5 R exit: +1.5 when reached before the stop, -1 at the stop, mark-to-market at the horizon.</summary>
        public double ResultR15 { get; private set; } = double.NaN;
        public bool PrimaryHit => Primary >= 0 && _hitBar[Primary] >= 0;

        /// <summary>The entry bar itself: only the stop can be hit (order inside the bar is unknown).</summary>
        public void OnEntryBar(OBar b, int barIndex)
        {
            _mae = Math.Max(_mae, Entry - b.L);
            if (!double.IsNaN(AdaptiveRange) && b.L > Stop) DeepenCorrection(b.L);
            if (b.L <= Stop)
            {
                _stopHit = true;
                ResultR15 = -1;
                Row.Set("stop_hit", true).Set("bars_stop", 0);
                Resolve(-1, barIndex);
                if (Zone != null) Zone.StopHitBar = barIndex;
            }
        }

        public void Update(OBar b, int barIndex, double volPct)
        {
            if (_done) return;
            _k++;
            bool stopNow = !_stopHit && b.L <= Stop;

            if (!_stopHit)
            {
                for (int j = 0; j < 5; j++)
                {
                    double tg = Targets[j];
                    if (double.IsNaN(tg)) continue;
                    if (!stopNow)
                    {
                        if (_hitBar[j] < 0 && b.H >= tg)
                        {
                            _hitBar[j] = _k;
                            if (j == Primary) _breakoutVolPct = volPct;
                        }
                        if (_hitTolBar[j] < 0 && b.H >= tg - TolTarget) _hitTolBar[j] = _k;
                        _minDist[j] = Math.Min(_minDist[j], Math.Max(0, tg - b.H));
                    }
                }
                if (Zone != null) MarkZoneTargets(barIndex, b, stopNow);
            }

            _mfe = Math.Max(_mfe, b.H - Entry);
            _mae = Math.Max(_mae, Entry - b.L);
            // a deeper low moves OP down from the next bar on (the order of high and low inside this bar is unknown)
            if (!double.IsNaN(AdaptiveRange) && !stopNow && !_resolved) DeepenCorrection(b.L);

            if (double.IsNaN(ResultR15))
            {
                if (stopNow) ResultR15 = -1;
                else if (_hitBar[3] == _k) ResultR15 = (Targets[3] - Entry) / R;
            }

            if (stopNow)
            {
                _stopHit = true;
                Row.Set("stop_hit", true).Set("bars_stop", _k);
                if (Zone != null && Zone.StopHitBar < 0) Zone.StopHitBar = barIndex;
                if (!_resolved) Resolve(-1, barIndex);
            }
            else if (!_resolved && Primary >= 0 && _hitBar[Primary] == _k)
                Resolve((Targets[Primary] - Entry) / R, barIndex);

            for (int h = 0; h < Horizons.Length; h++)
                if (Horizons[h] == _k)
                {
                    Row.Set("mfe_" + Horizons[h], Math.Round(_mfe / Tick));
                    Row.Set("mae_" + Horizons[h], Math.Round(_mae / Tick));
                }

            if (Zone != null && Zone.BreakEvenBar < 0 && _mfe >= R && !_stopHit) Zone.BreakEvenBar = barIndex;

            int max = Horizons[Horizons.Length - 1];
            if (_k >= max)
            {
                if (!_resolved) Resolve((b.C - Entry) / R, barIndex);
                if (double.IsNaN(ResultR15)) ResultR15 = (b.C - Entry) / R;
                Finish(true);
            }
        }

        private void DeepenCorrection(double low)
        {
            if (!double.IsNaN(_c) && low >= _c) return;
            _c = low;
            Targets[0] = _c + AdaptiveRange;
        }

        private void MarkZoneTargets(int barIndex, OBar b, bool stopNow)
        {
            if (stopNow) return;
            foreach (var t in Zone.Targets)
            {
                if (t.HitBar >= 0) continue;
                double o = Dir * t.Price;
                if (b.H >= o - TolTarget) t.HitBar = barIndex;
            }
        }

        private void Resolve(double r, int barIndex)
        {
            _resolved = true;
            ResultR = r;
            if (Zone != null)
            {
                Zone.ResultR = r;
                Zone.TradeEndBar = barIndex;
            }
        }

        /// <summary>Writes the outcome columns. <paramref name="complete"/> = false when the data ended early.</summary>
        public void Finish(bool complete)
        {
            if (_done) return;
            _done = true;
            for (int j = 0; j < 5; j++)
            {
                var n = LogRow.TargetNames[j];
                if (double.IsNaN(Targets[j])) continue;
                Row.Set("hit_" + n, _hitBar[j] >= 0);
                Row.Set("hit_tol_" + n, _hitTolBar[j] >= 0);
                if (_hitBar[j] >= 0) Row.Set("bars_" + n, _hitBar[j]);
                else if (_hitTolBar[j] >= 0) Row.Set("bars_" + n, _hitTolBar[j]);
                if (_minDist[j] < double.MaxValue) Row.Set("mindist_" + n, Math.Round(_minDist[j] / Tick));
            }
            if (!_stopHit) Row.Set("stop_hit", false);
            Row.Set("result_r", ResultR);
            Row.Set("result_r15", ResultR15);
            string cls = "failure";
            if (Primary >= 0 && _hitBar[Primary] >= 0)
                cls = _hitBar[Primary] <= VBars ? "v-reversal" : "base-breakout";
            else if (!_stopHit && Primary >= 0 && _hitTolBar[Primary] >= 0)
                cls = _hitTolBar[Primary] <= VBars ? "v-reversal" : "base-breakout";
            Row.Set("class", cls);
            Row.Set("breakout_vol_pct", _breakoutVolPct);
            Row.Set("complete", complete);
            OnDone?.Invoke(this);
        }
    }
}
