using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReversalConfirmation.Core
{
    public enum SessionMode
    {
        /// <summary>Electronic session 18:00–17:00 ET (CME Globex day).</summary>
        Eth,
        /// <summary>Regular trading hours 09:30–16:00 ET only.</summary>
        Rth
    }

    /// <summary>The part of the day the trader trades. Signals, contexts and calibration are kept inside it.</summary>
    public enum TradeWindow
    {
        /// <summary>US session on the trader's clock (default 15:30–22:12 Prague = 09:30–16:12 New York).</summary>
        Rth,
        /// <summary>European morning on the trader's clock (default 08:00–15:00 Prague).</summary>
        Eth,
        /// <summary>Whole Globex day (as in the original spec).</summary>
        All
    }

    /// <summary>How value areas are built.</summary>
    public enum ProfileSource
    {
        /// <summary>Volume profile: traded volume at each price (from the footprint).</summary>
        Volume,
        /// <summary>Market profile: TPO count, i.e. in how many 30-minute periods the price traded.</summary>
        Tpo
    }

    public enum AtrMode
    {
        /// <summary>Wilder ATR(14) of the chart timeframe, exactly as in the spec.</summary>
        Classic,
        /// <summary>
        /// max(ATR14, median range of the same time-of-day slot over the last D sessions).
        /// ATR14 lags at the RTH open (overnight bars are ~2.5x smaller). Optional: in the 2024–2026 ES back-test
        /// it did not beat the classic ATR, so the spec's ATR14 stays the default.
        /// </summary>
        Hybrid
    }

    [Flags]
    public enum ZoneTypes
    {
        None = 0,
        ConfirmationVpoc = 1,
        ConfirmationVal = 2,
        ReversalVpoc = 4,
        Retest = 8
    }

    /// <summary>Component identifiers; index into weight arrays.</summary>
    public enum Comp
    {
        A = 0, B, C, D, E, F, G, H, I
    }

    /// <summary>
    /// All tunable parameters. Defaults follow the specification; deviations are documented in docs/DESIGN.md.
    /// Every threshold is a percentile, a z-score or a multiple of ATR (with a floor in ticks).
    /// </summary>
    public sealed class EngineSettings
    {
        // ---------------- Session ----------------
        public string TimeZoneId = "America/New_York";
        public TimeSpan EthStart = new TimeSpan(18, 0, 0);
        public TimeSpan RthStart = new TimeSpan(9, 30, 0);
        public TimeSpan RthEnd = new TimeSpan(16, 0, 0);
        public SessionMode VwapSession = SessionMode.Eth;
        public SessionMode PriorDayProfile = SessionMode.Rth;
        /// <summary>News times in the session time zone (ET), separated by ';'.</summary>
        public string NewsTimes = "08:30;10:00;14:00";
        public int NewsWindowMinutes = 5;
        public bool SuppressNewsSignals = false;

        // ---------------- Trading window (trader's clock) ----------------
        /// <summary>
        /// Window in which reversals, confirmations and entries are signalled. RTH and the European morning have
        /// different volatility and volume, so each window has its own calibration; bars outside it only feed
        /// levels and statistics.
        /// </summary>
        public TradeWindow Window = TradeWindow.Rth;
        /// <summary>Trader's time zone for the window times below.</summary>
        public string LocalTimeZoneId = "Europe/Prague";
        /// <summary>
        /// RTH window on the trader's clock. It is anchored to the exchange: the times are converted with the normal
        /// (standard-time) difference to <see cref="TimeZoneId"/>, so in the weeks when the US and Europe change
        /// clocks on different dates the window moves with New York (e.g. 14:30 Prague instead of 15:30).
        /// </summary>
        public TimeSpan RthWindowFrom = new TimeSpan(15, 30, 0);
        public TimeSpan RthWindowTo = new TimeSpan(22, 12, 0);
        /// <summary>ETH (European morning) window on the trader's clock.</summary>
        public TimeSpan EthWindowFrom = new TimeSpan(8, 0, 0);
        public TimeSpan EthWindowTo = new TimeSpan(15, 0, 0);
        public bool DeadMarketFilter = true;
        public double DeadMarketPercentile = 20;

        // ---------------- Adaptivity ----------------
        public int TodDays = 10;
        /// <summary>Neighbouring time-of-day slots pooled on each side (1 = slot-1..slot+1).</summary>
        public int TodNeighborSlots = 1;
        public int TodMinSamples = 15;
        public int RollingFallbackBars = 100;
        public int DistributionWindow = 500;
        public int AtrPeriod = 14;
        public AtrMode AtrMode = AtrMode.Classic;
        public int MinTicks = 2;

        // ---------------- Levels ----------------
        public double WPrevVwap = 1.0;
        public double WSessionVwap = 1.0;
        public double WVwapBands = 0.6;
        public double WFirstRthBar = 1.0;
        public double WOpeningRange = 0.8;
        public double WRthOpen = 0.8;
        public double WPrevDayHl = 0.9;
        public double WPrevDayPoc = 1.0;
        public double WPrevDayVa = 0.9;
        public double WOvernight = 0.8;
        public double WLiquidityPool = 0.7;
        public double WSwing = 0.6;
        public double WManual = 1.0;
        public string ManualLevels = "";
        public double LevelTolAtr = 0.15;
        public int LevelTolTicks = 2;
        public double ConfluenceStep = 0.15;
        public double ConfluenceMax = 0.30;
        public double PoolBandAtr = 0.1;
        public int PoolBandTicks = 2;
        public int PoolLookback = 24;
        public int SwingStrength = 3;

        // ---------------- Market / volume profile levels ----------------
        /// <summary>Value areas from volume at price (VP) or from TPO counts (MP). Applies to every profile below.</summary>
        public ProfileSource ProfileType = ProfileSource.Volume;
        public double ValueAreaShare = 0.70;
        public int TpoMinutes = 30;
        /// <summary>Today's developing VAH / VAL / POC (RTH profile in RTH, the Globex session before the open).</summary>
        public double WDevVa = 0.7;
        /// <summary>Developing value area is used only after this many minutes of profile.</summary>
        public int DevVaMinMinutes = 60;
        /// <summary>This week's developing VAH / VAL / POC (from the second session of the week).</summary>
        public double WWeekVa = 0.8;
        /// <summary>Previous week's VAH / VAL / POC.</summary>
        public double WPrevWeekVa = 0.9;
        /// <summary>Profile of the weekly value areas: ETH = all trades of the week (default), RTH = only RTH.</summary>
        public SessionMode WeekProfile = SessionMode.Eth;
        /// <summary>Naked (virgin) VAH / VAL / POC of earlier days that price has not traded at since.</summary>
        public double WNakedVa = 1.0;
        /// <summary>How many sessions back naked levels are kept (the previous day is already a level of its own).</summary>
        public int NakedMaxDays = 10;

        // ---------------- Reversal (layer 1) ----------------
        public bool EnableBullish = true;
        public bool EnableBearish = true;
        public double[] Weights = { 8, 15, 12, 8, 15, 7, 7, 15, 13 };
        public int MaxMergedBars = 2;
        public bool EnableStall = true;

        public int A_Bars = 6;
        public double A_DropAtr = 2.0;
        public double A_DeltaZ = 2.0;

        public int C_SweepBars = 5;
        public double C_OvershootAtr = 0.5;
        public int C_MaxBarsBeyond = 2;
        public int C_SecondTestLookback = 12;

        public int D_SwingLookback = 24;

        public double E_PocZone = 0.35;
        public double E_ExtremeThirdPct = 70;
        public double E_VolumePct = 70;

        public int F_Levels = 5;
        public double F_Spearman = 0.5;
        public double F_FinishedAuction = 0.15;

        public double G_Clv = 0.5;

        public double H_FlushZ = 2.5;
        public double H_FlushVolPct = 80;
        public double H_ExtremeBonus = 1.25;

        public int I_MinBars = 3;
        public int I_MaxBars = 6;
        public double I_BandAtr = 0.2;
        public int I_BandTicks = 3;
        public double I_CumDeltaZ = 1.5;
        public double I_ProgressAtr = 0.1;
        public int I_ProgressTicks = 2;
        public double I_EfficiencyPct = 20;
        public double I_TriggerZ = 1.5;

        /// <summary>Replace the weights A–I by those in the calibration JSON (probabilities are shown either way).</summary>
        public bool UseCalibratedWeights = false;
        public double MinScore = 60;
        public double StrongScore = 75;
        public double LogMinScore = 40;

        // ---------------- Confirmation (layer 2) ----------------
        public int P_Bars = 6;
        public int MaxConfirmations = 2;
        public double Conf_DeltaZ = 1.0;
        public double Conf_DeltaFracOfFlush = 0.3;
        public double Conf_Clv = 0.6;
        public double Conf_EfficiencyPct = 40;
        public double Conf_ImbalanceRatio = 3.0;
        public int Conf_ImbalanceCount = 3;
        public double Conf_VolumePct = 60;
        public double Conf_MinScore = 60;
        /// <summary>
        /// Confirmation requires a break of structure (BOS): bullish = close above the first pivot high found going
        /// left from the reversal low (a candle whose high sticks out above the candle to its left); if the low
        /// candle itself is an outside bar (new low and high above the previous candle), its own high is the level.
        /// Bearish is the mirror.
        /// </summary>
        public bool RequireBos = true;
        /// <summary>true = the break needs a close beyond the level, false = a wick through it is enough.</summary>
        public bool BosOnClose = true;
        /// <summary>
        /// An order-flow confirmation without the break yet stays pending this long; when the BOS comes within it the
        /// confirmation becomes valid on the breaking candle, otherwise it expires.
        /// </summary>
        public int BosWaitMinutes = 30;
        public int BosLookback = 40;
        /// <summary>Fibo entry without a confirmation: how long after the extreme the BOS may come.</summary>
        public int BosMaxBars = 24;

        // ---------------- Fibo entry after BOS (návrat do F5 / F7) ----------------
        /// <summary>After the BOS, mark the first return into the F5–F7 retracement of the impulse A→B.</summary>
        public bool FiboEntry = true;
        public double FibF5 = 0.618;
        public double FibF7 = 0.789;
        /// <summary>Bars after the BOS within which the return must come.</summary>
        public int FibMaxBars = 36;
        /// <summary>0 = a touch of F5/F7 counts as filled, 1 = price must trade one tick through (conservative).</summary>
        public int FibFillThroughTicks = 0;
        /// <summary>
        /// The F mark needs a valid order-flow confirmation (C1/C2) first. Off by default: in the 2024–2026 back-test
        /// the F7 entry after the BOS was not better with the order-flow confirmation (docs/BACKTEST.md).
        /// </summary>
        public bool FiboRequireConf = false;

        // ---------------- Zones ----------------
        public ZoneTypes Zones = ZoneTypes.ConfirmationVpoc | ZoneTypes.Retest;
        /// <summary>
        /// Draw zones only when the session VWAP slope (12 bars) points in the trade direction.
        /// Back-test: trend-aligned zones were better in both the training and the hold-out period (docs/BACKTEST.md).
        /// </summary>
        public bool TrendFilter = false;
        public int Q_Bars = 6;
        /// <summary>Fill when Low &lt;= zone + N ticks (spec default +1). Negative = trade-through required.</summary>
        public int FillTolTicks = 1;
        public double ZoneFailDeltaZ = 1.5;
        public double Retest_MaxDistAtr = 0.3;
        public double Retest_MaxDeltaFrac = 0.4;
        public int Retest_Lookback = 12;

        // ---------------- Stop / targets ----------------
        public double StopBufferAtr = 0.05;
        public int StopBufferTicks = 2;
        public bool TargetOrigin = true;
        public bool TargetVwap = true;
        public bool TargetLevel = true;
        public double TargetR1 = 1.5;
        public double TargetR2 = 2.0;
        public double TargetTolAtr = 0.1;
        public int TargetTolTicks = 2;
        public double MinRewardRisk = 1.2;
        public double AbsorptionFailZ = 2.0;

        // ---------------- Outcomes ----------------
        public int[] Horizons = { 3, 6, 12, 24, 36 };
        public int VReversalBars = 6;

        public double TotalWeight(bool withH, bool withI)
        {
            double s = 0;
            for (int i = 0; i <= (int)Comp.G; i++) s += Weights[i];
            if (withH) s += Weights[(int)Comp.H];
            if (withI) s += Weights[(int)Comp.I];
            return s;
        }

        public List<TimeSpan> ParseNewsTimes()
        {
            var list = new List<TimeSpan>();
            if (string.IsNullOrWhiteSpace(NewsTimes)) return list;
            foreach (var part in NewsTimes.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (TimeSpan.TryParseExact(part.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var ts))
                    list.Add(ts);
            return list;
        }

        public List<double> ParseManualLevels()
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(ManualLevels)) return list;
            foreach (var part in ManualLevels.Split(new[] { ';', ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim().Replace(',', '.');
                if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                    list.Add(v);
            }
            return list;
        }

        public EngineSettings Clone()
        {
            var c = (EngineSettings)MemberwiseClone();
            c.Weights = (double[])Weights.Clone();
            c.Horizons = (int[])Horizons.Clone();
            return c;
        }
    }
}
