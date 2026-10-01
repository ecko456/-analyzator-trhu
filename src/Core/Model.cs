using System;
using System.Collections.Generic;

namespace ReversalConfirmation.Core
{
    public enum MarkType
    {
        Reversal,
        Confirmation,
        Retest,
        AbsorptionFailed,
        ContextCancelled
    }

    /// <summary>Immutable chart marker. Once emitted it never changes (no repaint); state changes are new marks.</summary>
    public sealed class Mark
    {
        public int Bar;
        public int Dir;
        public MarkType Type;
        public string Label;
        /// <summary>Anchor price: bar low for bullish marks, bar high for bearish ones.</summary>
        public double Price;
        public double Score;
        public double Probability = double.NaN;
        public bool Strong;
        public int ContextId;
        public string Tooltip;
    }

    public enum ZoneState
    {
        Active,
        Filled,
        Expired,
        Failed,
        Cancelled
    }

    public sealed class TargetLine
    {
        public string Name;
        public double Price;
        public int HitBar = -1;
    }

    /// <summary>
    /// Limit entry zone. Price, type, start and score never change; the life-cycle fields
    /// (state, end/fill bars, trade lines) only move forward.
    /// </summary>
    public sealed class Zone
    {
        public int Id;
        public int ContextId;
        public int Dir;
        public ZoneTypes Type;
        public double Price;
        public int StartBar;
        public int ExpiryBar;
        public double Score;
        public double ReversalScore;
        public double ConfirmationScore;
        public double Probability = double.NaN;
        public string Tooltip;

        public ZoneState State = ZoneState.Active;
        public int EndBar = -1;
        public int FillBar = -1;
        public double Entry = double.NaN;
        public double Stop;
        public double R;
        public readonly List<TargetLine> Targets = new List<TargetLine>();
        public int TradeEndBar = -1;
        public int StopHitBar = -1;
        public int BreakEvenBar = -1;
        public double ResultR = double.NaN;
        public bool WithTrend;

        public string TypeName
        {
            get
            {
                switch (Type)
                {
                    case ZoneTypes.ConfirmationVpoc: return "VPOC potvrzení";
                    case ZoneTypes.ConfirmationVal: return "VAL potvrzení";
                    case ZoneTypes.ReversalVpoc: return "VPOC reversalu";
                    case ZoneTypes.Retest: return "Retest VPOC";
                    default: return Type.ToString();
                }
            }
        }
    }

    public enum EngineEventType
    {
        Reversal,
        Confirmation,
        Retest,
        NewZone,
        ZoneFilled,
        ContextCancelled,
        AbsorptionFailed
    }

    public struct EngineEvent
    {
        public EngineEventType Type;
        public int Bar;
        public int Dir;
        public double Price;
        public double Score;
        public string Text;
    }

    /// <summary>Aggregated back-test statistics per score bucket.</summary>
    public sealed class BucketStats
    {
        public static readonly double[] Edges = { 60, 70, 80, 1000 };
        public readonly int[] N = new int[3];
        public readonly int[] HitT1 = new int[3];
        public readonly double[] SumR = new double[3];
        public readonly double[] SumR15 = new double[3];

        public static int BucketOf(double score)
        {
            for (int i = 0; i < 3; i++)
                if (score >= Edges[i] && score < Edges[i + 1]) return i;
            return -1;
        }

        public void Add(double score, bool hit, double r, double r15)
        {
            int b = BucketOf(score);
            if (b < 0) return;
            N[b]++;
            if (hit) HitT1[b]++;
            if (!double.IsNaN(r)) SumR[b] += r;
            if (!double.IsNaN(r15)) SumR15[b] += r15;
        }

        public void Clear()
        {
            Array.Clear(N, 0, 3);
            Array.Clear(HitT1, 0, 3);
            Array.Clear(SumR, 0, 3);
            Array.Clear(SumR15, 0, 3);
        }
    }
}
