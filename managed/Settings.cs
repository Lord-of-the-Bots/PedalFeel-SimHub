using System;

namespace PedalFeel.SimHub
{
    /// <summary>Serializable tuning for a car; connection choices are retained by the host.</summary>
    public sealed class PedalFeelSettings
    {
        public const double BaseEffectsGain = 2.1;
        public bool Enabled { get; set; } = false;
        public bool BrakeEnabled { get; set; } = true;
        public bool ThrottleEnabled { get; set; } = true;
        public int BrakeChannel { get; set; } = 1;
        public int ThrottleChannel { get; set; } = 2;
        public double EffectsGain { get; set; } = BaseEffectsGain;
        public double GripThreshold { get; set; } = .91;
        public double Strength { get; set; } = .70;
        public double Texture { get; set; } = .62;
        public double AbsPunch { get; set; } = .72;
        public double TractionStrength { get; set; } = .65;
        public double EngineTexture { get; set; } = .48;
        public double ShiftKick { get; set; } = .20;
        public double LimiterStrength { get; set; } = .20;
        public double DownshiftKick { get; set; } = .20;
        public double IdleTexture { get; set; } = .28;
        public double SurfaceStrength { get; set; } = .28;
        public double[] BrakeMinimum { get; set; } = new double[] { 0, 0, 0, 0 };
        public double[] ThrottleMinimum { get; set; } = new double[] { 0, 0, 0, 0 };
        public double[] BrakeMaximum { get; set; } = new double[] { 35, 35, 35, 35 };
        public double[] ThrottleMaximum { get; set; } = new double[] { 35, 35, 35, 35 };

        public PedalFeelSettings Clone()
        {
            var clone = (PedalFeelSettings)MemberwiseClone();
            clone.BrakeMinimum = BrakeMinimum == null ? new double[4] : (double[])BrakeMinimum.Clone();
            clone.ThrottleMinimum = ThrottleMinimum == null ? new double[4] : (double[])ThrottleMinimum.Clone();
            clone.BrakeMaximum = BrakeMaximum == null ? new double[] { 35, 35, 35, 35 } : (double[])BrakeMaximum.Clone();
            clone.ThrottleMaximum = ThrottleMaximum == null ? new double[] { 35, 35, 35, 35 } : (double[])ThrottleMaximum.Clone();
            return clone;
        }

        public void Normalize()
        {
            BrakeChannel = Math.Max(0, Math.Min(2, BrakeChannel));
            ThrottleChannel = Math.Max(0, Math.Min(2, ThrottleChannel));
            EffectsGain = Clamp(EffectsGain, 0, BaseEffectsGain * 2, BaseEffectsGain);
            // A duplicate mapping is refused by the UI/controller, never silently reassigned.
            GripThreshold = Clamp(GripThreshold, .75, 1.05, .91);
            Strength = Clamp(Strength, 0, 1, .70);
            Texture = Clamp(Texture, 0, 1, .62);
            AbsPunch = Clamp(AbsPunch, 0, 1, .72);
            TractionStrength = Clamp(TractionStrength, 0, 1, .65);
            EngineTexture = Clamp(EngineTexture, 0, 1, .48);
            ShiftKick = Clamp(ShiftKick, 0, 1, .20);
            LimiterStrength = Clamp(LimiterStrength, 0, 1, .20);
            DownshiftKick = Clamp(DownshiftKick, 0, 1, .20);
            IdleTexture = Clamp(IdleTexture, 0, 1, .28);
            SurfaceStrength = Clamp(SurfaceStrength, 0, 1, .28);
            BrakeMinimum = NormalizeCurve(BrakeMinimum, 0);
            BrakeMaximum = NormalizeCurve(BrakeMaximum, 35);
            ThrottleMinimum = NormalizeCurve(ThrottleMinimum, 0);
            ThrottleMaximum = NormalizeCurve(ThrottleMaximum, 35);
            OrderCurve(BrakeMinimum, BrakeMaximum);
            OrderCurve(ThrottleMinimum, ThrottleMaximum);
        }

        private static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? fallback : Math.Max(minimum, Math.Min(maximum, value));
        }

        private static double[] NormalizeCurve(double[]? curve, double fallback)
        {
            var normalized = new double[4];
            for (var index = 0; index < normalized.Length; ++index)
                normalized[index] = curve != null && index < curve.Length
                    ? Clamp(curve[index], 0, 100, fallback) : fallback;
            return normalized;
        }

        private static void OrderCurve(double[] minimum, double[] maximum)
        {
            for (var index = 0; index < minimum.Length; ++index)
            {
                if (minimum[index] <= maximum[index]) continue;
                var previousMinimum = minimum[index];
                minimum[index] = maximum[index];
                maximum[index] = previousMinimum;
            }
        }
    }
}
