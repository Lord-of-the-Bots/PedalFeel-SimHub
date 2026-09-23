namespace PedalFeel.SimHub
{
    public enum StatusTone { Neutral, Ready, Active, Warning, Error }

    /// <summary>A user-facing summary separate from the full diagnostic text.</summary>
    public sealed class PanelStatus
    {
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Diagnostic { get; set; } = "";
        public string OutputSummary { get; set; } = "";
        public StatusTone Tone { get; set; }

        internal static PanelStatus Create(string title, string detail, StatusTone tone) =>
            new PanelStatus { Title = L10n.T(title), Detail = L10n.T(detail), Tone = tone };
    }
}
