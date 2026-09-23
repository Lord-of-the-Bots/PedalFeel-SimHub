using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PedalFeel.SimHub
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeConfig
    {
        public uint Size, Version;
        public int BrakeEnabled, ThrottleEnabled;
        public double GripThreshold, Strength, Texture, AbsPunch, TractionStrength,
            EngineTexture, ShiftKick, IdleTexture, SurfaceStrength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] BrakeMinimum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] BrakeMaximum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] ThrottleMinimum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] ThrottleMaximum;
        public double EffectsGain, LimiterStrength, DownshiftKick;

        public static NativeConfig From(PedalFeelSettings s) => new NativeConfig {
            Size = (uint)Marshal.SizeOf(typeof(NativeConfig)), Version = 3,
            BrakeEnabled = s.BrakeEnabled ? 1 : 0, ThrottleEnabled = s.ThrottleEnabled ? 1 : 0,
            GripThreshold = s.GripThreshold, Strength = s.Strength, Texture = s.Texture,
            AbsPunch = s.AbsPunch, TractionStrength = s.TractionStrength,
            EngineTexture = s.EngineTexture, ShiftKick = s.ShiftKick, IdleTexture = s.IdleTexture,
            SurfaceStrength = s.SurfaceStrength,
            BrakeMinimum = Round(s.BrakeMinimum), BrakeMaximum = Round(s.BrakeMaximum),
            ThrottleMinimum = Round(s.ThrottleMinimum), ThrottleMaximum = Round(s.ThrottleMaximum),
            EffectsGain = s.EffectsGain, LimiterStrength = s.LimiterStrength, DownshiftKick = s.DownshiftKick
        };
        private static int[] Round(double[] values) => values.Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero)).ToArray();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeOutput
    {
        public uint Size, Version;
        public int NewSample, Connected, DrivingActive, Stale, BrakeHz, BrakeIntensity,
            ThrottleHz, ThrottleIntensity, BrakeMode, ThrottleMode;
        public double BrakeRaw, ThrottleRaw, SampleAgeMs, SessionTime;
    }

    internal interface IEffectEngine : IDisposable
    {
        void Configure(PedalFeelSettings settings);
        NativeOutput Tick();
    }

    internal interface IEffectPreviewEngine
    {
        NativeOutput Preview(EffectPreviewKind effect, double elapsedSeconds);
    }

    // Explicit absolute loading avoids dependence on SimHub's current directory.
    // The unmanaged module remains loaded for the process lifetime; instance handles are disposed.
    internal sealed class NativeEngine : IEffectEngine, IEffectPreviewEngine
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConfigureDelegate(IntPtr instance, ref NativeConfig config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TickDelegate(IntPtr instance, ref NativeOutput output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PreviewDelegate(ref NativeConfig config, int effect, double elapsedSeconds, ref NativeOutput output);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string filename);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static readonly object ModuleLock = new object();
        private static IntPtr module;
        private static CreateDelegate? create;
        private static DestroyDelegate? destroy;
        private static ConfigureDelegate? configure;
        private static TickDelegate? tick;
        private static PreviewDelegate? preview;
        private IntPtr handle;
        private NativeConfig currentConfig;
        private bool configured;

        public NativeEngine()
        {
            lock (ModuleLock) {
                if (module == IntPtr.Zero) {
                    string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                    string filename = Path.Combine(root, "PedalFeel", Environment.Is64BitProcess ? "x64" : "x86", "PedalFeel.Native.dll");
                    module = LoadLibraryW(filename);
                    if (module == IntPtr.Zero) throw new InvalidOperationException(L10n.F("Не удалось загрузить движок PedalFeel (код {0}). Проверьте установку файлов.", Marshal.GetLastWin32Error()));
                }
                create = Get<CreateDelegate>("pf_create"); destroy = Get<DestroyDelegate>("pf_destroy");
                configure = Get<ConfigureDelegate>("pf_configure"); tick = Get<TickDelegate>("pf_tick");
                preview = Get<PreviewDelegate>("pf_preview");
            }
            handle = create!();
            if (handle == IntPtr.Zero) throw new InvalidOperationException(L10n.T("Не удалось создать движок PedalFeel."));
        }
        private static T Get<T>(string name) where T : Delegate
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new InvalidOperationException(L10n.F("Несовместимая версия движка PedalFeel: {0}", name));
            return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        public void Configure(PedalFeelSettings settings)
        {
            var config = NativeConfig.From(settings);
            if (configure!(handle, ref config) != 0) throw new InvalidOperationException(L10n.T("Движок отклонил настройки PedalFeel."));
            currentConfig = config; configured = true;
        }
        public NativeOutput Tick()
        {
            var output = new NativeOutput { Size = (uint)Marshal.SizeOf(typeof(NativeOutput)), Version = 3 };
            if (tick!(handle, ref output) != 0) throw new InvalidOperationException(L10n.T("Ошибка обработки телеметрии PedalFeel."));
            return output;
        }
        public NativeOutput Preview(EffectPreviewKind effect, double elapsedSeconds)
        {
            if (!configured || handle == IntPtr.Zero) throw new InvalidOperationException(L10n.T("Движок ещё не готов к проверке эффекта."));
            var output = new NativeOutput { Size = (uint)Marshal.SizeOf(typeof(NativeOutput)), Version = 3 };
            if (preview!(ref currentConfig, (int)effect, elapsedSeconds, ref output) != 0)
                throw new InvalidOperationException(L10n.T("Не удалось воспроизвести пример эффекта."));
            return output;
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { destroy!(handle); handle = IntPtr.Zero; }
        }
    }
}
