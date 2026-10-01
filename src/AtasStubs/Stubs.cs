// Stand-ins for ATAS.Indicators / OFT.Rendering. Only the surface the indicator touches.
using System;
using System.Collections.Generic;
using System.Drawing;
using OFT.Rendering.Context;

namespace ATAS.Indicators
{
    public static class IndicatorCategories
    {
        public const string VolumeOrderFlow = "Volume Order Flow";
    }

    public enum VisualMode { Line, Hide }

    public enum DrawingLayouts { None = 0, LatestBar = 1, Historical = 2, Final = 4 }

    public enum ChartVisualModes { Candles, Clusters }

    public class PriceVolumeInfo
    {
        public decimal Price { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Volume { get; set; }
    }

    public class IndicatorCandle
    {
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal Delta { get; set; }
        public DateTime Time { get; set; }
        public DateTime LastTime { get; set; }
        public PriceVolumeInfo MaxVolumePriceInfo { get; set; }
        public List<PriceVolumeInfo> HostLevels = new List<PriceVolumeInfo>();
        public IEnumerable<PriceVolumeInfo> GetAllPriceLevels() => HostLevels;
        public PriceVolumeInfo GetPriceVolumeInfo(decimal price) => null;
    }

    public interface IDataSeries
    {
        bool IsHidden { get; set; }
    }

    public class ValueDataSeries : IDataSeries
    {
        public bool IsHidden { get; set; }
        public VisualMode VisualType { get; set; }
    }

    public class InstrumentInfo
    {
        public decimal TickSize { get; set; }
        public string Instrument { get; set; }
        public TimeSpan TimeZoneOffset { get; set; }
    }

    public class PriceChartContainer
    {
        public decimal BarsWidth { get; set; }
        public decimal PriceRowHeight { get; set; }
        public Rectangle Region { get; set; }
    }

    public class MouseLocationInfo
    {
        public Point LastPosition { get; set; }
        public int BarBelowMouse { get; set; }
    }

    /// <summary>Test host implementation of the chart.</summary>
    public class HostChartInfo : IChartInfo
    {
        public string TimeFrame { get; set; } = "M5";
        public string ChartType { get; set; } = "TimeFrame";
        public Rectangle Region { get; set; } = new Rectangle(0, 0, 1600, 900);
        public PriceChartContainer PriceChartContainer { get; set; } = new PriceChartContainer { BarsWidth = 6, PriceRowHeight = 4 };
        public ChartVisualModes ChartVisualMode { get; set; }
        public MouseLocationInfo MouseLocationInfo { get; set; } = new MouseLocationInfo();
        public int FirstBar;
        public decimal Top = 10000, RowsPerPixel = 0.25m;
        public int GetXByBar(int bar, bool isStartOfBar = true) => (bar - FirstBar) * 8;
        public int GetYByPrice(decimal price, bool isStartOfBar = true) => (int)((Top - price) / RowsPerPixel);
    }

    public interface IChartInfo
    {
        string TimeFrame { get; }
        string ChartType { get; }
        Rectangle Region { get; }
        PriceChartContainer PriceChartContainer { get; }
        ChartVisualModes ChartVisualMode { get; }
        MouseLocationInfo MouseLocationInfo { get; }
        int GetXByBar(int bar, bool isStartOfBar = true);
        int GetYByPrice(decimal price, bool isStartOfBar = true);
    }

    public abstract class Indicator
    {
        protected Indicator(bool useCandles = false) { }

        public List<IDataSeries> DataSeries { get; } = new List<IDataSeries> { new ValueDataSeries() };
        public bool DenyToChangePanel { get; set; }
        public bool EnableCustomDrawing { get; set; }
        public int CurrentBar => HostCurrentBar;
        public InstrumentInfo InstrumentInfo => HostInstrument;
        public IChartInfo ChartInfo => HostChart;
        public int FirstVisibleBarNumber => HostFirstVisible;
        public int LastVisibleBarNumber => HostLastVisible;
        public MouseLocationInfo MouseLocationInfo => HostChart?.MouseLocationInfo;

        // --- test host hooks (stubs only) ---
        public int HostCurrentBar, HostFirstVisible, HostLastVisible;
        public InstrumentInfo HostInstrument = new InstrumentInfo { TickSize = 0.25m, Instrument = "ES" };
        public HostChartInfo HostChart = new HostChartInfo();
        public Func<int, IndicatorCandle> HostGetCandle;
        public readonly List<string> HostAlerts = new List<string>();
        public int HostRecalcRequests;


        // Names of the remaining public/protected members of the real Indicator/BaseIndicator/ChartObject
        // (docs.atas.net class reference). Their types do not matter: they exist so that a member of the
        // indicator that would hide one of them fails this build with CS0108, exactly like the real build.
        public object AddText => null;
        public object AlertsEnabled => null;
        public object AllowedInteraction => null;
        public object ApplyDefaultColors => null;
        public object BarValueChanged => null;
        public object Calculate => null;
        public object CalculationTimeFrame => null;
        public object CandlesDataProvider => null;
        public object Category => null;
        public object ChartArea => null;
        public object ChartType => null;
        public object Clear => null;
        public object Container => null;
        public object CumulativeDomAsks => null;
        public object CumulativeDomBids => null;
        public object CustomName => null;
        public object DataPath => null;
        public object DataProvider => null;
        public object DenyCalculationTimeFrameChange => null;
        public object DoActionInGuiThread => null;
        public object Draw => null;
        public object DrawAbovePrice => null;
        public object ExternalCalculateBarRequested => null;
        public object ExternalCalculation => null;
        public object ExternalRecalculateRequested => null;
        public object FullScreenMode => null;
        public object GetCursor => null;
        public object GetFixedProfile => null;
        public object GetMarketByOrdersCache => null;
        public object GetMarketByOrdersWithTradesCache => null;
        public object GetMarketDepthSnapshot => null;
        public object GetTradesCache => null;
        public object HorizontalLinesTillTouch => null;
        public object IgnoreHistoryScale => null;
        public object InstrumentMarketDataProvider => null;
        public object IsDisposed => null;
        public object IsLoading => null;
        public object IsNewMonth => null;
        public object IsNewSession => null;
        public object IsNewWeek => null;
        public object IsVerticalIndicator => null;
        public object Labels => null;
        public object LineSeries => null;
        public object Locked => null;
        public object LockedOnChanged => null;
        public object MarketByOrders => null;
        public object MarketDepthChanged => null;
        public object MarketDepthInfo => null;
        public object MarketDepthsChanged => null;
        public object MarketTime => null;
        public object MaxCustomNameLength => null;
        public object MeasurePerformance => null;
        public object Name => null;
        public object OnApplyDefaultColors => null;
        public object OnBestBidAskChanged => null;
        public object OnChangeProperty => null;
        public object OnContainerChanged => null;
        public object OnCumulativeTrade => null;
        public object OnCumulativeTradesResponse => null;
        public object OnDataProviderChanged => null;
        public object OnDenyCalculationTimeFrameChangeChanged => null;
        public object OnFixedProfilesResponse => null;
        public object OnInitialize => null;
        public object OnMarketByOrdersChanged => null;
        public object OnNewMyTrade => null;
        public object OnNewOrder => null;
        public object OnNewTrade => null;
        public object OnNewTrades => null;
        public object OnOrderCancelFailed => null;
        public object OnOrderChanged => null;
        public object OnOrderModifyFailed => null;
        public object OnOrderRegisterFailed => null;
        public object OnPortfolioChanged => null;
        public object OnPositionChanged => null;
        public object OnPropertiesEditorChanged => null;
        public object OnSourceChanged => null;
        public object OnUpdateCumulativeTrade => null;
        public object OnVisibleChanged => null;
        public object OptionsDataProvider => null;
        public object Panel => null;
        public object PanelPropertyChanged => null;
        public object PerformanceDiagnoser => null;
        public object PlatformSettings => null;
        public object ProcessKeyDown => null;
        public object ProcessKeyUp => null;
        public object ProcessMouseClick => null;
        public object ProcessMouseDoubleClick => null;
        public object ProcessMouseDown => null;
        public object ProcessMouseMove => null;
        public object ProcessMouseUp => null;
        public object ProcessMouseWheel => null;
        public object PropertiesEditor => null;
        public object PropertyChanged => null;
        public object RaiseBarValueChanged => null;
        public object RaisePanelPropertyChanged => null;
        public object RaisePropertyChanged => null;
        public object Rectangles => null;
        public object RefreshData => null;
        public object RequestFixedProfileAsync => null;
        public object RequestForCumulativeTrades => null;
        public object SetProperty => null;
        public object SetTrackedProperty => null;
        public object ShowDescription => null;
        public object SourceDataSeries => null;
        public object StringFormat => null;
        public object SubscribeMarketByOrderData => null;
        public object SubscribeToTimer => null;
        public object SupportsExtendedSeries => null;
        public object TimeFrame => null;
        public object TradingManager => null;
        public object TradingStatisticsProvider => null;
        public object TrendLines => null;
        public object TryGetService => null;
        public object UnsubscribeFromTimer => null;
        public object UseCandles => null;
        public object UseProfiling => null;
        public object UsedIndicators => null;
        public object UtcTime => null;
        public object ValueAreaPercent => null;
        public object Visible => null;
        public object VisibleBarsCount => null;

        protected abstract void OnCalculate(int bar, decimal value);
        protected virtual void OnRecalculate() { }
        protected virtual void OnFinishRecalculate() { }
        protected virtual void OnDispose() { }
        protected virtual void OnRender(RenderContext context, DrawingLayouts layout) { }

        public IndicatorCandle GetCandle(int bar)
        {
            if (bar < 0 || bar >= HostCurrentBar) throw new ArgumentOutOfRangeException(nameof(bar));   // like ATAS
            return HostGetCandle?.Invoke(bar);
        }
        protected void SubscribeToDrawingEvents(DrawingLayouts layouts) { }
        public void RecalculateValues() => HostRecalcRequests++;
        public void RedrawChart() { }
        protected void AddAlert(string soundFile, string message) => HostAlerts.Add(message);
    }
}

namespace ATAS.Indicators.Drawing
{
    internal static class DrawingNamespaceMarker { }
}

namespace OFT.Rendering.Tools
{
    public class RenderFont
    {
        public RenderFont(string fontFamily, float size) { }
    }

    public class RenderPen
    {
        public RenderPen(Color color, float width = 1) { }
    }

    public class RenderStringFormat
    {
        public StringAlignment Alignment { get; set; }
        public StringAlignment LineAlignment { get; set; }
    }
}

namespace OFT.Rendering.Context
{
    using OFT.Rendering.Tools;

    public class RenderContext
    {
        public Rectangle ClipBounds { get; }
        public void ResetClip() { }
        public void SetClip(Rectangle r) { }
        public void FillRectangle(Color color, Rectangle rect) { }
        public void DrawRectangle(RenderPen pen, Rectangle rect) { }
        public void DrawLine(RenderPen pen, int x1, int y1, int x2, int y2) { }
        public void FillEllipse(Color color, Rectangle rect) { }
        public void DrawEllipse(RenderPen pen, Rectangle rect) { }
        public void FillPolygon(Color color, Point[] points) { }
        public void DrawString(string text, RenderFont font, Color color, int x, int y) { }
        public void DrawString(string text, RenderFont font, Color color, Rectangle rect, RenderStringFormat format) { }
        public Size MeasureString(string text, RenderFont font) => new Size(text.Length * 6, 12);
    }
}
