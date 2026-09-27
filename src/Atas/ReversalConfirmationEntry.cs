using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using ReversalConfirmation.Core;
using Color = System.Drawing.Color;

namespace ReversalConfirmation.Atas
{
    public enum DisplayMode
    {
        [Display(Name = "Jen vstupy")] EntriesOnly,
        [Display(Name = "Vše")] All
    }

    public enum SessionChoice
    {
        [Display(Name = "ETH (18:00–17:00 ET)")] Eth,
        [Display(Name = "RTH (09:30–16:00 ET)")] Rth
    }

    public enum AtrChoice
    {
        [Display(Name = "Klasický ATR14")] Classic,
        [Display(Name = "Hybrid: max(ATR14, typický range času dne)")] Hybrid
    }

    /// <summary>
    /// Reversal &amp; Confirmation Entry for ATAS. All logic lives in <see cref="ReversalEngine"/> (shared with the
    /// back-test tool); this class only feeds closed footprint candles into it and draws the result.
    /// Performance: every candle is analysed exactly once when it closes (no work on ticks except an optional
    /// zone-touch alert), drawing iterates only the visible bars, CSV logging runs on a background thread.
    /// </summary>
    [DisplayName("Reversal & Confirmation Entry")]
    [Category(IndicatorCategories.VolumeOrderFlow)]
    public class ReversalConfirmationEntry : Indicator
    {
        private const string GSession = "1. Session";
        private const string GLevels = "2. Úrovně";
        private const string GReversal = "3. Reversal";
        private const string GConfirm = "4. Potvrzení";
        private const string GZones = "5. Zóny";
        private const string GTargets = "6. Stop a targety";
        private const string GFilters = "7. Filtry šumu";
        private const string GLog = "8. Logování a kalibrace";
        private const string GView = "9. Zobrazení";
        private const string GAlerts = "10. Alerty";

        private readonly EngineSettings _s = new EngineSettings();
        private ReversalEngine _engine;
        private CsvLogWriter _log;
        private int _next;
        private int _lastCalcBar = -1;
        private bool _realtime;
        private string _status = "";
        private readonly HashSet<int> _touchAlerted = new HashSet<int>();

        // reusable buffers for converting ATAS candles
        private double[] _p = new double[512], _b = new double[512], _a = new double[512];

        // rendering resources (created once, not per frame)
        private readonly RenderFont _font = new RenderFont("Arial", 8);
        private readonly RenderFont _fontBold = new RenderFont("Arial", 9);
        private readonly RenderStringFormat _center = new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        private readonly RenderStringFormat _left = new RenderStringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

        public ReversalConfirmationEntry() : base(true)
        {
            DenyToChangePanel = true;
            DataSeries[0].IsHidden = true;
            ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
        }

        #region Parameters: session

        [Display(Name = "Časové pásmo session", GroupName = GSession, Order = 10, Description = "IANA nebo Windows ID. CME akciové futures: America/New_York (řeší i rozdílný přechod na letní čas USA/EU).")]
        public string TimeZone { get => _s.TimeZoneId; set { _s.TimeZoneId = value; RecalculateValues(); } }

        [Display(Name = "Začátek ETH", GroupName = GSession, Order = 20)]
        public TimeSpan EthStart { get => _s.EthStart; set { _s.EthStart = value; RecalculateValues(); } }

        [Display(Name = "Začátek RTH", GroupName = GSession, Order = 30)]
        public TimeSpan RthStart { get => _s.RthStart; set { _s.RthStart = value; RecalculateValues(); } }

        [Display(Name = "Konec RTH", GroupName = GSession, Order = 40)]
        public TimeSpan RthEnd { get => _s.RthEnd; set { _s.RthEnd = value; RecalculateValues(); } }

        [Display(Name = "VWAP session", GroupName = GSession, Order = 50, Description = "Od kdy se počítá session VWAP a VWAP předchozí session.")]
        public SessionChoice VwapSession { get => (SessionChoice)_s.VwapSession; set { _s.VwapSession = (SessionMode)value; RecalculateValues(); } }

        [Display(Name = "Profil předchozího dne", GroupName = GSession, Order = 60, Description = "Z čeho se počítá high/low/POC/VAH/VAL předchozího dne.")]
        public SessionChoice PriorDayProfile { get => (SessionChoice)_s.PriorDayProfile; set { _s.PriorDayProfile = (SessionMode)value; RecalculateValues(); } }

        [Display(Name = "Signály v session", GroupName = GSession, Order = 70, Description = "ETH = celý den (výchozí, dle zadání), RTH = jen 09:30–16:00 ET.")]
        public SessionChoice SignalSession { get => (SessionChoice)_s.SignalSession; set { _s.SignalSession = (SessionMode)value; RecalculateValues(); } }

        [Display(Name = "Časy zpráv (ET, oddělit ;)", GroupName = GSession, Order = 80)]
        public string NewsTimes { get => _s.NewsTimes; set { _s.NewsTimes = value; RecalculateValues(); } }

        [Display(Name = "Okno zpráv ± minut", GroupName = GSession, Order = 90)]
        [Range(0, 60)]
        public int NewsWindow { get => _s.NewsWindowMinutes; set { _s.NewsWindowMinutes = value; RecalculateValues(); } }

        #endregion

        #region Parameters: levels

        [Display(Name = "Váha: VWAP předchozí session", GroupName = GLevels, Order = 10)]
        [Range(0, 2)]
        public decimal WPrevVwap { get => (decimal)_s.WPrevVwap; set { _s.WPrevVwap = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: session VWAP", GroupName = GLevels, Order = 20)]
        [Range(0, 2)]
        public decimal WSessionVwap { get => (decimal)_s.WSessionVwap; set { _s.WSessionVwap = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: VWAP ±1σ/±2σ", GroupName = GLevels, Order = 30)]
        [Range(0, 2)]
        public decimal WVwapBands { get => (decimal)_s.WVwapBands; set { _s.WVwapBands = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: high/low 1. RTH svíčky", GroupName = GLevels, Order = 40)]
        [Range(0, 2)]
        public decimal WFirstRthBar { get => (decimal)_s.WFirstRthBar; set { _s.WFirstRthBar = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: OR15 / OR30 / IB", GroupName = GLevels, Order = 50)]
        [Range(0, 2)]
        public decimal WOpeningRange { get => (decimal)_s.WOpeningRange; set { _s.WOpeningRange = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: RTH open", GroupName = GLevels, Order = 60)]
        [Range(0, 2)]
        public decimal WRthOpen { get => (decimal)_s.WRthOpen; set { _s.WRthOpen = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: předchozí den high/low", GroupName = GLevels, Order = 70)]
        [Range(0, 2)]
        public decimal WPrevDayHl { get => (decimal)_s.WPrevDayHl; set { _s.WPrevDayHl = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: předchozí den POC", GroupName = GLevels, Order = 80)]
        [Range(0, 2)]
        public decimal WPrevDayPoc { get => (decimal)_s.WPrevDayPoc; set { _s.WPrevDayPoc = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: předchozí den VAH/VAL", GroupName = GLevels, Order = 90)]
        [Range(0, 2)]
        public decimal WPrevDayVa { get => (decimal)_s.WPrevDayVa; set { _s.WPrevDayVa = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: overnight high/low", GroupName = GLevels, Order = 100)]
        [Range(0, 2)]
        public decimal WOvernight { get => (decimal)_s.WOvernight; set { _s.WOvernight = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: liquidity pool (equal highs/lows)", GroupName = GLevels, Order = 110)]
        [Range(0, 2)]
        public decimal WPool { get => (decimal)_s.WLiquidityPool; set { _s.WLiquidityPool = (double)value; RecalculateValues(); } }

        [Display(Name = "Váha: swing high/low dne", GroupName = GLevels, Order = 120)]
        [Range(0, 2)]
        public decimal WSwing { get => (decimal)_s.WSwing; set { _s.WSwing = (double)value; RecalculateValues(); } }

        [Display(Name = "Ruční úrovně (ceny oddělit ;)", GroupName = GLevels, Order = 130)]
        public string ManualLevels { get => _s.ManualLevels; set { _s.ManualLevels = value; RecalculateValues(); } }

        [Display(Name = "Váha: ruční úrovně", GroupName = GLevels, Order = 140)]
        [Range(0, 2)]
        public decimal WManual { get => (decimal)_s.WManual; set { _s.WManual = (double)value; RecalculateValues(); } }

        [Display(Name = "Tolerance testu (× ATR)", GroupName = GLevels, Order = 150)]
        [Range(0, 2)]
        public decimal LevelTolAtr { get => (decimal)_s.LevelTolAtr; set { _s.LevelTolAtr = (double)value; RecalculateValues(); } }

        [Display(Name = "Tolerance testu – minimum (ticky)", GroupName = GLevels, Order = 160)]
        [Range(0, 50)]
        public int LevelTolTicks { get => _s.LevelTolTicks; set { _s.LevelTolTicks = value; RecalculateValues(); } }

        #endregion

        #region Parameters: reversal

        [Display(Name = "Bullish", GroupName = GReversal, Order = 10)]
        public bool Bullish { get => _s.EnableBullish; set { _s.EnableBullish = value; RecalculateValues(); } }

        [Display(Name = "Bearish", GroupName = GReversal, Order = 20)]
        public bool Bearish { get => _s.EnableBearish; set { _s.EnableBearish = value; RecalculateValues(); } }

        [Display(Name = "Váhy A–I (odděleno ;)", GroupName = GReversal, Order = 30, Description = "A předchozí pohyb, B úroveň, C sweep, D divergence, E absorpce, F vyčerpání, G close, H selhaná iniciativa, I stall. Kalibrační JSON je přepíše.")]
        public string Weights
        {
            get => string.Join(";", Array.ConvertAll(_s.Weights, w => w.ToString("0.##", CultureInfo.InvariantCulture)));
            set
            {
                var parts = (value ?? "").Split(';');
                if (parts.Length != 9) return;
                var w = new double[9];
                for (int i = 0; i < 9; i++)
                    if (!double.TryParse(parts[i].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out w[i]) || w[i] < 0) return;
                _s.Weights = w;
                RecalculateValues();
            }
        }

        [Display(Name = "Max sloučených barů", GroupName = GReversal, Order = 40)]
        [Range(1, 3)]
        public int MaxMergedBars { get => _s.MaxMergedBars; set { _s.MaxMergedBars = Math.Max(1, Math.Min(3, value)); RecalculateValues(); } }

        [Display(Name = "Stall (komponenta I)", GroupName = GReversal, Order = 50)]
        public bool Stall { get => _s.EnableStall; set { _s.EnableStall = value; RecalculateValues(); } }

        [Display(Name = "A: barů předchozího pohybu (K)", GroupName = GReversal, Order = 60)]
        [Range(2, 30)]
        public int ABars { get => _s.A_Bars; set { _s.A_Bars = value; RecalculateValues(); } }

        [Display(Name = "A: min. pohyb (× ATR)", GroupName = GReversal, Order = 70)]
        [Range(0, 10)]
        public decimal ADropAtr { get => (decimal)_s.A_DropAtr; set { _s.A_DropAtr = (double)value; RecalculateValues(); } }

        [Display(Name = "A: min. |z| delty", GroupName = GReversal, Order = 80)]
        [Range(0, 10)]
        public decimal ADeltaZ { get => (decimal)_s.A_DeltaZ; set { _s.A_DeltaZ = (double)value; RecalculateValues(); } }

        [Display(Name = "H: z delty flush baru", GroupName = GReversal, Order = 90)]
        [Range(0, 10)]
        public decimal HFlushZ { get => (decimal)_s.H_FlushZ; set { _s.H_FlushZ = (double)value; RecalculateValues(); } }

        [Display(Name = "Min. skóre reversalu", GroupName = GReversal, Order = 100)]
        [Range(0, 100)]
        public int MinScore { get => (int)_s.MinScore; set { _s.MinScore = value; RecalculateValues(); } }

        [Display(Name = "Silný reversal od skóre", GroupName = GReversal, Order = 110)]
        [Range(0, 100)]
        public int StrongScore { get => (int)_s.StrongScore; set { _s.StrongScore = value; RecalculateValues(); } }

        [Display(Name = "ATR", GroupName = GReversal, Order = 120, Description = "Hybrid = max(ATR14, typický range daného času dne); tlumí zkreslení ATR14 kolem RTH open. V backtestu nebyl lepší než ATR14.")]
        public AtrChoice Atr { get => _s.AtrMode == AtrMode.Hybrid ? AtrChoice.Hybrid : AtrChoice.Classic; set { _s.AtrMode = value == AtrChoice.Hybrid ? AtrMode.Hybrid : AtrMode.Classic; RecalculateValues(); } }

        [Display(Name = "Dní pro time-of-day srovnání (D)", GroupName = GReversal, Order = 130)]
        [Range(1, 60)]
        public int TodDays { get => _s.TodDays; set { _s.TodDays = value; RecalculateValues(); } }

        #endregion

        #region Parameters: confirmation

        [Display(Name = "P: max barů na potvrzení", GroupName = GConfirm, Order = 10)]
        [Range(1, 30)]
        public int PBars { get => _s.P_Bars; set { _s.P_Bars = value; RecalculateValues(); } }

        [Display(Name = "Max potvrzení na kontext", GroupName = GConfirm, Order = 20)]
        [Range(1, 5)]
        public int MaxConfirmations { get => _s.MaxConfirmations; set { _s.MaxConfirmations = value; RecalculateValues(); } }

        [Display(Name = "Min. z delty", GroupName = GConfirm, Order = 30)]
        [Range(0, 10)]
        public decimal ConfDeltaZ { get => (decimal)_s.Conf_DeltaZ; set { _s.Conf_DeltaZ = (double)value; RecalculateValues(); } }

        [Display(Name = "Min. close location", GroupName = GConfirm, Order = 40)]
        [Range(0, 1)]
        public decimal ConfClv { get => (decimal)_s.Conf_Clv; set { _s.Conf_Clv = (double)value; RecalculateValues(); } }

        [Display(Name = "Min. percentil efektivity", GroupName = GConfirm, Order = 50)]
        [Range(0, 100)]
        public int ConfEfficiency { get => (int)_s.Conf_EfficiencyPct; set { _s.Conf_EfficiencyPct = value; RecalculateValues(); } }

        [Display(Name = "Imbalance poměr (diagonálně)", GroupName = GConfirm, Order = 60)]
        [Range(1, 20)]
        public decimal ImbalanceRatio { get => (decimal)_s.Conf_ImbalanceRatio; set { _s.Conf_ImbalanceRatio = (double)value; RecalculateValues(); } }

        [Display(Name = "Min. skóre potvrzení", GroupName = GConfirm, Order = 70)]
        [Range(0, 100)]
        public int ConfMinScore { get => (int)_s.Conf_MinScore; set { _s.Conf_MinScore = value; RecalculateValues(); } }

        #endregion

        #region Parameters: zones

        [Display(Name = "Zóna: VPOC potvrzující svíčky", GroupName = GZones, Order = 10)]
        public bool ZoneConfVpoc { get => Has(ZoneTypes.ConfirmationVpoc); set => SetZone(ZoneTypes.ConfirmationVpoc, value); }

        [Display(Name = "Zóna: VAL potvrzující svíčky", GroupName = GZones, Order = 20)]
        public bool ZoneConfVal { get => Has(ZoneTypes.ConfirmationVal); set => SetZone(ZoneTypes.ConfirmationVal, value); }

        [Display(Name = "Zóna: VPOC reversalu", GroupName = GZones, Order = 30)]
        public bool ZoneRevVpoc { get => Has(ZoneTypes.ReversalVpoc); set => SetZone(ZoneTypes.ReversalVpoc, value); }

        [Display(Name = "Zóna: retest", GroupName = GZones, Order = 40)]
        public bool ZoneRetest { get => Has(ZoneTypes.Retest); set => SetZone(ZoneTypes.Retest, value); }

        [Display(Name = "Q: platnost zóny (barů)", GroupName = GZones, Order = 50)]
        [Range(1, 50)]
        public int QBars { get => _s.Q_Bars; set { _s.Q_Bars = value; RecalculateValues(); } }

        [Display(Name = "Fill tolerance (ticky, záporné = proobchodovat skrz)", GroupName = GZones, Order = 60)]
        [Range(-10, 10)]
        public int FillTolTicks { get => _s.FillTolTicks; set { _s.FillTolTicks = value; RecalculateValues(); } }

        [Display(Name = "Jen zóny po směru VWAP trendu", GroupName = GZones, Order = 70, Description = "Backtest ES M5 2024–2026: zóny po směru sklonu VWAP byly lepší v tréninkovém i testovacím období.")]
        public bool TrendFilter { get => _s.TrendFilter; set { _s.TrendFilter = value; RecalculateValues(); } }

        #endregion

        #region Parameters: stop & targets

        [Display(Name = "Stop buffer (× ATR)", GroupName = GTargets, Order = 10)]
        [Range(0, 2)]
        public decimal StopBufferAtr { get => (decimal)_s.StopBufferAtr; set { _s.StopBufferAtr = (double)value; RecalculateValues(); } }

        [Display(Name = "Stop buffer – minimum (ticky)", GroupName = GTargets, Order = 20)]
        [Range(0, 50)]
        public int StopBufferTicks { get => _s.StopBufferTicks; set { _s.StopBufferTicks = value; RecalculateValues(); } }

        [Display(Name = "T1 origin pohybu", GroupName = GTargets, Order = 30)]
        public bool TargetOrigin { get => _s.TargetOrigin; set { _s.TargetOrigin = value; RecalculateValues(); } }

        [Display(Name = "T2 session VWAP", GroupName = GTargets, Order = 40)]
        public bool TargetVwap { get => _s.TargetVwap; set { _s.TargetVwap = value; RecalculateValues(); } }

        [Display(Name = "T3 nejbližší protilehlá úroveň", GroupName = GTargets, Order = 50)]
        public bool TargetLevel { get => _s.TargetLevel; set { _s.TargetLevel = value; RecalculateValues(); } }

        [Display(Name = "Min. poměr k 1. targetu (R)", GroupName = GTargets, Order = 60)]
        [Range(0, 10)]
        public decimal MinRewardRisk { get => (decimal)_s.MinRewardRisk; set { _s.MinRewardRisk = (double)value; RecalculateValues(); } }

        [Display(Name = "Break-even čára po 1R", GroupName = GTargets, Order = 70)]
        public bool BreakEven { get; set; }

        #endregion

        #region Parameters: noise filters

        [Display(Name = "Potlačit signály v okně zpráv", GroupName = GFilters, Order = 10)]
        public bool SuppressNews { get => _s.SuppressNewsSignals; set { _s.SuppressNewsSignals = value; RecalculateValues(); } }

        [Display(Name = "Filtr mrtvého trhu", GroupName = GFilters, Order = 20)]
        public bool DeadMarket { get => _s.DeadMarketFilter; set { _s.DeadMarketFilter = value; RecalculateValues(); } }

        [Display(Name = "Mrtvý trh pod percentilem objemu", GroupName = GFilters, Order = 30)]
        [Range(0, 100)]
        public int DeadMarketPct { get => (int)_s.DeadMarketPercentile; set { _s.DeadMarketPercentile = value; RecalculateValues(); } }

        #endregion

        #region Parameters: logging & calibration

        [Display(Name = "Logovat do CSV", GroupName = GLog, Order = 10)]
        public bool LogEnabled { get => _logEnabled; set { _logEnabled = value; RecalculateValues(); } }
        private bool _logEnabled = true;

        [Display(Name = "Složka pro CSV", GroupName = GLog, Order = 20, Description = "Prázdné = %APPDATA%\\ATAS\\ReversalConfirmation\\logs. Soubor se přepíše při každém přepočtu.")]
        public string LogFolder { get => _logFolder; set { _logFolder = value; RecalculateValues(); } }
        private string _logFolder = "";

        [Display(Name = "Použít kalibrované váhy A–I", GroupName = GLog, Order = 40, Description = "Vypnuto: skóre podle vah ze zadání, z kalibrace se berou jen změřené pravděpodobnosti.")]
        public bool UseCalibratedWeights { get => _s.UseCalibratedWeights; set { _s.UseCalibratedWeights = value; RecalculateValues(); } }

        [Display(Name = "Kalibrační JSON", GroupName = GLog, Order = 30, Description = "Výstup calibration/calibrate.py. Prázdné = %APPDATA%\\ATAS\\ReversalConfirmation\\calibration.json (pokud existuje).")]
        public string CalibrationFile { get => _calibrationFile; set { _calibrationFile = value; RecalculateValues(); } }
        private string _calibrationFile = "";

        #endregion

        #region Parameters: view

        [Display(Name = "Režim", GroupName = GView, Order = 10)]
        public DisplayMode Mode { get; set; } = DisplayMode.EntriesOnly;

        [Display(Name = "Barva bullish", GroupName = GView, Order = 20)]
        public Color BullColor { get; set; } = Color.FromArgb(255, 0, 170, 90);

        [Display(Name = "Barva bearish", GroupName = GView, Order = 30)]
        public Color BearColor { get; set; } = Color.FromArgb(255, 220, 50, 50);

        [Display(Name = "Barva stopu", GroupName = GView, Order = 40)]
        public Color StopColor { get; set; } = Color.FromArgb(255, 200, 30, 30);

        [Display(Name = "Barva targetů", GroupName = GView, Order = 50)]
        public Color TargetColor { get; set; } = Color.FromArgb(255, 30, 140, 220);

        [Display(Name = "Barva textu", GroupName = GView, Order = 60)]
        public Color TextColor { get; set; } = Color.FromArgb(255, 230, 230, 230);

        [Display(Name = "Statistický panel", GroupName = GView, Order = 70)]
        public bool ShowStats { get; set; } = true;

        [Display(Name = "Kreslit referenční úrovně", GroupName = GView, Order = 80)]
        public bool ShowLevels { get; set; }

        [Display(Name = "Tooltip", GroupName = GView, Order = 90)]
        public bool ShowTooltip { get; set; } = true;

        #endregion

        #region Parameters: alerts

        [Display(Name = "Alert: nová vstupní zóna", GroupName = GAlerts, Order = 10)]
        public bool AlertNewZone { get; set; } = true;

        [Display(Name = "Alert: fill zóny", GroupName = GAlerts, Order = 20)]
        public bool AlertFill { get; set; } = true;

        [Display(Name = "Alert: dotyk zóny (intrabar)", GroupName = GAlerts, Order = 30, Description = "Upozorní hned, jak cena během svíčky dosáhne aktivní zóny (oficiální fill se vyhodnotí na close).")]
        public bool AlertTouch { get; set; }

        [Display(Name = "Alert: zrušení kontextu / absorpce selhala", GroupName = GAlerts, Order = 40)]
        public bool AlertCancel { get; set; } = true;

        [Display(Name = "Alert: reversal", GroupName = GAlerts, Order = 50)]
        public bool AlertReversal { get; set; }

        [Display(Name = "Zvukový soubor", GroupName = GAlerts, Order = 60)]
        public string AlertFile { get; set; } = "alert1";

        #endregion

        private bool Has(ZoneTypes t) => (_s.Zones & t) != 0;

        private void SetZone(ZoneTypes t, bool on)
        {
            _s.Zones = on ? _s.Zones | t : _s.Zones & ~t;
            RecalculateValues();
        }

        // =============================================================================================
        // Calculation
        // =============================================================================================
        protected override void OnRecalculate()
        {
            Reset();
        }

        private void Reset()
        {
            try { _log?.Dispose(); } catch { }
            _log = null;
            _engine = null;
            _next = 0;
            _lastCalcBar = -1;
            _realtime = false;
            _touchAlerted.Clear();
        }

        private void EnsureEngine()
        {
            if (_engine != null) return;
            double tick = InstrumentInfo != null && InstrumentInfo.TickSize > 0 ? (double)InstrumentInfo.TickSize : 0.25;
            double minutes = BarMinutes();
            string instrument = InstrumentInfo?.Instrument ?? "instrument";
            string tf = ChartInfo?.TimeFrame ?? "";
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "ReversalConfirmation");

            Calibration cal = null;
            var calPath = string.IsNullOrWhiteSpace(_calibrationFile) ? Path.Combine(baseDir, "calibration.json") : _calibrationFile;
            string calError = null;
            try { cal = Calibration.TryLoad(calPath, out calError); } catch (Exception e) { calError = e.Message; }

            if (_logEnabled)
            {
                try
                {
                    var dir = string.IsNullOrWhiteSpace(_logFolder) ? Path.Combine(baseDir, "logs") : _logFolder;
                    var name = Regex.Replace($"{instrument}_{tf}", @"[^\w\-.]+", "_") + ".csv";
                    _log = new CsvLogWriter(Path.Combine(dir, name));
                }
                catch { _log = null; }
            }

            _engine = new ReversalEngine(_s, tick, minutes, _log, cal);
            _status = (cal != null ? "kalibrace: " + Path.GetFileName(calPath) : calError != null ? "kalibrace: chyba " + calError : "bez kalibrace")
                      + (minutes > 0 ? "" : " | graf není časový: time-of-day srovnání vypnuto");
        }

        /// <summary>Bar length in minutes for time-based charts, 0 otherwise (tick, volume, range, ...).</summary>
        private double BarMinutes()
        {
            if (ChartInfo == null) return 0;
            var type = ChartInfo.ChartType;
            var tf = ChartInfo.TimeFrame ?? "";
            var m = Regex.Match(tf, @"\d+$");
            int n = m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 1;
            if (type == "Seconds") return n / 60.0;
            if (type != "TimeFrame") return 0;
            if (tf == "Hourly") return 60;
            if (tf == "Daily") return 1440;
            if (tf.StartsWith("M")) return n;
            if (tf.StartsWith("H")) return 60 * n;
            return 0;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            try
            {
                if (bar == 0 && _next > 0) Reset();
                EnsureEngine();

                // analyse every candle exactly once, when it is closed (all bars before the current one)
                while (_next < bar)
                {
                    ProcessClosed(_next);
                    _next++;
                }

                if (bar == _lastCalcBar) _realtime = true;   // tick updates of the forming candle = live data
                _lastCalcBar = bar;

                if (_realtime && AlertTouch && bar == CurrentBar - 1) CheckTouch(bar);
            }
            catch (Exception e)
            {
                _status = "chyba: " + e.Message;
            }
        }

        private void ProcessClosed(int i)
        {
            var c = GetCandle(i);
            int n = 0;
            foreach (var lvl in c.GetAllPriceLevels())
            {
                if (n == _p.Length)
                {
                    Array.Resize(ref _p, n * 2);
                    Array.Resize(ref _b, n * 2);
                    Array.Resize(ref _a, n * 2);
                }
                _p[n] = (double)lvl.Price;
                _b[n] = (double)lvl.Bid;
                _a[n] = (double)lvl.Ask;
                n++;
            }
            var b = Bar.Create(i, c.Time, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, _engine.Tick, _p, _b, _a, n);
            _engine.OnBar(b);

            List<EngineEvent> events = null;
            lock (_engine.Sync)
            {
                if (_engine.Events.Count > 0)
                {
                    events = new List<EngineEvent>(_engine.Events);
                    _engine.Events.Clear();
                }
            }
            // alerts only for candles that closed while the chart was live (never for history)
            if (events == null || !_realtime || i < CurrentBar - 2) return;
            foreach (var e in events) Alert(e);
        }

        private void Alert(EngineEvent e)
        {
            bool on = e.Type switch
            {
                EngineEventType.NewZone => AlertNewZone,
                EngineEventType.ZoneFilled => AlertFill,
                EngineEventType.ContextCancelled => AlertCancel,
                EngineEventType.AbsorptionFailed => AlertCancel,
                EngineEventType.Reversal => AlertReversal,
                _ => false
            };
            if (!on) return;
            AddAlert(AlertFile, $"{InstrumentInfo?.Instrument} {(e.Dir > 0 ? "▲" : "▼")} {e.Text}");
        }

        private void CheckTouch(int bar)
        {
            var c = GetCandle(bar);
            List<string> msgs = null;
            lock (_engine.Sync)
            {
                foreach (var z in _engine.Zones)
                {
                    if (z.State != ZoneState.Active || _touchAlerted.Contains(z.Id)) continue;
                    bool touch = z.Dir > 0 ? (double)c.Low <= z.Price + _s.FillTolTicks * _engine.Tick : (double)c.High >= z.Price - _s.FillTolTicks * _engine.Tick;
                    if (!touch) continue;
                    _touchAlerted.Add(z.Id);
                    (msgs ??= new List<string>()).Add($"{InstrumentInfo?.Instrument} {(z.Dir > 0 ? "▲" : "▼")} cena v zóně {z.TypeName} {z.Price.ToString("0.##", CultureInfo.InvariantCulture)}");
                }
            }
            if (msgs != null)
                foreach (var m in msgs) AddAlert(AlertFile, m);
        }

        protected override void OnDispose()
        {
            try { _log?.Dispose(); } catch { }
            _log = null;
        }

        // =============================================================================================
        // Rendering (visible bars only)
        // =============================================================================================
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var engine = _engine;
            if (ChartInfo == null || engine == null) return;
            try
            {
                int first = Math.Max(0, FirstVisibleBarNumber), last = LastVisibleBarNumber;
                lock (engine.Sync)
                {
                    DrawZones(context, engine, first, last);
                    DrawMarks(context, engine, first, last);
                    if (ShowLevels) DrawLevels(context, engine, last);
                    if (ShowStats) DrawStats(context, engine);
                    if (ShowTooltip) DrawTooltip(context, engine, first, last);
                }
            }
            catch (Exception e)
            {
                _status = "chyba vykreslení: " + e.Message;
            }
        }

        private int X(int bar) => ChartInfo.GetXByBar(bar, false);
        private int Y(double price) => ChartInfo.GetYByPrice((decimal)price, false);
        private int BarWidth => Math.Max(1, (int)ChartInfo.PriceChartContainer.BarsWidth);
        private int RowHalf => Math.Max(2, (int)ChartInfo.PriceChartContainer.PriceRowHeight / 2);

        private static Color Alpha(Color c, int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);

        private void DrawZones(RenderContext g, ReversalEngine e, int first, int last)
        {
            // zones start at most ~(Q + horizon) bars before their end: search from there
            int from = LowerBound(e.Zones, first - _s.Q_Bars - 60);
            for (int i = from; i < e.Zones.Count; i++)
            {
                var z = e.Zones[i];
                if (z.StartBar > last) break;
                int end = z.EndBar >= 0 ? z.EndBar : Math.Min(z.ExpiryBar, last + 1);
                int tradeEnd = z.FillBar >= 0 ? (z.TradeEndBar >= 0 ? z.TradeEndBar : Math.Min(z.FillBar + 36, last + 1)) : -1;
                if (Math.Max(end, tradeEnd) < first) continue;

                var baseColor = z.Dir > 0 ? BullColor : BearColor;
                int alpha = z.State == ZoneState.Active || z.State == ZoneState.Filled
                    ? 60 + (int)(1.6 * Math.Max(0, Math.Min(100, z.Score) - 50))
                    : 35;
                var fill = z.State == ZoneState.Expired || z.State == ZoneState.Cancelled || z.State == ZoneState.Failed
                    ? Color.FromArgb(alpha, 128, 128, 128) : Alpha(baseColor, alpha);
                int x1 = X(z.StartBar) + BarWidth / 2, x2 = X(end) + BarWidth / 2, y = Y(z.Price), h = RowHalf;
                var rect = new Rectangle(x1, y - h, Math.Max(2, x2 - x1), 2 * h);
                g.FillRectangle(fill, rect);
                if (z.State == ZoneState.Filled) g.DrawRectangle(new RenderPen(baseColor, 1), rect);
                string label = !double.IsNaN(z.Probability) ? $"{z.Score:0} · {z.Probability * 100:0}%" : $"{z.Score:0}";
                g.DrawString(label, _font, TextColor, x2 + 3, y - 6);

                if (tradeEnd < 0) continue;
                int t1 = X(z.FillBar), t2 = X(tradeEnd) + BarWidth;
                var stopPen = new RenderPen(StopColor, 1);
                int ys = Y(z.Stop);
                g.DrawLine(stopPen, t1, ys, t2, ys);
                g.DrawString("SL", _font, StopColor, t2 + 2, ys - 6);
                var tgPen = new RenderPen(Alpha(TargetColor, 200), 1);
                foreach (var t in z.Targets)
                {
                    int yt = Y(t.Price);
                    int endX = t.HitBar >= 0 ? X(t.HitBar) + BarWidth : t2;
                    g.DrawLine(tgPen, t1, yt, endX, yt);
                    g.DrawString(t.Name + (t.HitBar >= 0 ? " ✓" : ""), _font, TargetColor, endX + 2, yt - 6);
                }
                if (BreakEven && z.BreakEvenBar >= 0 && !double.IsNaN(z.Entry))
                {
                    int yb = Y(z.Entry);
                    g.DrawLine(new RenderPen(Alpha(TextColor, 160), 1), X(z.BreakEvenBar), yb, t2, yb);
                    g.DrawString("BE", _font, TextColor, t2 + 2, yb - 6);
                }
            }
        }

        private static int LowerBound(List<Zone> zones, int bar)
        {
            int lo = 0, hi = zones.Count;
            while (lo < hi)
            {
                int m = (lo + hi) >> 1;
                if (zones[m].StartBar < bar) lo = m + 1; else hi = m;
            }
            return lo;
        }

        private static int LowerBound(List<Mark> marks, int bar)
        {
            int lo = 0, hi = marks.Count;
            while (lo < hi)
            {
                int m = (lo + hi) >> 1;
                if (marks[m].Bar < bar) lo = m + 1; else hi = m;
            }
            return lo;
        }

        private void DrawMarks(RenderContext g, ReversalEngine e, int first, int last)
        {
            int bw = BarWidth;
            for (int i = LowerBound(e.Marks, first); i < e.Marks.Count; i++)
            {
                var m = e.Marks[i];
                if (m.Bar > last) break;
                var col = m.Dir > 0 ? BullColor : BearColor;
                int x = X(m.Bar) + bw / 2;
                int y = Y(m.Price);
                int off = 8 + RowHalf;
                int yy = m.Dir > 0 ? y + off : y - off;
                switch (m.Type)
                {
                    case MarkType.Reversal:
                        bool small = Mode == DisplayMode.EntriesOnly;
                        int r = small ? (m.Strong ? 3 : 2) : (m.Strong ? 5 : 4);
                        g.FillEllipse(small ? Alpha(col, 170) : col, new Rectangle(x - r, yy - r, 2 * r, 2 * r));
                        if (!small)
                        {
                            var pts = m.Dir > 0
                                ? new[] { new Point(x, yy + r + 2), new Point(x - 5, yy + r + 10), new Point(x + 5, yy + r + 10) }
                                : new[] { new Point(x, yy - r - 2), new Point(x - 5, yy - r - 10), new Point(x + 5, yy - r - 10) };
                            g.FillPolygon(col, pts);
                            g.DrawString($"{m.Score:0}", _font, TextColor, x - 10, m.Dir > 0 ? yy + r + 11 : yy - r - 22);
                        }
                        break;
                    case MarkType.Confirmation:
                        Label(g, m.Label, col, x, m.Dir > 0 ? yy + 10 : yy - 10, true);
                        break;
                    case MarkType.Retest:
                        Label(g, "R", col, x, m.Dir > 0 ? yy + 10 : yy - 10, true);
                        break;
                    case MarkType.AbsorptionFailed:
                        Label(g, "!", Color.Orange, x, m.Dir > 0 ? yy + 22 : yy - 22, true);
                        break;
                    case MarkType.ContextCancelled:
                        Label(g, "×", Color.Gray, x, m.Dir > 0 ? yy + 22 : yy - 22, false);
                        break;
                }
            }
        }

        private void Label(RenderContext g, string text, Color col, int x, int y, bool boxed)
        {
            var size = g.MeasureString(text, _fontBold);
            var rect = new Rectangle(x - size.Width / 2 - 2, y - size.Height / 2 - 1, size.Width + 4, size.Height + 2);
            if (boxed) g.FillRectangle(Alpha(col, 220), rect);
            g.DrawString(text, _fontBold, boxed ? Color.White : col, rect, _center);
        }

        private void DrawLevels(RenderContext g, ReversalEngine e, int last)
        {
            int x2 = X(last) + BarWidth, x1 = Math.Max(0, x2 - 120);
            var pen = new RenderPen(Alpha(TextColor, 90), 1);
            foreach (var l in e.CurrentLevels)
            {
                int y = Y(l.Price);
                g.DrawLine(pen, x1, y, x2, y);
                g.DrawString(l.Name, _font, Alpha(TextColor, 150), x1, y - 11);
            }
        }

        private void DrawStats(RenderContext g, ReversalEngine e)
        {
            var z = e.ZoneStats;
            var lines = new List<string> { "Zóny (vyplněné)   n   T1%   R(T1)  R(1.5R)" };
            string[] names = { "60–70", "70–80", "80+  " };
            for (int i = 0; i < 3; i++)
            {
                int n = z.N[i];
                lines.Add(n == 0
                    ? $"{names[i]}             0"
                    : $"{names[i]}          {n,4}  {100.0 * z.HitT1[i] / n,4:0}  {z.SumR[i] / n,6:+0.00;-0.00}  {z.SumR15[i] / n,6:+0.00;-0.00}");
            }
            lines.Add("Typ zóny          n   win1.5R  R(1.5R)");
            foreach (var kv in e.ZoneTypeStats)
            {
                var a = kv.Value;
                if (a[0] <= 0) continue;
                string name = kv.Key == ZoneTypes.Retest ? "retest       " : kv.Key == ZoneTypes.ConfirmationVpoc ? "VPOC potvrzení" : kv.Key == ZoneTypes.ConfirmationVal ? "VAL potvrzení " : "VPOC reversalu";
                lines.Add($"{name}  {a[0],4:0}   {100 * a[4] / a[0],4:0}%   {a[3] / a[0],6:+0.00;-0.00}");
            }
            lines.Add(_status);
            int y = 20;
            foreach (var l in lines)
            {
                g.DrawString(l, _font, TextColor, 10, y);
                y += 13;
            }
        }

        private void DrawTooltip(RenderContext g, ReversalEngine e, int first, int last)
        {
            if (MouseLocationInfo == null) return;
            var mouse = MouseLocationInfo.LastPosition;
            int bar = MouseLocationInfo.BarBelowMouse;
            if (bar < first || bar > last) return;
            string text = null;
            int bw = BarWidth;
            for (int i = LowerBound(e.Marks, bar); i < e.Marks.Count && e.Marks[i].Bar == bar; i++)
            {
                var m = e.Marks[i];
                int y = Y(m.Price) + (m.Dir > 0 ? 8 + RowHalf : -8 - RowHalf);
                if (Math.Abs(mouse.Y - y) <= 30 && !string.IsNullOrEmpty(m.Tooltip)) { text = m.Tooltip; break; }
            }
            if (text == null)
                foreach (var z in e.Zones)
                {
                    int end = z.EndBar >= 0 ? z.EndBar : z.ExpiryBar;
                    if (bar < z.StartBar || bar > end) continue;
                    if (Math.Abs(mouse.Y - Y(z.Price)) <= RowHalf + 3) { text = z.Tooltip; break; }
                }
            if (text == null) return;
            var size = g.MeasureString(text, _font);
            var rect = new Rectangle(mouse.X + 14, mouse.Y + 14, size.Width + 12, size.Height + 10);
            if (rect.Right > ChartInfo.Region.Width) rect.X = mouse.X - rect.Width - 14;
            g.FillRectangle(Color.FromArgb(235, 25, 28, 34), rect);
            g.DrawRectangle(new RenderPen(Color.FromArgb(255, 90, 90, 90), 1), rect);
            g.DrawString(text, _font, Color.FromArgb(255, 235, 235, 235), new Rectangle(rect.X + 6, rect.Y + 5, size.Width + 2, size.Height + 2), _left);
        }
    }
}
