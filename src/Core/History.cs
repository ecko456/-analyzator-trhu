using System;

namespace ReversalConfirmation.Core
{
    /// <summary>Per-bar statistics frozen at the bar's close (baselines exclude the bar itself).</summary>
    public sealed class BarStat
    {
        public BarTime T;
        /// <summary>ATR used for this bar's thresholds (as known before the bar, see <see cref="AtrMode"/>).</summary>
        public double Atr;
        public double AtrClassic;
        public double TodRangeMedian;
        public bool TodBaseline;
        public double DeltaZ;
        public double DeltaStd;
        public double VolumePct;
        public double VolumeZ;
        public double Efficiency;
        public double EfficiencyPct;
        /// <summary>Efficiency percentile among initiative bars only (|delta z| >= 1): "did aggression move price as usual?"</summary>
        public double InitiativeEffPct;
        public double Window4EffPct;
        public double LowThirdShare, HighThirdShare;
        public double LowThirdPct, HighThirdPct;
        public double SessionCvd;
        public double Vwap = double.NaN;
        public double VwapStd;
        public double VwapSlope = double.NaN;

        public double ExtremeThirdPct(int dir) => dir > 0 ? LowThirdPct : HighThirdPct;
    }

    /// <summary>Ring buffer addressed by absolute bar index.</summary>
    public sealed class History<T> where T : class
    {
        private readonly T[] _buf;
        private int _last = -1;

        public History(int capacity) => _buf = new T[capacity];

        public int Last => _last;
        public int Capacity => _buf.Length;

        public void Put(int index, T item)
        {
            _buf[((index % _buf.Length) + _buf.Length) % _buf.Length] = item;
            if (index > _last) _last = index;
        }

        public bool Has(int index) => index >= 0 && index <= _last && index > _last - _buf.Length && _buf[index % _buf.Length] != null;

        public T this[int index]
        {
            get
            {
                if (!Has(index)) throw new ArgumentOutOfRangeException(nameof(index), $"bar {index} not in history (last {_last})");
                return _buf[index % _buf.Length];
            }
        }

        public T TryGet(int index) => Has(index) ? _buf[index % _buf.Length] : null;

        public void Clear()
        {
            Array.Clear(_buf, 0, _buf.Length);
            _last = -1;
        }
    }
}
