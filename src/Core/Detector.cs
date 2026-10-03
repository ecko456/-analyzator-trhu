using System;
using System.Collections.Generic;

namespace ReversalConfirmation.Core
{
    public enum Variant
    {
        OneBar,
        TwoBar,
        ThreeBar,
        Stall
    }

    /// <summary>One evaluated reversal candidate. All prices are oriented (see <see cref="OBar"/>).</summary>
    public sealed class Candidate
    {
        public int Dir;
        public Variant Variant;
        public int First, Last;
        public OBar M;
        public double Atr;
        public double Tol;

        public readonly double[] S = new double[9];
        public bool HApplicable, IApplicable;
        public double Score;

        public bool GateA, HasLevel, Reclaimed, SpeedOk;
        public string Reject;
        public bool Passed => Reject == null;

        // level
        public RefLevel Level;
        public double LevelO;          // oriented level price
        public int Confluence;
        /// <summary>Profile levels (VAH / VAL / POC of any profile) within the tolerance of the low, the picked one included.</summary>
        public int ProfileConfluence;
        /// <summary>Those profile levels (null when none), for the tooltip.</summary>
        public List<RefLevel> NearProfile;
        public double LevelDistTicks;  // + = low stayed above the level, - = pierced
        public int BarsBeyond;
        public int TestOrder = 1;

        // raw features for logging / calibration
        public double DropAtr, MinZ, SweepOk, OvershootAtr, Div1, Div2, Flip, PocPos, ThirdPct, VolPct, Rho, FinAuction, Clv;
        public double FlushZ = double.NaN, FlushVolPct = double.NaN;
        public int StallBars;
        public double StallZ = double.NaN, StallEffPct = double.NaN;

        // trade context
        public int OriginBar;
        public double OriginOpen;      // oriented T1
        public double RefDelta;        // most negative oriented delta of the move (<= 0)
        public int ExtremeBar;         // bar that printed the extreme

        public string VariantName
        {
            get
            {
                switch (Variant)
                {
                    case Variant.OneBar: return "1-bar";
                    case Variant.TwoBar: return "2-bar";
                    case Variant.ThreeBar: return "3-bar";
                    default: return "stall";
                }
            }
        }

        public double Real(double oriented) => Dir * oriented;
        public double ExtremeReal => Dir * M.L;
    }

    /// <summary>Layer 1: reversal detection on closed bars (components A–I, gate, score).</summary>
    public sealed class Detector
    {
        private readonly EngineSettings _s;
        private readonly double _tick;
        private readonly History<Bar> _bars;
        private readonly History<BarStat> _stats;
        private readonly SortedWindow _thirdShares;
        private readonly SortedWindow _eff4;
        private readonly double[] _x = new double[16], _y = new double[16];

        public Detector(EngineSettings s, double tick, History<Bar> bars, History<BarStat> stats, SortedWindow thirdShares, SortedWindow eff4)
        {
            _s = s;
            _tick = tick;
            _bars = bars;
            _stats = stats;
            _thirdShares = thirdShares;
            _eff4 = eff4;
        }

        private OBar O(int i, int dir) => _bars[i].Oriented(dir);
        private double Z(int i, int dir) => dir * _stats[i].DeltaZ;

        /// <summary>
        /// Evaluates all variants ending at bar <paramref name="t"/> for one direction and returns the best one
        /// (passing candidates first, by score). Returns null when nothing is worth logging.
        /// </summary>
        public Candidate Evaluate(int t, int dir, List<RefLevel> levels)
        {
            var st = _stats[t];
            double atr = st.Atr;
            if (atr <= 0) return null;

            Candidate best = null;
            void Consider(Candidate c)
            {
                if (c == null) return;
                if (best == null
                    || c.Passed && !best.Passed
                    || c.Passed == best.Passed && c.Score > best.Score)
                    best = c;
            }

            Consider(Build(t, t, dir, Variant.OneBar, levels, atr));
            if (_s.MaxMergedBars >= 2 && _bars.Has(t - 1)) Consider(Build(t - 1, t, dir, Variant.TwoBar, levels, atr));
            if (_s.MaxMergedBars >= 3 && _bars.Has(t - 2)) Consider(Build(t - 2, t, dir, Variant.ThreeBar, levels, atr));
            if (_s.EnableStall) Consider(BuildStall(t, dir, levels, atr));
            return best;
        }

        private Candidate BuildStall(int t, int dir, List<RefLevel> levels, double atr)
        {
            var s = _s;
            if (!_bars.Has(t - s.I_MaxBars - 1)) return null;
            var trig = O(t, dir);
            var prev = O(t - 1, dir);
            double trigZ = Z(t, dir);
            // trigger: initiative delta in the reversal direction and close through the previous bar's high
            if (trigZ < s.I_TriggerZ || trig.C <= prev.H) return null;

            double band = Math.Max(s.I_BandTicks * _tick, s.I_BandAtr * atr);
            double progMax = Math.Max(s.I_ProgressTicks * _tick, s.I_ProgressAtr * atr);
            for (int k = s.I_MaxBars; k >= s.I_MinBars; k--)
            {
                int a = t - k;
                double minL = double.MaxValue, maxL = double.MinValue, cum = 0, varSum = 0;
                for (int i = a; i <= t - 1; i++)
                {
                    var b = O(i, dir);
                    minL = Math.Min(minL, b.L);
                    maxL = Math.Max(maxL, b.L);
                    cum += b.Delta;
                    var sd = _stats[i].DeltaStd;
                    varSum += sd * sd;
                }
                if (maxL - minL > band) continue;
                if (O(a, dir).L - minL > progMax) continue;
                double zW = varSum > 0 ? cum / Math.Sqrt(varSum) : 0;
                if (zW > -s.I_CumDeltaZ) continue;
                double eff = ReversalEngine.Efficiency(Math.Abs(O(t - 1, dir).C - O(a, dir).O), atr, Math.Abs(cum), Math.Sqrt(varSum));
                double effPct = _eff4.PercentileRank(eff);
                if (effPct >= s.I_EfficiencyPct) continue;

                var c = Build(a, t, dir, Variant.Stall, levels, atr);
                c.IApplicable = true;
                c.StallBars = k;
                c.StallZ = zW;
                c.StallEffPct = effPct;
                c.S[(int)Comp.I] = MathUtil.Clamp01(0.7 + 0.15 * MathUtil.Ramp(-zW, s.I_CumDeltaZ, 3) + 0.15 * MathUtil.Ramp(s.I_EfficiencyPct - effPct, 0, s.I_EfficiencyPct));
                Finish(c);
                return c;
            }
            return null;
        }

        private Candidate Build(int first, int last, int dir, Variant variant, List<RefLevel> levels, double atr)
        {
            var s = _s;
            int n = last - first + 1;
            var arr = new OBar[n];
            for (int i = 0; i < n; i++) arr[i] = O(first + i, dir);
            var m = OBar.Merge(arr, 0, n - 1);
            var c = new Candidate
            {
                Dir = dir, Variant = variant, First = first, Last = last, M = m, Atr = atr,
                Tol = Math.Max(s.LevelTolTicks * _tick, s.LevelTolAtr * atr),
                HApplicable = variant == Variant.TwoBar || variant == Variant.ThreeBar
            };
            c.ExtremeBar = first;
            for (int i = first; i <= last; i++)
                if (O(i, dir).L <= O(c.ExtremeBar, dir).L) c.ExtremeBar = i;

            CompA(c);
            CompB(c, levels);
            CompC(c);
            CompD(c);
            CompE(c);
            CompF(c);
            CompG(c);
            if (c.HApplicable) CompH(c);
            if (variant != Variant.Stall) Finish(c);
            return c;
        }

        private void Finish(Candidate c)
        {
            var w = _s.Weights;
            double num = 0, den = 0;
            for (int i = 0; i <= (int)Comp.G; i++) { num += w[i] * c.S[i]; den += w[i]; }
            if (c.HApplicable) { num += w[(int)Comp.H] * c.S[(int)Comp.H]; den += w[(int)Comp.H]; }
            if (c.IApplicable) { num += w[(int)Comp.I] * c.S[(int)Comp.I]; den += w[(int)Comp.I]; }
            c.Score = den > 0 ? 100 * num / den : 0;

            if (!c.GateA) c.Reject = "no initiative";
            else if (!c.HasLevel) c.Reject = "no level";
            else if (!c.Reclaimed) c.Reject = "no reclaim";
            else if (!c.SpeedOk) c.Reject = "slow reclaim";
            else if (c.Score < _s.MinScore) c.Reject = "low score";
        }

        /// <summary>
        /// Structure level that has to break to confirm the reversal (oriented: for a bullish reversal a high).
        /// Anomaly: the extreme candle is an outside bar (new low and high above the previous candle) - its high.
        /// Standard: going left from the extreme, the first candle whose high is above the candle to its left
        /// (the last lower high). Fallback when the whole lookback only falls: its highest high.
        /// </summary>
        public double StructureLevel(int dir, int extremeBar, out int pivotBar, out bool anomaly)
        {
            anomaly = false;
            var a = O(extremeBar, dir);
            if (_bars.Has(extremeBar - 1) && a.H > O(extremeBar - 1, dir).H)
            {
                anomaly = true;
                pivotBar = extremeBar;
                return a.H;
            }
            int stop = Math.Max(0, extremeBar - _s.BosLookback);
            double maxH = double.MinValue;
            pivotBar = extremeBar;
            for (int j = extremeBar - 1; j > stop && _bars.Has(j - 1); j--)
            {
                var b = O(j, dir);
                if (b.H > maxH) { maxH = b.H; pivotBar = j; }
                if (b.H > O(j - 1, dir).H)
                {
                    pivotBar = j;
                    return b.H;
                }
            }
            return maxH > double.MinValue ? maxH : a.H;
        }

        // ---------------- A: prior initiative move ----------------
        private void CompA(Candidate c)
        {
            var s = _s;
            int from = Math.Max(0, c.First - s.A_Bars + 1);
            double maxH = double.MinValue;
            int hBar = c.First;
            for (int i = from; i <= c.First; i++)
            {
                if (!_bars.Has(i)) continue;
                var b = O(i, c.Dir);
                if (b.H > maxH) { maxH = b.H; hBar = i; }
            }
            double minZ = 0, minDelta = 0;
            for (int i = from; i <= c.Last; i++)
            {
                if (!_bars.Has(i)) continue;
                minZ = Math.Min(minZ, Z(i, c.Dir));
                minDelta = Math.Min(minDelta, O(i, c.Dir).Delta);
            }
            double drop = (maxH - c.M.L) / c.Atr;
            c.DropAtr = drop;
            c.MinZ = minZ;
            c.RefDelta = minDelta;
            c.GateA = drop >= s.A_DropAtr && minZ <= -s.A_DeltaZ;
            c.S[(int)Comp.A] = c.GateA
                ? MathUtil.Clamp01(0.5 + 0.25 * MathUtil.Ramp(drop, s.A_DropAtr, 2 * s.A_DropAtr) + 0.25 * MathUtil.Ramp(-minZ, s.A_DeltaZ, s.A_DeltaZ + 1.5))
                : 0.25 * MathUtil.Ramp(drop, 0, s.A_DropAtr) + 0.25 * MathUtil.Ramp(-minZ, 0, s.A_DeltaZ);

            // origin = open of the first bar that moved against the reversal from the local extreme
            c.OriginBar = hBar;
            c.OriginOpen = O(hBar, c.Dir).O;
            for (int i = hBar; i <= c.First; i++)
            {
                var b = O(i, c.Dir);
                if (b.C < b.O)
                {
                    c.OriginBar = i;
                    c.OriginOpen = b.O;
                    break;
                }
            }
        }

        // ---------------- B: reference level ----------------
        private void CompB(Candidate c, List<RefLevel> levels)
        {
            var m = c.M;
            double tol = c.Tol;
            double prevClose = _bars.Has(c.First - 1) ? O(c.First - 1, c.Dir).C : m.O;
            double bestScore = -1, bestReclaimedScore = -1;
            int best = -1, bestReclaimed = -1;
            var prox = new double[levels.Count];

            for (int i = 0; i < levels.Count; i++)
            {
                double L = c.Dir * levels[i].Price;
                if (prevClose < L - tol) continue;          // must approach from the reversal side
                if (m.L > L + tol) continue;                // not reached
                double d = m.L - L;
                double p;
                if (d >= 0) p = 1 - 0.4 * d / tol;
                else
                {
                    double pierce = -d;
                    if (pierce <= Math.Max(tol, _s.C_OvershootAtr * c.Atr)) p = 1;
                    else p = Math.Max(0.3, 1 - 0.7 * (pierce / c.Atr - _s.C_OvershootAtr));
                }
                prox[i] = p;
                double sc = levels[i].Weight * p;
                if (sc > bestScore) { bestScore = sc; best = i; }
                if (m.C > L && sc > bestReclaimedScore) { bestReclaimedScore = sc; bestReclaimed = i; }
            }

            if (best < 0)
            {
                c.HasLevel = false;
                c.S[(int)Comp.B] = 0;
                return;
            }

            int pick = bestReclaimed >= 0 ? bestReclaimed : best;
            c.HasLevel = true;
            c.Reclaimed = bestReclaimed >= 0;
            c.Level = levels[pick];
            c.LevelO = c.Dir * levels[pick].Price;
            c.LevelDistTicks = (m.L - c.LevelO) / _tick;

            int conf = 0, prof = 0;
            if (levels[pick].IsProfile) { prof = 1; (c.NearProfile = new List<RefLevel>()).Add(levels[pick]); }
            for (int i = 0; i < levels.Count; i++)
            {
                if (i == pick) continue;
                if (Math.Abs(c.Dir * levels[i].Price - c.LevelO) > tol) continue;
                bool dup = false;
                for (int j = 0; j < i; j++)
                    if (j != pick && levels[j].Kind == levels[i].Kind && Math.Abs(levels[j].Price - levels[i].Price) < _tick / 2) dup = true;
                if (dup) continue;
                conf++;
                if (levels[i].IsProfile)
                {
                    prof++;
                    (c.NearProfile ??= new List<RefLevel>()).Add(levels[i]);
                }
            }
            c.Confluence = conf;
            c.ProfileConfluence = prof;
            double bonus = Math.Min(_s.ConfluenceMax, _s.ConfluenceStep * conf);
            c.S[(int)Comp.B] = MathUtil.Clamp01(levels[pick].Weight * prox[pick] * (1 + bonus));
        }

        // ---------------- C: sweep & speed of return ----------------
        private void CompC(Candidate c)
        {
            var s = _s;
            var m = c.M;
            double prevMin = double.MaxValue;
            for (int i = c.First - s.C_SweepBars; i < c.First; i++)
                if (_bars.Has(i)) prevMin = Math.Min(prevMin, O(i, c.Dir).L);
            bool sweep = prevMin < double.MaxValue && m.L <= prevMin - _tick + 1e-9 && m.C > prevMin;
            c.SweepOk = sweep ? 1 : 0;

            if (!c.HasLevel)
            {
                c.SpeedOk = true;
                c.S[(int)Comp.C] = 0.55 * c.SweepOk;
                return;
            }

            // consecutive closes beyond the level right before the reclaim bar
            int beyond = 0;
            for (int i = c.Last - 1; i >= c.Last - 6 && _bars.Has(i); i--)
            {
                if (O(i, c.Dir).C < c.LevelO) beyond++;
                else break;
            }
            c.BarsBeyond = beyond;
            c.SpeedOk = beyond <= s.C_MaxBarsBeyond;
            double speed = beyond <= 1 ? 1 : beyond == 2 ? 0.5 : 0;

            double pierce = Math.Max(0, c.LevelO - m.L) / c.Atr;
            c.OvershootAtr = pierce;
            double penalty = pierce <= s.C_OvershootAtr ? 1 : Math.Max(0.4, 1 - 0.6 * (pierce - s.C_OvershootAtr));

            // second test that takes out the first test's low
            double bonus = 0;
            double firstTest = double.NaN;
            for (int i = c.First - 1; i >= c.First - s.C_SecondTestLookback && _bars.Has(i); i--)
            {
                var b = O(i, c.Dir);
                if (Math.Abs(b.L - c.LevelO) <= c.Tol)
                {
                    c.TestOrder++;
                    if (double.IsNaN(firstTest) || b.L < firstTest) firstTest = b.L;
                }
            }
            if (!double.IsNaN(firstTest) && m.L < firstTest) bonus = 0.25;

            c.S[(int)Comp.C] = MathUtil.Clamp01((0.55 * c.SweepOk + 0.45 * speed) * penalty + bonus);
        }

        // ---------------- D: delta divergence ----------------
        private void CompD(Candidate c)
        {
            var s = _s;
            var m = c.M;
            var low = O(c.ExtremeBar, c.Dir);

            // previous confirmed swing low (strength 2) above the new low
            double div1 = 0;
            for (int j = c.First - 3; j >= c.First - s.D_SwingLookback && _bars.Has(j - 2); j--)
            {
                var b = O(j, c.Dir);
                bool pivot = b.L < O(j - 1, c.Dir).L && b.L < O(j - 2, c.Dir).L && b.L <= O(j + 1, c.Dir).L && b.L <= O(j + 2, c.Dir).L;
                if (!pivot) continue;
                if (b.L <= m.L) break;          // not a lower low relative to this swing
                div1 = low.Delta > b.Delta ? 1 : 0;
                break;
            }

            // cumulative delta of the move does not make a new low while price does
            double cvd = 0, cvdMinBefore = 0, lowBefore = double.MaxValue;
            for (int i = c.OriginBar; i < c.First; i++)
            {
                var b = O(i, c.Dir);
                cvd += b.Delta;
                cvdMinBefore = Math.Min(cvdMinBefore, cvd);
                lowBefore = Math.Min(lowBefore, b.L);
            }
            for (int i = c.First; i <= c.Last; i++) cvd += O(i, c.Dir).Delta;
            double div2 = c.OriginBar < c.First && m.L < lowBefore && cvd > cvdMinBefore ? 1 : 0;

            // intrabar flip: sellers below the POC, buyers above
            double below = 0, above = 0;
            for (int k = 0; k < m.Levels; k++)
            {
                double d = m.With[k] - m.Against[k];
                if (k < m.PocIndex) below += d;
                else if (k > m.PocIndex) above += d;
            }
            double flip = below < 0 && above > 0 ? 1 : 0;

            c.Div1 = div1;
            c.Div2 = div2;
            c.Flip = flip;
            c.S[(int)Comp.D] = 0.35 * div1 + 0.35 * div2 + 0.30 * flip;
        }

        // ---------------- E: absorption at the extreme ----------------
        private void CompE(Candidate c)
        {
            var s = _s;
            var m = c.M;
            double pocPos = m.Range > 0 ? (m.Poc - m.L) / m.Range : 0.5;
            double e1 = MathUtil.Ramp(pocPos, 0.6, s.E_PocZone);

            double third = m.L + m.Range / 3 + 1e-9, vt = 0;
            for (int k = 0; k < m.Levels; k++)
                if (m.PriceAt(k) <= third) vt += m.LevelVolume(k);
            double share = m.Volume > 0 ? vt / m.Volume : 0;
            double sharePct = _thirdShares.PercentileRank(share);
            double e2 = MathUtil.Ramp(sharePct, 40, s.E_ExtremeThirdPct);

            double volPct = 0;
            for (int i = c.First; i <= c.Last; i++) volPct = Math.Max(volPct, _stats[i].VolumePct);
            double e3 = MathUtil.Ramp(volPct, 40, s.E_VolumePct);

            c.PocPos = pocPos;
            c.ThirdPct = sharePct;
            c.VolPct = volPct;
            c.S[(int)Comp.E] = (e1 + e2 + e3) / 3;
        }

        // ---------------- F: exhaustion at the extreme ----------------
        private void CompF(Candidate c)
        {
            var s = _s;
            var m = c.M;
            int n = Math.Min(s.F_Levels, m.Levels);
            if (n < 3)
            {
                c.S[(int)Comp.F] = 0;
                return;
            }
            for (int k = 0; k < n; k++)
            {
                _x[k] = k;               // distance from the extreme in ticks
                _y[k] = m.Against[k];    // aggressive volume against the reversal
            }
            // exhaustion = aggression fades into the extreme = volume rises with distance from it
            double rho = MathUtil.Spearman(_x, _y, n);
            double avg = m.Volume / m.Levels;
            double fin = avg > 0 ? m.LevelVolume(0) / avg : 1;
            c.Rho = rho;
            c.FinAuction = fin;
            double f1 = MathUtil.Ramp(rho, 0, s.F_Spearman);
            double f2 = MathUtil.Ramp(fin, 2 * s.F_FinishedAuction, s.F_FinishedAuction);
            c.S[(int)Comp.F] = 0.5 * f1 + 0.5 * f2;
        }

        // ---------------- G: close ----------------
        private void CompG(Candidate c)
        {
            var m = c.M;
            double clv = m.Clv;
            double g = clv >= _s.G_Clv ? 0.7 + 0.3 * MathUtil.Ramp(clv, _s.G_Clv, 1) : 0.7 * MathUtil.Ramp(clv, 0.2, _s.G_Clv);
            if (m.C > m.O) g += 0.15;
            c.Clv = clv;
            c.S[(int)Comp.G] = MathUtil.Clamp01(g);
        }

        // ---------------- H: failed initiative (trapped sellers) ----------------
        private void CompH(Candidate c)
        {
            var s = _s;
            double best = 0;
            var r = O(c.Last, c.Dir);
            for (int f = c.First; f < c.Last; f++)
            {
                var fb = O(f, c.Dir);
                double z = Z(f, c.Dir);
                double vp = _stats[f].VolumePct;
                if (double.IsNaN(c.FlushZ) || z < c.FlushZ) { c.FlushZ = z; c.FlushVolPct = vp; }

                bool flush = z <= -s.H_FlushZ && vp >= s.H_FlushVolPct && c.HasLevel && fb.L <= c.LevelO + c.Tol;
                if (!flush) continue;
                bool noNewLow = true;
                for (int i = f + 1; i <= c.Last; i++)
                    if (O(i, c.Dir).L < fb.L - _tick - 1e-9) noNewLow = false;
                bool ok = noNewLow && r.Delta > 0 && r.C > (fb.H + fb.L) / 2;
                if (!ok) continue;

                double h = 0.8 + 0.2 * MathUtil.Ramp(-z, s.H_FlushZ, s.H_FlushZ + 1.5);
                // capitulation bonus: the flush is the most extreme bar of the whole move by volume and delta
                bool extreme = true;
                for (int i = Math.Max(0, c.First - s.A_Bars); i <= c.Last && extreme; i++)
                {
                    if (i == f || !_bars.Has(i)) continue;
                    var b = O(i, c.Dir);
                    if (b.Volume > fb.Volume || b.Delta < fb.Delta) extreme = false;
                }
                if (extreme) h = Math.Min(1, h * s.H_ExtremeBonus);
                best = Math.Max(best, h);
            }
            c.S[(int)Comp.H] = best;
        }
    }
}
