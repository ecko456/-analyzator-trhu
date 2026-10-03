using System;
using System.Collections.Generic;

namespace ReversalConfirmation.Core
{
    /// <summary>Where a bar sits in the trading day.</summary>
    public struct BarTime
    {
        public DateTime Local;          // session time zone (ET)
        public long SessionId;          // trade date as day number (the 18:00 ET open belongs to the next day)
        public double MinutesFromEth;   // minutes since the 18:00 ET session open
        public int Slot;                // time-of-day slot, -1 when the chart is not time based
        public bool IsRth;
        public double MinutesFromRth;   // minutes since 09:30 ET (negative before the open)
        public bool News;
        /// <summary>Bar starts inside the trader's RTH / ETH window (trader's clock, see EngineSettings.Window).</summary>
        public bool InRthWindow, InEthWindow;
        /// <summary>Bar starts inside the selected trading window.</summary>
        public bool InWindow;
        public string WindowCode => InRthWindow ? "rth" : InEthWindow ? "eth" : "";
        /// <summary>Time of day on the trader's clock (EngineSettings.LocalTimeZoneId).</summary>
        public TimeSpan LocalTod;
    }

    /// <summary>DST-correct session calendar in the exchange's reference time zone (ET for CME equity futures).</summary>
    public sealed class SessionClock
    {
        private readonly EngineSettings _s;
        private readonly TimeZoneInfo _tz, _localTz;
        private readonly List<TimeSpan> _news;
        private readonly double _barMinutes;
        private readonly TimeSpan _rthFrom, _rthTo;   // RTH window in exchange time

        /// <param name="barMinutes">Bar length in minutes for time-based charts; 0 for tick/volume/range charts.</param>
        public SessionClock(EngineSettings s, double barMinutes)
        {
            _s = s;
            _tz = FindTimeZone(s.TimeZoneId);
            _localTz = FindLocalTimeZone(s.LocalTimeZoneId);
            _news = s.ParseNewsTimes();
            _barMinutes = barMinutes;
            // RTH follows the exchange: trader's clock minus the normal (standard-time) difference, e.g. Prague-NY = 6 h
            var diff = _localTz.BaseUtcOffset - _tz.BaseUtcOffset;
            _rthFrom = s.RthWindowFrom - diff;
            _rthTo = s.RthWindowTo - diff;
        }

        public static TimeZoneInfo FindLocalTimeZone(string id)
        {
            foreach (var cand in new[] { id, "Europe/Prague", "Central Europe Standard Time" })
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                try { return TimeZoneInfo.FindSystemTimeZoneById(cand); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.Local;
        }

        private static bool InRange(TimeSpan tod, TimeSpan from, TimeSpan to)
        {
            // windows may be given across midnight (from > to)
            return from <= to ? tod >= from && tod < to : tod >= from || tod < to;
        }

        public double BarMinutes => _barMinutes;
        public bool TimeBased => _barMinutes > 0;

        public static TimeZoneInfo FindTimeZone(string id)
        {
            foreach (var cand in new[] { id, "America/New_York", "Eastern Standard Time" })
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                try { return TimeZoneInfo.FindSystemTimeZoneById(cand); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.Utc;
        }

        private static TimeSpan Norm(TimeSpan t)
        {
            double m = t.TotalMinutes % 1440;
            return TimeSpan.FromMinutes(m < 0 ? m + 1440 : m);
        }

        public DateTime ToLocal(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _tz);

        public BarTime Resolve(DateTime utcOpen)
        {
            var local = ToLocal(utcOpen);
            var tod = local.TimeOfDay;
            var tradeDate = tod >= _s.EthStart ? local.Date.AddDays(1) : local.Date;
            // Sunday evening belongs to Monday; Saturday/Sunday never trade otherwise
            var sessionStart = tradeDate.AddDays(-1).Add(_s.EthStart);
            var bt = new BarTime
            {
                Local = local,
                SessionId = (long)(tradeDate - DateTime.MinValue).TotalDays,
                MinutesFromEth = (local - sessionStart).TotalMinutes,
                IsRth = tod >= _s.RthStart && tod < _s.RthEnd,
                MinutesFromRth = (tod - _s.RthStart).TotalMinutes
            };
            bt.Slot = _barMinutes > 0 ? (int)Math.Floor(bt.MinutesFromEth / _barMinutes + 1e-9) : -1;
            bt.InRthWindow = InRange(tod, Norm(_rthFrom), Norm(_rthTo));
            var ltod = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcOpen, DateTimeKind.Utc), _localTz).TimeOfDay;
            bt.InEthWindow = InRange(ltod, _s.EthWindowFrom, _s.EthWindowTo);
            bt.LocalTod = ltod;
            bt.InWindow = _s.Window == TradeWindow.All || (_s.Window == TradeWindow.Rth ? bt.InRthWindow : bt.InEthWindow);

            if (_news.Count > 0)
            {
                double w = _s.NewsWindowMinutes;
                double start = tod.TotalMinutes;
                double end = start + Math.Max(_barMinutes, 0.0);
                foreach (var n in _news)
                {
                    double t = n.TotalMinutes;
                    // bar [start, end) overlaps [t - w, t + w]
                    if (start <= t + w && end > t - w)
                    {
                        bt.News = true;
                        break;
                    }
                }
            }
            return bt;
        }
    }
}
