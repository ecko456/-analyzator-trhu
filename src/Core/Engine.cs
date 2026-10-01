using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReversalConfirmation.Core
{
    /// <summary>
    /// Incremental reversal &amp; confirmation engine. Feed it closed bars in order with <see cref="OnBar"/>;
    /// it never looks ahead and never rewrites what it has emitted. All work per bar is O(levels + small windows).
    /// Thread-safety: callers that read <see cref="Marks"/>/<see cref="Zones"/> from another thread lock <see cref="Sync"/>.
    /// </summary>
    public sealed class ReversalEngine
    {
        public readonly object Sync = new object();
        public readonly EngineSettings S;
        public readonly double Tick;
        public readonly SessionClock Clock;

        public readonly List<Mark> Marks = new List<Mark>();
        public readonly List<Zone> Zones = new List<Zone>();
        public readonly List<EngineEvent> Events = new List<EngineEvent>();
        public readonly BucketStats ReversalStats = new BucketStats();
        public readonly BucketStats ZoneStats = new BucketStats();
        /// <summary>Filled-zone results per zone type (n, T1 hits, sum R at T1 exit, sum R at 1.5 R exit).</summary>
        public readonly Dictionary<ZoneTypes, double[]> ZoneTypeStats = new Dictionary<ZoneTypes, double[]>();
        public readonly List<RefLevel> CurrentLevels = new List<RefLevel>();

        /// <summary>Diagnostic counters (why candidates / confirmations fail); cheap, for the replay report.</summary>
        public readonly Dictionary<string, long> Diag = new Dictionary<string, long>();
        private void Count(string key) { Diag.TryGetValue(key, out var v); Diag[key] = v + 1; }

        public int ProcessedBars { get; private set; }
        public int LastBar { get; private set; } = -1;
        public Calibration Calibration { get; }

        private readonly History<Bar> _bars = new History<Bar>(256);
        private readonly History<BarStat> _stats = new History<BarStat>(256);
        private readonly TodStats _volTod, _deltaTod, _rangeTod;
        private readonly Atr _atr;
        private readonly SortedWindow _thirdShares, _eff1, _eff4, _effInit;
        private readonly LevelTracker _levels;
        private readonly Detector _det;
        private readonly List<RefLevel> _lvl = new List<RefLevel>();
        private readonly List<Context> _contexts = new List<Context>();
        private readonly List<Tracker> _trackers = new List<Tracker>();
        private readonly ILogSink _log;
        private long _session = long.MinValue;
        private double _cvd;
        private int _nextId = 1;

        public ReversalEngine(EngineSettings settings, double tick, double barMinutes, ILogSink log = null, Calibration calibration = null)
        {
            S = settings.Clone();
            if (S.UseCalibratedWeights) calibration?.ApplyTo(S);
            Calibration = calibration;
            Tick = tick;
            Clock = new SessionClock(S, barMinutes);
            _log = log;
            _volTod = new TodStats(S.TodDays, S.TodNeighborSlots, S.TodMinSamples, S.RollingFallbackBars);
            _deltaTod = new TodStats(S.TodDays, S.TodNeighborSlots, S.TodMinSamples, S.RollingFallbackBars);
            _rangeTod = new TodStats(S.TodDays, S.TodNeighborSlots, S.TodMinSamples, S.RollingFallbackBars);
            _atr = new Atr(S.AtrPeriod);
            _thirdShares = new SortedWindow(S.DistributionWindow * 2);
            _eff1 = new SortedWindow(S.DistributionWindow);
            _eff4 = new SortedWindow(S.DistributionWindow);
            _effInit = new SortedWindow(S.DistributionWindow);
            _levels = new LevelTracker(S, tick, barMinutes);
            _det = new Detector(S, tick, _bars, _stats, _thirdShares, _eff4);
        }

        public BarStat StatOf(int bar) => _stats.TryGet(bar);

        // =====================================================================================
        public void OnBar(Bar b)
        {
            lock (Sync)
            {
                if (b.Index <= LastBar) return;   // idempotent: a bar is processed once
                Process(b);
                LastBar = b.Index;
                ProcessedBars++;
            }
        }

        /// <summary>Contract roll in a continuous back-test series.</summary>
        public void ApplyPriceOffset(double offset)
        {
            lock (Sync)
            {
                _levels.Shift(offset);
                _atr.Shift(offset);
                for (int i = LastBar; i > LastBar - _bars.Capacity + 1 && i >= 0; i--)
                    _bars.TryGet(i)?.Shift(offset);
                // open contexts/trades span the roll gap and would be meaningless
                foreach (var c in _contexts) Close(c, LastBar, "roll", false);
                _contexts.Clear();
                foreach (var t in _trackers) t.Finish(false);
                _trackers.Clear();
            }
        }

        /// <summary>Flushes unfinished outcome rows (end of data).</summary>
        public void Flush()
        {
            lock (Sync)
            {
                foreach (var t in _trackers.ToArray()) t.Finish(false);
                _trackers.Clear();
                foreach (var c in _contexts)
                    foreach (var z in c.Zones)
                        if (z.State == ZoneState.Active) LogZoneUnfilled(c, z, "open");
            }
        }

        private void Process(Bar b)
        {
            int t = b.Index;
            var bt = Clock.Resolve(b.TimeUtc);
            if (bt.SessionId != _session)
            {
                _session = bt.SessionId;
                _cvd = 0;
            }

            // ---------- statistics of this bar against history ----------
            var st = new BarStat { T = bt };
            var bv = _volTod.Query(bt.Slot, bt.SessionId);
            st.VolumePct = bv.Pct(b.Volume);
            st.VolumeZ = bv.Z(b.Volume);
            st.TodBaseline = bv.TimeOfDay;
            var bd = _deltaTod.Query(bt.Slot, bt.SessionId);
            st.DeltaStd = Math.Max(bd.Std, 1);
            st.DeltaZ = bd.Count >= 3 ? (b.Delta - bd.Mean) / st.DeltaStd : 0;
            var br = _rangeTod.Query(bt.Slot, bt.SessionId);
            st.TodRangeMedian = br.TimeOfDay ? br.Median() : 0;
            st.AtrClassic = _atr.Value > 0 ? _atr.Value : b.Range;
            st.Atr = S.AtrMode == AtrMode.Hybrid ? Math.Max(st.AtrClassic, st.TodRangeMedian) : st.AtrClassic;
            st.Atr = Math.Max(st.Atr, 4 * Tick);

            // price efficiency = net move (in ATR) per unit of delta (in time-of-day z units); both sides are
            // normalised so that thin overnight bars and busy RTH bars are comparable
            double eff = Efficiency(Math.Abs(b.Close - b.Open), st.Atr, Math.Abs(b.Delta), st.DeltaStd);
            st.Efficiency = eff;
            st.EfficiencyPct = _eff1.PercentileRank(eff);
            st.InitiativeEffPct = _effInit.Count >= 20 ? _effInit.PercentileRank(eff) : st.EfficiencyPct;
            double eff4 = double.NaN;
            if (_bars.Has(t - 3))
            {
                double cum = b.Delta, var = st.DeltaStd * st.DeltaStd;
                for (int i = t - 3; i < t; i++)
                {
                    cum += _bars[i].Delta;
                    var += _stats[i].DeltaStd * _stats[i].DeltaStd;
                }
                eff4 = Efficiency(Math.Abs(b.Close - _bars[t - 3].Open), st.Atr, Math.Abs(cum), Math.Sqrt(var));
                st.Window4EffPct = _eff4.PercentileRank(eff4);
            }
            ThirdShares(b, out st.LowThirdShare, out st.HighThirdShare);
            st.LowThirdPct = _thirdShares.PercentileRank(st.LowThirdShare);
            st.HighThirdPct = _thirdShares.PercentileRank(st.HighThirdShare);
            _cvd += b.Delta;
            st.SessionCvd = _cvd;
            if (_levels.HasVwap)
            {
                st.Vwap = _levels.SessionVwap;
                st.VwapStd = _levels.SessionVwapStd;
                st.VwapSlope = _levels.VwapSlope(12);
            }

            _bars.Put(t, b);
            _stats.Put(t, st);

            // ---------- levels known before this bar ----------
            _levels.Snapshot(_lvl, st.Atr, bt.SessionId);
            CurrentLevels.Clear();
            CurrentLevels.AddRange(_lvl);

            // ---------- 1) running outcome trackers ----------
            for (int i = _trackers.Count - 1; i >= 0; i--)
            {
                var tr = _trackers[i];
                tr.Update(b.Oriented(tr.Dir), t, st.VolumePct);
                if (tr.Done) _trackers.RemoveAt(i);
            }

            // ---------- 2) live contexts: zones, retests, confirmations ----------
            for (int i = 0; i < _contexts.Count; i++) UpdateContext(_contexts[i], t);
            _contexts.RemoveAll(c => c.Done);

            // ---------- 3) new reversals ----------
            if (S.EnableBullish) HandleCandidate(_det.Evaluate(t, 1, _lvl), t);
            if (S.EnableBearish) HandleCandidate(_det.Evaluate(t, -1, _lvl), t);

            // ---------- 4) fold the bar into rolling state ----------
            _levels.Update(b, bt);
            _volTod.Add(bt.Slot, bt.SessionId, b.Volume);
            _deltaTod.Add(bt.Slot, bt.SessionId, b.Delta);
            _rangeTod.Add(bt.Slot, bt.SessionId, b.Range);
            _atr.Add(b.High, b.Low, b.Close);
            _eff1.Add(eff);
            if (Math.Abs(st.DeltaZ) >= 1) _effInit.Add(eff);
            if (!double.IsNaN(eff4)) _eff4.Add(eff4);
            _thirdShares.Add(st.LowThirdShare);
            _thirdShares.Add(st.HighThirdShare);
        }

        /// <summary>Net move in ATR per unit of |delta| z-score (floor 0.25 so tiny deltas do not explode).</summary>
        public static double Efficiency(double move, double atr, double absDelta, double deltaStd) =>
            (move / Math.Max(atr, 1e-9)) / Math.Max(absDelta / Math.Max(deltaStd, 1e-9), 0.25);

        private static void ThirdShares(Bar b, out double low, out double high)
        {
            low = high = 0;
            if (b.Volume <= 0) return;
            double lo = b.Low + b.Range / 3 + 1e-9, hi = b.High - b.Range / 3 - 1e-9;
            for (int i = 0; i < b.Levels; i++)
            {
                double p = b.PriceAt(i), v = b.Bid[i] + b.Ask[i];
                if (p <= lo) low += v;
                if (p >= hi) high += v;
            }
            low /= b.Volume;
            high /= b.Volume;
        }

        // =====================================================================================
        // Reversal handling
        // =====================================================================================
        private void HandleCandidate(Candidate c, int t)
        {
            if (c == null) return;

            if (!c.Passed)
            {
                // negative samples: real candidates (initiative present) or near misses that only lack part of it
                bool nearMiss = !c.GateA && c.HasLevel && c.Reclaimed && c.SpeedOk && c.DropAtr >= 0.5 * S.A_DropAtr;
                bool inSession = S.SignalSession == SessionMode.Eth || _stats[t].T.IsRth;
                if (c.Score >= S.LogMinScore && inSession && (c.GateA || nearMiss))
                    StartReversalTracker(c, t, "REJ", c.Reject, null);
                return;
            }

            string reason = null;
            bool news = false;
            for (int i = c.First; i <= c.Last; i++) news |= _stats[i].T.News;
            if (S.SignalSession == SessionMode.Rth && !_stats[t].T.IsRth) reason = "outside session";
            else if (S.DeadMarketFilter && c.VolPct < S.DeadMarketPercentile) reason = "dead market";
            else if (S.SuppressNewsSignals && news) reason = "news";

            Context same = null, opposite = null;
            foreach (var x in _contexts)
            {
                if (x.Done) continue;
                if (x.Dir == c.Dir) same = x;
                else opposite = x;
            }
            if (reason == null && same != null && !(c.M.L < same.ExtremeO - 1e-9 || c.Score > same.Score))
                reason = "context active";

            if (reason != null)
            {
                c.Reject = reason;
                StartReversalTracker(c, t, "REJ", reason, null);
                return;
            }

            if (same != null) Close(same, t, "superseded", false);
            if (opposite != null) Close(opposite, t, "opposite reversal", true);

            var ctx = new Context
            {
                Id = _nextId++, Dir = c.Dir, Cand = c, RevBar = t, Score = c.Score,
                ExtremeO = c.M.L, RevHighO = _bars[c.Last].Oriented(c.Dir).H, RevPocO = c.M.Poc,
                RefDelta = Math.Min(c.RefDelta, -1), OriginO = c.OriginOpen, Atr = c.Atr,
                StopO = c.M.L - Math.Max(S.StopBufferTicks * Tick, S.StopBufferAtr * c.Atr),
                Waiting = true, WaitStart = t, News = news
            };
            _contexts.Add(ctx);

            // the row carries every feature the calibrated model may use
            var revRow = BuildReversalRow(c, t, "REV", null, ctx);
            double prob = double.NaN;
            if (Calibration != null)
            {
                prob = Calibration.Probability(Calibration.ReversalModel, revRow, Calibration.MinSamples);
                // score buckets were measured on calibrated scores: only valid when those weights are in use
                if (double.IsNaN(prob) && Calibration.ReversalModel == null && S.UseCalibratedWeights) prob = Calibration.ReversalProbability(c.Score);
                revRow.Set("p_model", prob);
            }
            var mark = new Mark
            {
                Bar = t, Dir = c.Dir, Type = MarkType.Reversal, Label = c.Score >= S.StrongScore ? "REV" : "rev",
                Price = c.Dir > 0 ? _bars[t].Low : _bars[t].High,
                Score = c.Score, Strong = c.Score >= S.StrongScore, ContextId = ctx.Id, Probability = prob,
                Tooltip = ReversalTooltip(c, prob, news)
            };
            Marks.Add(mark);
            Events.Add(new EngineEvent
            {
                Type = EngineEventType.Reversal, Bar = t, Dir = c.Dir, Price = c.ExtremeReal, Score = c.Score,
                Text = $"{(c.Dir > 0 ? "Bullish" : "Bearish")} reversal, skóre {c.Score:0} ({c.VariantName}, {c.Level.Name})"
            });
            ctx.RevRow = StartReversalTracker(c, t, "REV", null, ctx, revRow);
        }

        private LogRow BuildReversalRow(Candidate c, int t, string kind, string reject, Context ctx)
        {
            var row = new LogRow();
            int id = ctx?.Id ?? _nextId++;
            FillCommon(row, kind, id, ctx?.Id ?? 0, c.Dir, t);
            FillCandidate(row, c);
            row.Set("reject", reject ?? "");
            row.Set("rev_score", c.Score);
            return row;
        }

        private LogRow StartReversalTracker(Candidate c, int t, string kind, string reject, Context ctx, LogRow prebuilt = null)
        {
            var row = prebuilt ?? BuildReversalRow(c, t, kind, reject, ctx);
            double entry = c.M.C;
            double stop = c.M.L - Math.Max(S.StopBufferTicks * Tick, S.StopBufferAtr * c.Atr);
            var tr = MakeTracker(row, c.Dir, t, entry, stop, c.OriginOpen, c.Atr, kind);
            if (tr == null)
            {
                if (_log != null) { row.Set("complete", true); _log.Write(row); }
                return row;
            }
            tr.OnDone = done =>
            {
                if (kind == "REV") ReversalStats.Add(c.Score, done.PrimaryHit, done.ResultR, done.ResultR15);
                _log?.Write(done.Row);
            };
            _trackers.Add(tr);
            return row;
        }

        // =====================================================================================
        // Context state machine (layer 2)
        // =====================================================================================
        private sealed class Context
        {
            public int Id, Dir, RevBar, WaitStart, Confirmations, LastConfBar = -1;
            public Candidate Cand;
            public double Score, ExtremeO, RevHighO, RevPocO, RefDelta, OriginO, Atr, StopO;
            public bool Waiting, RetestDone, InTrade, AbsorptionWarned, Done, News;
            public readonly List<Zone> Zones = new List<Zone>();
            public LogRow RevRow;
        }

        private void UpdateContext(Context ctx, int t)
        {
            if (ctx.Done || t <= ctx.RevBar) return;
            var ob = _bars[t].Oriented(ctx.Dir);
            var st = _stats[t];
            double z = ctx.Dir * st.DeltaZ;

            // a new extreme beyond the reversal invalidates the context in any state
            if (ob.L < ctx.ExtremeO - 1e-9)
            {
                Count(ctx.Confirmations == 0 ? "ctx: new extreme before C1" : "ctx: new extreme after C1");
                Close(ctx, t, "new extreme", true);
                return;
            }

            // zones
            foreach (var zone in ctx.Zones)
            {
                if (zone.State != ZoneState.Active || t <= zone.StartBar) continue;
                double zp = ctx.Dir * zone.Price;
                if (ob.L <= zp + S.FillTolTicks * Tick + 1e-9)
                    FillZone(ctx, zone, t, ob, z);
                else if (t - zone.StartBar >= S.Q_Bars)
                    EndZone(ctx, zone, t, ZoneState.Expired, "expired");
                else if (ob.C < zp && z <= -S.ZoneFailDeltaZ)
                    EndZone(ctx, zone, t, ZoneState.Failed, "defence failed");
            }

            // absorption failed after entry
            if (ctx.InTrade && !ctx.AbsorptionWarned && ob.C < ctx.RevPocO && z <= -S.AbsorptionFailZ)
            {
                ctx.AbsorptionWarned = true;
                Marks.Add(new Mark
                {
                    Bar = t, Dir = ctx.Dir, Type = MarkType.AbsorptionFailed, Label = "!",
                    Price = ctx.Dir > 0 ? _bars[t].Low : _bars[t].High, ContextId = ctx.Id,
                    Tooltip = $"! Absorpce selhala: close za VPOC reversalu ({F(ctx.Dir * ctx.RevPocO, "0.##")}) se silnou protisměrnou deltou (z {F(ctx.Dir * z, "+0.0;-0.0")})"
                });
                Events.Add(new EngineEvent { Type = EngineEventType.AbsorptionFailed, Bar = t, Dir = ctx.Dir, Price = ctx.Dir * ob.C, Text = "Absorpce selhala: close pod VPOC reversalu se silnou protisměrnou deltou" });
            }

            // retest: higher low close to the reversal extreme on weak opposing delta
            if (!ctx.RetestDone && t - ctx.RevBar <= S.Retest_Lookback && t > ctx.RevBar + 1)
            {
                double minSince = double.MaxValue;
                for (int i = ctx.RevBar + 1; i < t; i++) minSince = Math.Min(minSince, _bars[i].Oriented(ctx.Dir).L);
                bool higherLow = ob.L > ctx.ExtremeO && ob.L <= ctx.ExtremeO + S.Retest_MaxDistAtr * ctx.Atr;
                bool pullback = ob.L <= minSince;
                bool quiet = Math.Abs(ob.Delta) <= S.Retest_MaxDeltaFrac * Math.Abs(ctx.RefDelta);
                if (higherLow && pullback && quiet)
                {
                    ctx.RetestDone = true;
                    Marks.Add(new Mark
                    {
                        Bar = t, Dir = ctx.Dir, Type = MarkType.Retest, Label = "R",
                        Price = ctx.Dir > 0 ? _bars[t].Low : _bars[t].High, ContextId = ctx.Id, Score = ctx.Score,
                        Tooltip = RetestTooltip(ctx, ob)
                    });
                    Events.Add(new EngineEvent
                    {
                        Type = EngineEventType.Retest, Bar = t, Dir = ctx.Dir, Price = ctx.Dir * ob.Poc, Score = ctx.Score,
                        Text = $"Retest {(ctx.Dir > 0 ? "bullish" : "bearish")} reversalu, VPOC {F(ctx.Dir * ob.Poc, "0.##")}"
                    });
                    if ((S.Zones & ZoneTypes.Retest) != 0)
                        CreateZone(ctx, t, ZoneTypes.Retest, ob.Poc, ctx.Score, double.NaN, null);
                    if (ctx.Confirmations < S.MaxConfirmations)
                    {
                        ctx.Waiting = true;
                        ctx.WaitStart = t;
                    }
                }
            }

            // confirmation candle
            if (ctx.Waiting && ctx.Confirmations < S.MaxConfirmations && t > ctx.LastConfBar)
            {
                if (t - ctx.WaitStart > S.P_Bars)
                {
                    ctx.Waiting = false;
                    Count(ctx.Confirmations == 0 ? "ctx: no C1 within P" : "ctx: no C2 within P");
                }
                else
                {
                    var conf = EvaluateConfirmation(ctx, t, ob, z);
                    if (conf != null && conf.Score < S.Conf_MinScore) Count("conf: score<min");
                    if (conf != null && conf.Score >= S.Conf_MinScore) Confirm(ctx, t, ob, conf);
                }
            }

            bool activeZone = false;
            foreach (var zone in ctx.Zones) activeZone |= zone.State == ZoneState.Active;
            if (!ctx.Waiting && !activeZone && !(ctx.InTrade && t - ctx.RevBar < 60))
                ctx.Done = true;
            if (t - ctx.RevBar > 120) ctx.Done = true;
        }

        private sealed class ConfResult
        {
            public double Score, Z, Clv, EffPct, VolPct;
            public int Imbalances;
            public bool Warning, AboveRevHigh;
        }

        private ConfResult EvaluateConfirmation(Context ctx, int t, OBar ob, double z)
        {
            var st = _stats[t];
            var prev = _bars[t - 1].Oriented(ctx.Dir);
            if (ob.L < ctx.ExtremeO) return null;
            if (!(ob.Delta > 0 && (z >= S.Conf_DeltaZ || ob.Delta >= S.Conf_DeltaFracOfFlush * Math.Abs(ctx.RefDelta)))) { Count("conf: delta"); return null; }
            if (ob.Clv < S.Conf_Clv) { Count("conf: clv"); return null; }
            if (ob.C <= prev.C) { Count("conf: close<=prev"); return null; }
            if (st.InitiativeEffPct < S.Conf_EfficiencyPct) { Count("conf: efficiency"); return null; }

            var r = new ConfResult { Z = z, Clv = ob.Clv, EffPct = st.InitiativeEffPct, VolPct = st.VolumePct };

            // stacked diagonal imbalances in the reversal direction
            int run = 0, best = 0;
            for (int k = 1; k < ob.Levels; k++)
            {
                if (ob.With[k] > 0 && ob.With[k] >= S.Conf_ImbalanceRatio * Math.Max(ob.Against[k - 1], 1)) best = Math.Max(best, ++run);
                else run = 0;
            }
            r.Imbalances = best;

            // warning: POC in the top quarter with net selling above it (selling into the rise)
            double above = 0;
            for (int k = ob.PocIndex + 1; k < ob.Levels; k++) above += ob.With[k] - ob.Against[k];
            double pocPos = ob.Range > 0 ? (ob.Poc - ob.L) / ob.Range : 0.5;
            r.Warning = pocPos >= 0.75 && above < 0;
            r.AboveRevHigh = ob.C > ctx.RevHighO;

            double sc = 50
                        + 15 * MathUtil.Ramp(z, S.Conf_DeltaZ, S.Conf_DeltaZ + 2)
                        + 10 * MathUtil.Ramp(ob.Clv, S.Conf_Clv, 1)
                        + 10 * MathUtil.Ramp(st.InitiativeEffPct, S.Conf_EfficiencyPct, 90)
                        + (best >= S.Conf_ImbalanceCount ? 8 : 0)
                        + (st.VolumePct >= S.Conf_VolumePct ? 5 : 0)
                        + (r.AboveRevHigh ? 7 : 0)
                        - (r.Warning ? 15 : 0);
            r.Score = Math.Max(0, Math.Min(100, sc));
            return r;
        }

        private void Confirm(Context ctx, int t, OBar ob, ConfResult conf)
        {
            ctx.Confirmations++;
            ctx.LastConfBar = t;
            ctx.Waiting = false;
            string label = "C" + ctx.Confirmations;
            var st = _stats[t];
            Marks.Add(new Mark
            {
                Bar = t, Dir = ctx.Dir, Type = MarkType.Confirmation, Label = label,
                Price = ctx.Dir > 0 ? _bars[t].Low : _bars[t].High, Score = conf.Score, ContextId = ctx.Id,
                Tooltip = ConfirmationTooltip(ctx, label, conf, ob)
            });
            Events.Add(new EngineEvent
            {
                Type = EngineEventType.Confirmation, Bar = t, Dir = ctx.Dir, Price = ctx.Dir * ob.Poc, Score = conf.Score,
                Text = $"{label} potvrzení {(ctx.Dir > 0 ? "bullish" : "bearish")} reversalu, skóre {F(conf.Score, "0")}, VPOC {F(ctx.Dir * ob.Poc, "0.##")}"
            });

            var row = new LogRow();
            FillCommon(row, "CONF", _nextId++, ctx.Id, ctx.Dir, t);
            FillCandidate(row, ctx.Cand);
            FillConf(row, ctx, conf);
            var tr = MakeTracker(row, ctx.Dir, t, ob.C, ctx.StopO, ctx.OriginO, ctx.Atr, "CONF");
            if (tr != null)
            {
                tr.OnDone = d => _log?.Write(d.Row);
                _trackers.Add(tr);
            }

            if ((S.Zones & ZoneTypes.ConfirmationVpoc) != 0) CreateZone(ctx, t, ZoneTypes.ConfirmationVpoc, ob.Poc, 0.5 * (ctx.Score + conf.Score), conf.Score, conf);
            if ((S.Zones & ZoneTypes.ConfirmationVal) != 0) CreateZone(ctx, t, ZoneTypes.ConfirmationVal, ob.ValueArea().val, 0.5 * (ctx.Score + conf.Score), conf.Score, conf);
            if ((S.Zones & ZoneTypes.ReversalVpoc) != 0) CreateZone(ctx, t, ZoneTypes.ReversalVpoc, ctx.RevPocO, 0.5 * (ctx.Score + conf.Score), conf.Score, conf);
        }

        // =====================================================================================
        // Zones, stops and targets
        // =====================================================================================
        private void CreateZone(Context ctx, int t, ZoneTypes type, double priceO, double score, double confScore, ConfResult conf)
        {
            double entry = priceO;
            double stop = ctx.StopO;
            double r = entry - stop;
            var row = new LogRow();
            FillCommon(row, "ZONE", _nextId, ctx.Id, ctx.Dir, t);
            FillCandidate(row, ctx.Cand);
            if (conf != null) FillConf(row, ctx, conf);
            row.Set("zone_type", ZoneTypeCode(type)).Set("zone_price", ctx.Dir * priceO).Set("zone_score", score)
               .Set("zone_retest", type == ZoneTypes.Retest);

            var targets = Targets(ctx.Dir, entry, r, ctx.OriginO, t, out int primary);
            // "first target" = T1 (origin of the move); without it the nearest structural target
            double first = FirstTarget(targets);
            double rr = !double.IsNaN(first) && r > 0 ? (first - entry) / r : double.NaN;
            row.Set("entry", ctx.Dir * entry).Set("stop", ctx.Dir * stop).Set("r_ticks", r / Tick).Set("rr_first", rr);

            var stNow = _stats[t];
            bool withTrend = !double.IsNaN(stNow.VwapSlope) && ctx.Dir * stNow.VwapSlope > 0;
            row.Set("with_trend", withTrend);
            string reject = r <= 0 ? "zone above stop"
                : !double.IsNaN(rr) && rr < S.MinRewardRisk ? "rr"
                : S.TrendFilter && !withTrend ? "against trend" : null;
            if (reject != null)
            {
                // not drawn: poor reward/risk is one of the main noise filters; still logged as a negative sample
                row.Set("reject", reject).Set("filled", false).Set("complete", true);
                _nextId++;
                _log?.Write(row);
                return;
            }

            var zone = new Zone
            {
                Id = _nextId++, ContextId = ctx.Id, Dir = ctx.Dir, Type = type, Price = ctx.Dir * priceO,
                StartBar = t, ExpiryBar = t + S.Q_Bars, Score = score, ReversalScore = ctx.Score, ConfirmationScore = confScore,
                Stop = ctx.Dir * stop, R = r, WithTrend = withTrend
            };
            if (Calibration != null)
            {
                zone.Probability = Calibration.Probability(Calibration.ZoneModel, row, Calibration.MinSamples);
                if (double.IsNaN(zone.Probability) && Calibration.ZoneModel == null && S.UseCalibratedWeights) zone.Probability = Calibration.ZoneProbability(score);
                row.Set("p_model", zone.Probability);
            }
            string[] names = { "T1 origin", "T2 VWAP", "T3 úroveň", "1.5R", "2R" };
            for (int j = 0; j < 5; j++)
                if (!double.IsNaN(targets[j])) zone.Targets.Add(new TargetLine { Name = names[j], Price = ctx.Dir * targets[j] });
            zone.Tooltip = ZoneTooltip(ctx, zone, rr);
            _pendingZoneRows[zone.Id] = row;
            _zoneTargets[zone.Id] = (targets, primary);
            ctx.Zones.Add(zone);
            Zones.Add(zone);
            Events.Add(new EngineEvent
            {
                Type = EngineEventType.NewZone, Bar = t, Dir = ctx.Dir, Price = zone.Price,
                Text = $"Nová zóna {zone.TypeName} {zone.Price.ToString("0.##", CultureInfo.InvariantCulture)} (skóre {score:0}, RR {rr:0.0})"
            });
        }

        private readonly Dictionary<int, LogRow> _pendingZoneRows = new Dictionary<int, LogRow>();
        private readonly Dictionary<int, (double[] targets, int primary)> _zoneTargets = new Dictionary<int, (double[], int)>();

        private void FillZone(Context ctx, Zone zone, int t, OBar ob, double z)
        {
            double zp = ctx.Dir * zone.Price;
            double entry = ob.O < zp ? ob.O : zp;   // gap through the limit fills at the open
            zone.State = ZoneState.Filled;
            zone.FillBar = t;
            zone.EndBar = t;
            zone.Entry = ctx.Dir * entry;
            ctx.InTrade = true;

            var row = _pendingZoneRows[zone.Id];
            _pendingZoneRows.Remove(zone.Id);
            row.Set("filled", true).Set("bars_to_fill", t - zone.StartBar).Set("fill_delta_z", z).Set("entry", ctx.Dir * entry);
            var (targets, primary) = _zoneTargets[zone.Id];
            _zoneTargets.Remove(zone.Id);
            double r = entry - ctx.StopO;
            var tr = new Tracker
            {
                Row = row, Dir = ctx.Dir, Kind = "ZONE", EntryBar = t, Entry = entry, Stop = ctx.StopO, R = Math.Max(r, Tick),
                Tick = Tick, TolTarget = Math.Max(S.TargetTolTicks * Tick, S.TargetTolAtr * ctx.Atr),
                Horizons = S.Horizons, VBars = S.VReversalBars, Zone = zone, Primary = primary
            };
            Array.Copy(targets, tr.Targets, 5);
            // R multiples are relative to the actual entry
            tr.Targets[3] = entry + S.TargetR1 * r;
            tr.Targets[4] = entry + S.TargetR2 * r;
            WriteTargets(row, ctx.Dir, tr.Targets);
            tr.OnDone = d =>
            {
                ZoneStats.Add(zone.Score, d.PrimaryHit, d.ResultR, d.ResultR15);
                if (!ZoneTypeStats.TryGetValue(zone.Type, out var agg)) ZoneTypeStats[zone.Type] = agg = new double[5];
                agg[0]++;
                if (d.PrimaryHit) agg[1]++;
                if (!double.IsNaN(d.ResultR)) agg[2] += d.ResultR;
                if (!double.IsNaN(d.ResultR15)) { agg[3] += d.ResultR15; if (d.ResultR15 > 0) agg[4]++; }
                _log?.Write(d.Row);
            };
            tr.OnEntryBar(ob, t);
            _trackers.Add(tr);

            Events.Add(new EngineEvent
            {
                Type = EngineEventType.ZoneFilled, Bar = t, Dir = ctx.Dir, Price = zone.Entry,
                Text = $"Fill zóny {zone.TypeName} {zone.Entry.ToString("0.##", CultureInfo.InvariantCulture)}, stop {zone.Stop.ToString("0.##", CultureInfo.InvariantCulture)}"
            });
        }

        private void EndZone(Context ctx, Zone zone, int t, ZoneState state, string why)
        {
            zone.State = state;
            zone.EndBar = t;
            LogZoneUnfilled(ctx, zone, why);
            if (!ctx.InTrade && ctx.Confirmations < S.MaxConfirmations && !ctx.Waiting)
            {
                // diagram: zone expired -> wait for another confirmation (max 2 per context)
                ctx.Waiting = true;
                ctx.WaitStart = t;
            }
        }

        private void LogZoneUnfilled(Context ctx, Zone zone, string why)
        {
            if (!_pendingZoneRows.TryGetValue(zone.Id, out var row)) return;
            _pendingZoneRows.Remove(zone.Id);
            _zoneTargets.Remove(zone.Id);
            row.Set("filled", false).Set("reject", why).Set("complete", true);
            _log?.Write(row);
        }

        private void Close(Context ctx, int t, string why, bool mark)
        {
            if (ctx.Done) return;
            ctx.Done = true;
            foreach (var z in ctx.Zones)
                if (z.State == ZoneState.Active)
                {
                    z.State = ZoneState.Cancelled;
                    z.EndBar = t;
                    LogZoneUnfilled(ctx, z, why);
                }
            if (!mark) return;
            string text = "Kontext zrušen: " + ReasonText(why);
            Marks.Add(new Mark
            {
                Bar = t, Dir = ctx.Dir, Type = MarkType.ContextCancelled, Label = "×",
                Price = ctx.Dir > 0 ? _bars[t].Low : _bars[t].High, ContextId = ctx.Id, Tooltip = text
            });
            // alert only when the trader may have acted on the context (a zone was drawn)
            if (ctx.Zones.Count > 0)
                Events.Add(new EngineEvent { Type = EngineEventType.ContextCancelled, Bar = t, Dir = ctx.Dir, Text = text });
        }

        private static string ReasonText(string why)
        {
            switch (why)
            {
                case "new extreme": return "nový extrém za reversalem";
                case "opposite reversal": return "opačný reversal";
                case "superseded": return "nahrazen silnějším reversalem";
                case "roll": return "roll kontraktu";
                default: return why;
            }
        }

        /// <summary>T1 origin, T2 session VWAP, T3 nearest opposing level, 1.5R, 2R (oriented, NaN when not above entry).</summary>
        private double[] Targets(int dir, double entry, double r, double originO, int t, out int primary)
        {
            var tg = new double[5];
            for (int i = 0; i < 5; i++) tg[i] = double.NaN;
            var st = _stats[t];
            double tol = Math.Max(S.TargetTolTicks * Tick, S.TargetTolAtr * st.Atr);
            if (S.TargetOrigin && originO > entry + tol) tg[0] = originO;
            if (S.TargetVwap && !double.IsNaN(st.Vwap) && dir * st.Vwap > entry + tol) tg[1] = dir * st.Vwap;
            if (S.TargetLevel)
            {
                double best = double.MaxValue;
                foreach (var l in _lvl)
                {
                    double p = dir * l.Price;
                    if (p > entry + tol && p < best) best = p;
                }
                if (best < double.MaxValue) tg[2] = best;
            }
            if (r > 0)
            {
                tg[3] = entry + S.TargetR1 * r;
                tg[4] = entry + S.TargetR2 * r;
            }
            // primary target for results: T1 origin when available, else the nearest structural target, else 2R
            primary = !double.IsNaN(tg[0]) ? 0 : -1;
            if (primary < 0)
            {
                double best = double.MaxValue;
                for (int j = 1; j < 3; j++)
                    if (!double.IsNaN(tg[j]) && tg[j] < best) { best = tg[j]; primary = j; }
            }
            if (primary < 0 && r > 0) primary = 4;
            return tg;
        }

        private static double FirstTarget(double[] tg)
        {
            if (!double.IsNaN(tg[0])) return tg[0];
            double first = double.NaN;
            for (int j = 1; j < 3; j++)
                if (!double.IsNaN(tg[j]) && (double.IsNaN(first) || tg[j] < first)) first = tg[j];
            return first;
        }

        private Tracker MakeTracker(LogRow row, int dir, int t, double entry, double stop, double originO, double atr, string kind)
        {
            double r = entry - stop;
            if (r <= 0) return null;
            var tg = Targets(dir, entry, r, originO, t, out int primary);
            var tr = new Tracker
            {
                Row = row, Dir = dir, Kind = kind, EntryBar = t, Entry = entry, Stop = stop, R = r, Tick = Tick,
                TolTarget = Math.Max(S.TargetTolTicks * Tick, S.TargetTolAtr * atr),
                Horizons = S.Horizons, VBars = S.VReversalBars, Primary = primary
            };
            Array.Copy(tg, tr.Targets, 5);
            double first = FirstTarget(tg);
            row.Set("entry", dir * entry).Set("stop", dir * stop).Set("r_ticks", r / Tick)
               .Set("rr_first", double.IsNaN(first) ? double.NaN : (first - entry) / r);
            WriteTargets(row, dir, tg);
            return tr;
        }

        private static void WriteTargets(LogRow row, int dir, double[] tg)
        {
            row.Set("t1", dir * tg[0]).Set("t2", dir * tg[1]).Set("t3", dir * tg[2]);
            row.Set("t_first", dir * FirstTarget(tg));
        }

        // =====================================================================================
        // Log helpers
        // =====================================================================================
        private void FillCommon(LogRow row, string kind, int id, int ctxId, int dir, int t)
        {
            var b = _bars[t];
            var st = _stats[t];
            row.Set("id", id).Set("kind", kind).Set("context_id", ctxId).Set("dir", dir).Set("bar", t)
               .Set("time_utc", b.TimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
               .Set("time_et", st.T.Local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
               .Set("session", st.T.SessionId).Set("min_from_rth", st.T.MinutesFromRth).Set("rth", st.T.IsRth).Set("news", st.T.News)
               .Set("atr", st.Atr).Set("atr_classic", st.AtrClassic).Set("tod_baseline", st.TodBaseline)
               .Set("volume", b.Volume).Set("volume_pct", st.VolumePct).Set("delta", b.Delta).Set("delta_z", st.DeltaZ);
            if (!double.IsNaN(st.Vwap) && st.Atr > 0)
            {
                double side = Math.Sign(b.Close - st.Vwap) * dir;
                row.Set("vwap_side", side).Set("vwap_dist_atr", dir * (b.Close - st.Vwap) / st.Atr);
                if (!double.IsNaN(st.VwapSlope)) row.Set("vwap_slope_atr", dir * st.VwapSlope / st.Atr);
            }
        }

        private void FillCandidate(LogRow row, Candidate c)
        {
            row.Set("variant", c.VariantName).Set("score", c.Score).Set("strong", c.Score >= S.StrongScore);
            if (c.HasLevel)
                row.Set("level", c.Level.Kind.ToString()).Set("level_price", c.Level.Price).Set("level_weight", c.Level.Weight)
                   .Set("level_dist_ticks", c.LevelDistTicks).Set("confluence", c.Confluence).Set("bars_beyond", c.BarsBeyond).Set("test_order", c.TestOrder);
            string[] cols = { "s_A", "s_B", "s_C", "s_D", "s_E", "s_F", "s_G", "s_H", "s_I" };
            for (int i = 0; i < 9; i++) row.Set(cols[i], c.S[i]);
            row.Set("h_applicable", c.HApplicable).Set("i_applicable", c.IApplicable)
               .Set("drop_atr", c.DropAtr).Set("min_z", c.MinZ).Set("sweep", c.SweepOk).Set("overshoot_atr", c.OvershootAtr)
               .Set("div1", c.Div1).Set("div2", c.Div2).Set("flip", c.Flip).Set("poc_pos", c.PocPos).Set("third_pct", c.ThirdPct)
               .Set("pattern_vol_pct", c.VolPct).Set("rho", c.Rho).Set("fin_auction", c.FinAuction).Set("clv", c.Clv)
               .Set("flush_z", c.FlushZ).Set("flush_vol_pct", c.FlushVolPct);
            if (c.IApplicable) row.Set("stall_bars", c.StallBars).Set("stall_z", c.StallZ).Set("stall_eff_pct", c.StallEffPct);
        }

        private void FillConf(LogRow row, Context ctx, ConfResult conf)
        {
            row.Set("rev_score", ctx.Score).Set("conf_score", conf.Score).Set("conf_n", ctx.Confirmations)
               .Set("conf_delta_z", conf.Z).Set("conf_clv", conf.Clv).Set("conf_eff_pct", conf.EffPct)
               .Set("conf_imbalances", conf.Imbalances).Set("conf_vol_pct", conf.VolPct).Set("conf_warning", conf.Warning);
        }

        private static string ZoneTypeCode(ZoneTypes t)
        {
            switch (t)
            {
                case ZoneTypes.ConfirmationVpoc: return "conf_vpoc";
                case ZoneTypes.ConfirmationVal: return "conf_val";
                case ZoneTypes.ReversalVpoc: return "rev_vpoc";
                default: return "retest";
            }
        }

        // Czech number format without depending on installed cultures (decimal comma)
        private static readonly NumberFormatInfo Cz = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };
        private static string F(double v, string fmt) => v.ToString(fmt, Cz);

        /// <summary>Plain-language explanation of a reversal (only WGL4 glyphs so every Windows font renders it).</summary>
        private string ReversalTooltip(Candidate c, double prob, bool news)
        {
            bool bull = c.Dir > 0;
            string ext = bull ? "low" : "high";
            string against = bull ? "prodejci" : "kupci";
            string other = bull ? "high" : "low";
            var sb = new StringBuilder();
            sb.Append(bull ? "▲ BULLISH REVERSAL" : "▼ BEARISH REVERSAL").Append("   skóre ").Append(F(c.Score, "0"));
            if (c.Score >= S.StrongScore) sb.Append(" (silný)");
            if (!double.IsNaN(prob)) sb.Append('\n').Append("Úspěšnost podobných signálů (T1 před stopem): ").Append(F(prob * 100, "0")).Append(" %");
            sb.Append('\n').Append("Úroveň: ").Append(c.Level.Name).Append(' ').Append(F(c.Level.Price, "0.##"));
            if (c.Confluence > 0) sb.Append("  (+").Append(c.Confluence).Append(c.Confluence == 1 ? " další úroveň)" : " další úrovně)");
            sb.Append('\n').Append("Varianta: ").Append(c.VariantName).Append(c.Variant switch
            {
                Variant.OneBar => " (jedna svíčka)",
                Variant.Stall => " (absorpce několika svíček + spouštěč)",
                _ => " (flush + návrat)"
            });
            sb.Append('\n');

            void Line(int i, string label, string detail)
            {
                sb.Append('\n').Append(c.S[i] >= 0.6 ? "● " : "○ ").Append(label);
                if (!string.IsNullOrEmpty(detail)) sb.Append(": ").Append(detail);
            }

            Line(0, bull ? "A prodejní tlak předtím" : "A nákupní tlak předtím", $"pohyb {F(c.DropAtr, "0.0")}× ATR, delta z {F(c.Dir * c.MinZ, "+0.0;-0.0")}");
            Line(1, "B test úrovně", c.LevelDistTicks >= 0 ? $"{F(c.LevelDistTicks, "0")} t před úrovní" : $"{F(-c.LevelDistTicks, "0")} t za úroveň");
            Line(2, "C sweep a rychlý návrat", (c.SweepOk > 0 ? $"vybral předchozí {ext}y" : "bez sweepu") +
                                                    (c.BarsBeyond == 0 ? ", návrat hned" : $", návrat po {c.BarsBeyond} sv."));
            var d = new List<string>();
            if (c.Div1 > 0) d.Add("delta silnější než u minulého swingu");
            if (c.Div2 > 0) d.Add("kumul. delta bez nového extrému");
            if (c.Flip > 0) d.Add("delta se ve svíčce otočila");
            Line(3, "D divergence delty", d.Count > 0 ? string.Join(", ", d) : "ne");
            Line(4, $"E absorpce u {ext}u", $"POC {F(c.PocPos * 100, "0")} % cesty od {ext}u, objem {F(c.VolPct, "0")}. percentil");
            Line(5, $"F vyčerpání na {ext}u", $"{against} slábnou k {ext}u, na samém {ext}u {F(c.FinAuction * 100, "0")} % průměru");
            Line(6, "G close", $"{F(c.Clv * 100, "0")} % cesty od {ext}u k {other}u");
            if (c.HApplicable)
                Line(7, $"H selhaný flush ({against} chyceni)", double.IsNaN(c.FlushZ) ? "" : $"delta z {F(c.Dir * c.FlushZ, "+0.0;-0.0")}, objem {F(c.FlushVolPct, "0")}. pct");
            if (c.IApplicable)
                Line(8, "I stall (absorpce)", $"{c.StallBars} svíček, delta z {F(c.Dir * c.StallZ, "+0.0;-0.0")} bez posunu ceny");
            if (news) sb.Append("\n\n! svíčka v okně ekonomických zpráv");
            return sb.ToString();
        }

        private string ConfirmationTooltip(Context ctx, string label, ConfResult conf, OBar ob)
        {
            bool bull = ctx.Dir > 0;
            var sb = new StringBuilder();
            sb.Append(label).Append("  POTVRZENÍ ").Append(bull ? "bullish" : "bearish").Append(" reversalu   skóre ").Append(F(conf.Score, "0"));
            sb.Append('\n').Append(bull ? "Kupci převzali iniciativu a low reversalu drží." : "Prodejci převzali iniciativu a high reversalu drží.");
            sb.Append('\n');
            sb.Append('\n').Append("● delta z ").Append(F(ctx.Dir * conf.Z, "+0.0;-0.0")).Append(bull ? " (agresivní nákupy)" : " (agresivní prodeje)");
            sb.Append('\n').Append("● close ").Append(F(conf.Clv * 100, "0")).Append(bull ? " % cesty od lowu k highu, nad close předchozí svíčky" : " % cesty od highu k lowu, pod close předchozí svíčky");
            sb.Append('\n').Append("● efektivita ").Append(F(conf.EffPct, "0")).Append(". percentil (cena se opravdu pohnula)");
            sb.Append('\n').Append(conf.Imbalances >= S.Conf_ImbalanceCount ? "● " : "○ ").Append(bull ? "ask" : "bid").Append(" imbalance v řadě: ").Append(conf.Imbalances);
            sb.Append('\n').Append(conf.VolPct >= S.Conf_VolumePct ? "● " : "○ ").Append("objem ").Append(F(conf.VolPct, "0")).Append(". percentil");
            sb.Append('\n').Append(conf.AboveRevHigh ? "● " : "○ ").Append(bull ? "close nad high reversalu" : "close pod low reversalu");
            if (conf.Warning) sb.Append('\n').Append(bull ? "! POC nahoře a prodej nad POC (prodej do růstu)" : "! POC dole a nákup pod POC (nákup do poklesu)");
            sb.Append('\n').Append('\n').Append("VPOC svíčky (úroveň pro limit): ").Append(F(ctx.Dir * ob.Poc, "0.##"));
            return sb.ToString();
        }

        private string RetestTooltip(Context ctx, OBar ob)
        {
            bool bull = ctx.Dir > 0;
            var sb = new StringBuilder();
            sb.Append("R  RETEST ").Append(bull ? "bullish" : "bearish").Append(" reversalu");
            sb.Append('\n').Append("Cena se vrátila k ").Append(bull ? "low" : "high").Append(" reversalu, ale ").Append(bull ? "nové low" : "nové high").Append(" neudělala:");
            sb.Append('\n').Append("● ").Append(bull ? "vyšší low " : "nižší high ").Append(F((ob.L - ctx.ExtremeO) / Tick, "0")).Append(" t od extrému reversalu");
            sb.Append('\n').Append("● slabá delta: ").Append(F(Math.Abs(ob.Delta) / Math.Abs(ctx.RefDelta) * 100, "0")).Append(" % nejsilnější svíčky pohybu");
            sb.Append('\n').Append(bull ? "Prodejci už nemají sílu tlačit cenu níž." : "Kupci už nemají sílu tlačit cenu výš.");
            sb.Append('\n').Append('\n').Append("VPOC svíčky (úroveň pro limit): ").Append(F(ctx.Dir * ob.Poc, "0.##"));
            return sb.ToString();
        }

        private string ZoneTooltip(Context ctx, Zone z, double rr)
        {
            var sb = new StringBuilder();
            sb.Append("Zóna ").Append(z.TypeName).Append("  ").Append(z.Price.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append('\n').Append("Skóre ").Append(z.Score.ToString("0", CultureInfo.InvariantCulture))
              .Append(" (reversal ").Append(z.ReversalScore.ToString("0", CultureInfo.InvariantCulture));
            if (!double.IsNaN(z.ConfirmationScore)) sb.Append(", potvrzení ").Append(z.ConfirmationScore.ToString("0", CultureInfo.InvariantCulture));
            sb.Append(')');
            if (!double.IsNaN(z.Probability)) sb.Append('\n').Append("P(T1 před stopem) = ").Append((z.Probability * 100).ToString("0", CultureInfo.InvariantCulture)).Append(" %");
            sb.Append('\n').Append("Stop ").Append(z.Stop.ToString("0.##", CultureInfo.InvariantCulture))
              .Append("  R = ").Append((z.R / Tick).ToString("0", CultureInfo.InvariantCulture)).Append(" t");
            if (!double.IsNaN(rr)) sb.Append("  RR k 1. cíli ").Append(rr.ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append('\n').Append(z.WithTrend ? "Po směru VWAP trendu" : "Proti VWAP trendu (slabší v backtestu)");
            foreach (var t in z.Targets) sb.Append('\n').Append(t.Name).Append(": ").Append(t.Price.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append('\n').Append("Úroveň reversalu: ").Append(ctx.Cand.Level.Name).Append(", ").Append(ctx.Cand.VariantName);
            return sb.ToString();
        }
    }
}
