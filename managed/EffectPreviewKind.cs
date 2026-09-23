namespace PedalFeel.SimHub
{
    // Values are shared with PF_PREVIEW_* in the native C ABI.
    public enum EffectPreviewKind
    {
        BrakeLoading = 0, Threshold = 1, Locking = 2, Abs = 3,
        Downshift = 4, Traction = 5, Engine = 6, Limiter = 7,
        Idle = 8, Upshift = 9, Surface = 10, Rumble = 11
    }

    internal static class EffectPreview
    {
        public const int DurationMilliseconds = 2000;
        public static bool IsValid(EffectPreviewKind effect) => effect >= EffectPreviewKind.BrakeLoading && effect <= EffectPreviewKind.Rumble;
        public static bool UsesBrake(EffectPreviewKind effect) => effect <= EffectPreviewKind.Downshift || effect >= EffectPreviewKind.Surface;
        public static bool UsesThrottle(EffectPreviewKind effect) => effect >= EffectPreviewKind.Traction;
        public static string Name(EffectPreviewKind effect)
        {
            switch (effect) {
                case EffectPreviewKind.BrakeLoading: return L10n.T("Нагрузка на тормоз");
                case EffectPreviewKind.Threshold: return L10n.T("Предупреждение о пределе");
                case EffectPreviewKind.Locking: return L10n.T("Блокировка колёс");
                case EffectPreviewKind.Abs: return L10n.T("Импульсы ABS");
                case EffectPreviewKind.Downshift: return L10n.T("Толчок при понижении");
                case EffectPreviewKind.Traction: return L10n.T("Потеря сцепления сзади");
                case EffectPreviewKind.Engine: return L10n.T("Вибрация двигателя");
                case EffectPreviewKind.Limiter: return L10n.T("Отсечка");
                case EffectPreviewKind.Idle: return L10n.T("Холостой ход");
                case EffectPreviewKind.Upshift: return L10n.T("Толчок при повышении");
                case EffectPreviewKind.Surface: return L10n.T("Неровности");
                case EffectPreviewKind.Rumble: return L10n.T("Поребрики");
                default: return L10n.T("эффект");
            }
        }
        public static bool HasStrength(EffectPreviewKind effect, PedalFeelSettings s)
        {
            if (s.EffectsGain <= 0) return false;
            switch (effect) {
                case EffectPreviewKind.BrakeLoading:
                case EffectPreviewKind.Locking: return s.Strength > 0;
                case EffectPreviewKind.Threshold: return s.Strength > 0 && s.Texture > 0;
                case EffectPreviewKind.Abs: return s.Strength > 0 && s.AbsPunch > 0;
                case EffectPreviewKind.Downshift: return s.Strength > 0 && s.DownshiftKick > 0;
                case EffectPreviewKind.Traction: return s.TractionStrength > 0;
                case EffectPreviewKind.Engine: return s.EngineTexture > 0;
                case EffectPreviewKind.Limiter: return s.LimiterStrength > 0;
                case EffectPreviewKind.Idle: return s.IdleTexture > 0;
                case EffectPreviewKind.Upshift: return s.ShiftKick > 0;
                case EffectPreviewKind.Surface:
                case EffectPreviewKind.Rumble: return s.SurfaceStrength > 0;
                default: return false;
            }
        }
    }
}
