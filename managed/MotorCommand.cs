namespace PedalFeel.SimHub
{
    /// <summary>Physical controller channels are 0..2; intensity is 0..100, frequency is Hz.</summary>
    public sealed class MotorCommand
    {
        public int BrakeChannel { get; set; } = 1;
        public int ThrottleChannel { get; set; } = 2;
        public int BrakeFrequency { get; set; } = 10;
        public int BrakeIntensity { get; set; }
        public int ThrottleFrequency { get; set; } = 10;
        public int ThrottleIntensity { get; set; }
    }
}
