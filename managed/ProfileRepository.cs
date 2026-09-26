using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PedalFeel.SimHub
{
    internal sealed class PresetOrigin
    {
        public string PresetId { get; set; } = "";
        public string Basis { get; set; } = "";
        public int Revision { get; set; }
        public int Confidence { get; set; }
        public bool Automatic { get; set; }
        public bool UserEdited { get; set; }
        public PresetOrigin Clone() => (PresetOrigin)MemberwiseClone();
    }
    internal sealed class NamedProfile
    {
        public string Name { get; set; } = "";
        public string BasisId { get; set; } = CarPresets.Standard;
        public bool LocalizedName { get; set; }
        public bool UserEdited { get; set; }
        public PedalFeelSettings Settings { get; set; } = new PedalFeelSettings();
        public PresetOrigin? LegacyOrigin { get; set; }
    }
    internal sealed class ProfileCatalog
    {
        public int SchemaVersion { get; set; } = 1;
        public int TuningRevision { get; set; }
        public int StandardPresetRevision { get; set; }
        public PedalFeelSettings Device { get; set; } = new PedalFeelSettings();
        public Dictionary<string, PedalFeelSettings> Cars { get; set; } = new Dictionary<string, PedalFeelSettings>();
        public Dictionary<string, string> CarNames { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ModelAliases { get; set; } = new Dictionary<string, string>();
        // Additive metadata: older catalogs retain every saved car value without re-seeding.
        public Dictionary<string, PresetOrigin> Origins { get; set; } = new Dictionary<string, PresetOrigin>();
        public Dictionary<string, NamedProfile> Profiles { get; set; } = new Dictionary<string, NamedProfile>();
        public Dictionary<string, string> CarAssignments { get; set; } = new Dictionary<string, string>();
        public string SelectedProfileId { get; set; } = CarPresets.Standard;
    }

    // Independent of SimHub's selected device preset: a vehicle change must never discard another car's tuning.
    internal sealed class ProfileRepository : IDisposable
    {
        public const string DefaultKey = "default";
        private const int CurrentTuningRevision = 1;
        private readonly object sync = new object();
        private readonly string path;
        private readonly Timer saveTimer;
        private ProfileCatalog catalog = FreshCatalog();
        private bool dirty, disposed, recoveryReadOnly;
        private string saveError = "";
        public string SaveError { get => L10n.T(saveError); private set => saveError = value; }
        public string CurrentKey { get; private set; } = DefaultKey;
        public string CurrentName { get; private set; } = "Базовый профиль — машина ещё не определена";
        public string CurrentProfileId { get { lock (sync) return catalog.SelectedProfileId; } }

        public ProfileRepository(string path)
        {
            this.path = path;
            saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            bool migrated = false;
            if (File.Exists(path)) {
                try { catalog = Read(path, out migrated); }
                catch (Exception) {
                    PreserveUnreadable(path);
                    try {
                        catalog = Read(path + ".bak", out migrated);
                        SaveError = "Настройки восстановлены из резервной копии.";
                    } catch (Exception) {
                        if (File.Exists(path + ".bak")) PreserveUnreadable(path + ".bak");
                        SaveError = recoveryReadOnly ? "Файл настроек недоступен. Сохранение отключено, чтобы сохранить исходные данные." : "Не удалось прочитать настройки. Исходники сохранены в файлах .recovery; используются начальные значения.";
                    }
                }
            }
            if (migrated) {
                // A dedicated migration snapshot survives later rotating .bak saves.
                if (File.Exists(path) && !File.Exists(path + ".before-0.5.0"))
                    File.Copy(path, path + ".before-0.5.0");
                ScheduleSave();
            }
        }
        private static ProfileCatalog FreshCatalog()
        {
            var result = new ProfileCatalog { SchemaVersion = 3, StandardPresetRevision = 1, TuningRevision = CurrentTuningRevision };
            AddSeeds(result); return result;
        }
        private static void AddSeeds(ProfileCatalog result)
        {
            foreach (var basis in CarPresets.Bases.Where(p => p.Key != "current"))
                if (!result.Profiles.ContainsKey(basis.Key)) result.Profiles[basis.Key] = new NamedProfile {
                    Name = basis.Value, BasisId = basis.Key, LocalizedName = true, Settings = CarPresets.CreateBasis(basis.Key)
                };
        }
        private void PreserveUnreadable(string original)
        {
            try { File.Copy(original, original + ".recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); }
            catch { recoveryReadOnly = true; }
        }
        private static ProfileCatalog Read(string path, out bool migrated)
        {
            migrated = false;
            var json = JObject.Parse(File.ReadAllText(path));
            var result = json.ToObject<ProfileCatalog>() ?? throw new InvalidDataException("Empty settings");
            if ((result.SchemaVersion != 1 && result.SchemaVersion != 2 && result.SchemaVersion != 3) || result.Device == null || result.Cars == null || result.CarNames == null)
                throw new InvalidDataException("Unsupported settings schema");
            if (result.ModelAliases == null) result.ModelAliases = new Dictionary<string, string>();
            if (result.Origins == null) result.Origins = new Dictionary<string, PresetOrigin>();
            bool changed = false;
            if (result.SchemaVersion == 1) {
                MigrateTuning(result, json);
                result.Device.Normalize();
                result.Profiles = new Dictionary<string, NamedProfile>();
                result.CarAssignments = new Dictionary<string, string>();
                AddSeeds(result);
                foreach (var pair in result.Cars) {
                    if (pair.Value == null) throw new InvalidDataException("Invalid car settings");
                    var settings = pair.Value.Clone(); settings.EffectsGain = result.Device.EffectsGain; settings.Normalize();
                    string id = "import-" + Guid.NewGuid().ToString("N");
                    string name = result.CarNames.TryGetValue(pair.Key, out var savedName) && !string.IsNullOrWhiteSpace(savedName) ? savedName : pair.Key;
                    result.Origins.TryGetValue(pair.Key, out var origin);
                    result.Profiles[id] = new NamedProfile { Name = UniqueName(result, name), LocalizedName = pair.Key == DefaultKey && name == "Базовый профиль — машина ещё не определена", Settings = settings,
                        BasisId = origin == null ? "legacy" : "legacy:" + origin.PresetId,
                        LegacyOrigin = origin?.Clone(), UserEdited = origin?.UserEdited ?? true };
                    if (pair.Key == DefaultKey) result.SelectedProfileId = id;
                    else result.CarAssignments[pair.Key] = id;
                }
                if (!result.Cars.ContainsKey(DefaultKey) && Math.Abs(result.Device.EffectsGain - PedalFeelSettings.BaseEffectsGain) > .000001) {
                    string id = "import-" + Guid.NewGuid().ToString("N");
                    result.Profiles[id] = new NamedProfile { Name = "Сохранённый базовый профиль", LocalizedName = true,
                        BasisId = "legacy", Settings = new PedalFeelSettings { EffectsGain = result.Device.EffectsGain } };
                    result.SelectedProfileId = id;
                }
                result.SchemaVersion = 2; changed = true;
            } else if (json["Profiles"] == null || result.Profiles == null || result.Profiles.Count == 0 || result.CarAssignments == null)
                throw new InvalidDataException("Invalid named profile library");
            if (result.SchemaVersion < 3) {
                // Backed up by atomic Save before migration reaches disk. Keep named user
                // profiles; replace retired built-in starters and their car assignments.
                var retired = new[] { "author-subtle", "author-aggressive", "formula" };
                foreach (var id in retired) {
                    result.Profiles.Remove(id);
                    foreach (var car in result.CarAssignments.Where(p => p.Value == id).Select(p => p.Key).ToArray())
                        result.CarAssignments[car] = CarPresets.Standard;
                    if (result.SelectedProfileId == id) result.SelectedProfileId = CarPresets.Standard;
                }
                AddSeeds(result);
                foreach (var profile in result.Profiles.Values) profile.Settings.EffectsGain = PedalFeelSettings.BaseEffectsGain;
                result.SchemaVersion = 3; changed = true;
            }
            result.Device.Normalize();
            foreach (var profile in result.Profiles.Values) {
                if (profile == null || profile.Settings == null || string.IsNullOrWhiteSpace(profile.Name))
                    throw new InvalidDataException("Invalid named profile");
                profile.Settings.Normalize();
            }
            if (!result.Profiles.ContainsKey(CarPresets.Standard) || !result.Profiles.ContainsKey(CarPresets.AuthorBalanced)) { AddSeeds(result); changed = true; }
            if (result.StandardPresetRevision < 1) {
                var standard = result.Profiles[CarPresets.Standard];
                if (standard.BasisId == CarPresets.Standard) {
                    // Advance unchanged old defaults once; preserve personal tuning and copies.
                    var tuning = standard.Settings;
                    if (Math.Abs(tuning.Texture - 1) < .000001) tuning.Texture = .35;
                    if (Math.Abs(tuning.AbsPunch - 1) < .000001) tuning.AbsPunch = .70;
                    if (Math.Abs(tuning.DownshiftKick - .80) < .000001) tuning.DownshiftKick = 1;
                    if (Math.Abs(tuning.TractionStrength - .40) < .000001) tuning.TractionStrength = .30;
                    if (Math.Abs(tuning.ShiftKick - .80) < .000001) tuning.ShiftKick = .65;
                    if (Math.Abs(tuning.SurfaceStrength - .30) < .000001) tuning.SurfaceStrength = .70;
                }
                result.StandardPresetRevision = 1; changed = true;
            }
            if (!result.Profiles.ContainsKey(result.SelectedProfileId ?? "")) { result.SelectedProfileId = CarPresets.Standard; changed = true; }
            foreach (var pair in result.CarAssignments.ToArray())
                if (string.IsNullOrEmpty(pair.Value) || !result.Profiles.ContainsKey(pair.Value)) {
                    result.CarAssignments[pair.Key] = CarPresets.Standard; changed = true;
                }
            // A provisional model and its stable ID are one car. Retain imported
            // profiles themselves, but keep a single authoritative assignment.
            foreach (var alias in result.ModelAliases) {
                if (alias.Key == alias.Value || !result.CarAssignments.TryGetValue(alias.Key, out var provisional)) continue;
                if (!result.CarAssignments.ContainsKey(alias.Value)) result.CarAssignments[alias.Value] = provisional;
                result.CarAssignments.Remove(alias.Key); changed = true;
            }
            foreach (var seed in CarPresets.Bases.Where(p => p.Key != "current")) {
                if (result.Profiles.TryGetValue(seed.Key, out var profile) && profile.LocalizedName) {
                    profile.Name = seed.Value;
                }
            }
            migrated = changed;
            return result;
        }
        private static bool MigrateTuning(ProfileCatalog result, JObject source)
        {
            if (result.TuningRevision >= CurrentTuningRevision) return false;
            // 0.3.3 displayed its default absolute gain of 3 as x1. Keep personalized
            // absolute gains, but move the old default to the requested softer x1.
            if (Math.Abs(result.Device.EffectsGain - 3) < .000001)
                result.Device.EffectsGain = PedalFeelSettings.BaseEffectsGain;
            foreach (var pair in result.Cars) {
                if (pair.Value == null) throw new InvalidDataException("Invalid car settings");
                if (!result.Origins.TryGetValue(pair.Key, out var origin) || origin == null || origin.Revision != 1 ||
                    !CarPresets.TryGetRevisionOneShift(origin.PresetId, out var previousShift)) continue;
                if (Math.Abs(pair.Value.ShiftKick - previousShift) < .000001)
                    pair.Value.ShiftKick = Math.Min(previousShift, .20);
                // These fields did not exist in the old catalog. Their electric defaults
                // remain zero even when the user customized other existing car effects.
                if (origin.PresetId == "electric") {
                    var settingsJson = (source["Cars"] as JObject)?[pair.Key] as JObject;
                    if (settingsJson?[nameof(PedalFeelSettings.LimiterStrength)] == null) pair.Value.LimiterStrength = 0;
                    if (settingsJson?[nameof(PedalFeelSettings.DownshiftKick)] == null) pair.Value.DownshiftKick = 0;
                }
                origin.Revision = CarPresets.Revision;
            }
            result.TuningRevision = CurrentTuningRevision;
            return true;
        }
        public static string CarKey(string? game, string? id, string? model)
        {
            if (!string.Equals(game?.Trim(), "iracing", StringComparison.OrdinalIgnoreCase)) return DefaultKey;
            string car = !string.IsNullOrWhiteSpace(id) ? "id:" + id!.Trim() : "model:" + model?.Trim();
            return car == "model:" ? DefaultKey : "iracing|" + car;
        }
        public bool SelectCar(string? game, string? id, string? model)
        {
            lock (sync) {
                string key = CarKey(game, id, model);
                if (key == DefaultKey) return string.Equals(game?.Trim(), "iracing", StringComparison.OrdinalIgnoreCase)
                    ? false : Select(DefaultKey, null);
                string modelKey = "iracing|model:" + model?.Trim();
                bool hasModel = !string.IsNullOrWhiteSpace(model), identityUpgrade = false;
                if (!string.IsNullOrWhiteSpace(id) && hasModel) {
                    if (catalog.CarAssignments.TryGetValue(modelKey, out var provisional)) {
                        if (!catalog.CarAssignments.ContainsKey(key)) catalog.CarAssignments[key] = provisional;
                        catalog.CarAssignments.Remove(modelKey); ScheduleSave();
                    }
                    identityUpgrade = CurrentKey == modelKey;
                    if (!catalog.ModelAliases.TryGetValue(modelKey, out var alias) || alias != key) {
                        catalog.ModelAliases[modelKey] = key; ScheduleSave();
                    }
                } else if (hasModel) {
                    if (CurrentKey.StartsWith("iracing|id:", StringComparison.Ordinal) && string.Equals(CurrentName, model!.Trim(), StringComparison.OrdinalIgnoreCase)) key = CurrentKey;
                    else if (catalog.ModelAliases.TryGetValue(modelKey, out var known)) key = known;
                }
                // The same car gaining its stable identity must retain a manual selection.
                if (identityUpgrade) CurrentKey = key;
                return Select(key, model) || identityUpgrade;
            }
        }
        public bool Select(string key, string? name)
        {
            lock (sync) {
                string display = key == DefaultKey ? "Базовый профиль — машина ещё не определена" : (!string.IsNullOrWhiteSpace(name) ? name!.Trim() : key);
                bool changedCar = CurrentKey != key, changedName = CurrentName != display;
                CurrentKey = key; CurrentName = display;
                if (changedCar) catalog.SelectedProfileId = AssignedOrDefault(key);
                if (!catalog.CarNames.TryGetValue(key, out var previous) || previous != display) {
                    catalog.CarNames[key] = display; ScheduleSave();
                }
                if (changedCar) ScheduleSave();
                return changedCar;
            }
        }
        private string AssignedOrDefault(string key) => catalog.CarAssignments.TryGetValue(key, out var id) && catalog.Profiles.ContainsKey(id) ? id : CarPresets.Standard;
        public PedalFeelSettings Current()
        {
            lock (sync) {
                var result = catalog.Profiles[CurrentProfileId].Settings.Clone();
                CopyDevice(catalog.Device, result); result.Normalize(); return result;
            }
        }
        public ProfilePanelState PanelState()
        {
            lock (sync) return new ProfilePanelState {
                Profiles = catalog.Profiles.Select(p => new KeyValuePair<string, string>(p.Key, DisplayName(p.Value))).ToList(),
                Bases = CarPresets.Bases.ToList(), SelectedId = CurrentProfileId,
                AssignedId = catalog.CarAssignments.TryGetValue(CurrentKey, out var assigned) ? assigned : "",
                CurrentCar = CurrentKey == DefaultKey ? "" : CurrentName,
                CanAssign = CurrentKey.StartsWith("iracing|", StringComparison.Ordinal), Description = DescribeProfile(catalog.Profiles[CurrentProfileId])
            };
        }
        public bool SelectProfile(string id)
        {
            lock (sync) {
                RequireProfile(id);
                if (CurrentProfileId == id) return false;
                catalog.SelectedProfileId = id; ScheduleSave(); return true;
            }
        }
        public string CreateProfile(string name, string basisId, bool assignCurrent)
        {
            lock (sync) {
                string trimmed = name?.Trim() ?? "";
                if (trimmed.Length == 0) throw new ArgumentException(L10n.T("Введите название профиля."));
                if (trimmed.Length > 80) throw new ArgumentException(L10n.T("Название профиля не должно быть длиннее 80 символов."));
                if (trimmed.Any(char.IsControl)) throw new ArgumentException(L10n.T("Название профиля должно занимать одну строку."));
                if (catalog.Profiles.Values.Any(p => string.Equals(DisplayName(p), trimmed, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(L10n.T("Профиль с таким названием уже существует."));
                if (assignCurrent) RequireCurrentCar();
                var selected = catalog.Profiles[CurrentProfileId];
                var settings = basisId == "current" ? selected.Settings.Clone() : CarPresets.CreateBasis(basisId);
                string id = "profile-" + Guid.NewGuid().ToString("N");
                catalog.Profiles[id] = new NamedProfile { Name = trimmed, Settings = settings,
                    BasisId = basisId == "current" ? selected.BasisId : basisId,
                    UserEdited = basisId == "current" && selected.UserEdited,
                    LegacyOrigin = basisId == "current" ? selected.LegacyOrigin?.Clone() : null };
                catalog.SelectedProfileId = id;
                if (assignCurrent) catalog.CarAssignments[CurrentKey] = id;
                ScheduleSave(); return id;
            }
        }
        public bool AssignCurrentProfile()
        {
            lock (sync) {
                RequireCurrentCar();
                if (catalog.CarAssignments.TryGetValue(CurrentKey, out var previous) && previous == CurrentProfileId) return false;
                catalog.CarAssignments[CurrentKey] = CurrentProfileId; ScheduleSave(); return true;
            }
        }
        public bool ClearCurrentAssignment()
        {
            lock (sync) {
                RequireCurrentCar();
                bool removed = catalog.CarAssignments.Remove(CurrentKey);
                foreach (var alias in catalog.ModelAliases.Where(a => a.Value == CurrentKey).ToArray())
                    removed |= catalog.CarAssignments.Remove(alias.Key);
                if (removed) { catalog.SelectedProfileId = CarPresets.Standard; ScheduleSave(); }
                return removed;
            }
        }
        public int DeleteProfile(string id)
        {
            lock (sync) {
                RequireProfile(id);
                if (id == CarPresets.Standard || id == CarPresets.AuthorBalanced)
                    throw new InvalidOperationException(L10n.T("Встроенный профиль нельзя удалить."));
                int count = 0;
                foreach (var car in catalog.CarAssignments.Where(p => p.Value == id).Select(p => p.Key).ToArray()) {
                    catalog.CarAssignments[car] = CarPresets.Standard; count++;
                }
                catalog.Profiles.Remove(id);
                if (catalog.SelectedProfileId == id) catalog.SelectedProfileId = CarPresets.Standard;
                ScheduleSave(); return count;
            }
        }
        private void RequireCurrentCar()
        {
            if (!CurrentKey.StartsWith("iracing|", StringComparison.Ordinal)) throw new InvalidOperationException(L10n.T("Сначала выберите машину в iRacing."));
        }
        private NamedProfile RequireProfile(string id)
        {
            if (id == null || !catalog.Profiles.TryGetValue(id, out var profile)) throw new ArgumentException(L10n.F("Неизвестный профиль: {0}", id ?? ""));
            return profile;
        }
        public void CommitProfile(string profileId, PedalFeelSettings settings)
        {
            lock (sync) {
                var profile = RequireProfile(profileId);
                var copy = settings.Clone(); copy.Normalize();
                if (!SameEffects(profile.Settings, copy)) profile.UserEdited = true;
                profile.Settings = copy; CopyDevice(copy, catalog.Device); ScheduleSave();
            }
        }
        public void Commit(PedalFeelSettings settings) => CommitProfile(CurrentProfileId, settings);
        public void Commit(string key, string name, PedalFeelSettings settings)
        {
            lock (sync) {
                if (key == CurrentKey) { CommitProfile(CurrentProfileId, settings); return; }
                // A legacy delayed callback targets its displayed car, never a newly arrived car.
                if (!catalog.CarAssignments.TryGetValue(key, out var id)) {
                    id = "profile-" + Guid.NewGuid().ToString("N");
                    catalog.Profiles[id] = new NamedProfile { Name = UniqueName(catalog, name), Settings = settings.Clone(), BasisId = "legacy", UserEdited = true };
                    catalog.CarAssignments[key] = id;
                }
                catalog.CarNames[key] = name; CommitProfile(id, settings);
            }
        }
        public PedalFeelSettings ResetCurrent()
        {
            lock (sync) {
                var profile = catalog.Profiles[CurrentProfileId];
                var settings = BasisSettings(profile.BasisId); CopyDevice(catalog.Device, settings);
                profile.Settings = settings.Clone(); profile.UserEdited = false; ScheduleSave(); return settings;
            }
        }
        public PedalFeelSettings ApplyPreset(string key, string name, string presetId)
        {
            lock (sync) {
                string basisId = presetId == "auto" ? CarPresets.AuthorBalanced : presetId;
                var settings = BasisSettings(basisId); CopyDevice(catalog.Device, settings);
                string id;
                if (key == CurrentKey) id = CurrentProfileId;
                else if (!catalog.CarAssignments.TryGetValue(key, out id)) {
                    id = "profile-" + Guid.NewGuid().ToString("N");
                    catalog.Profiles[id] = new NamedProfile { Name = UniqueName(catalog, name) };
                    catalog.CarAssignments[key] = id;
                }
                var profile = catalog.Profiles[id]; profile.Settings = settings.Clone(); profile.BasisId = basisId;
                profile.UserEdited = false; profile.LegacyOrigin = null; catalog.CarNames[key] = name; ScheduleSave(); return settings;
            }
        }
        private static PedalFeelSettings BasisSettings(string id)
        {
            if (id.StartsWith("legacy:", StringComparison.Ordinal)) id = id.Substring(7);
            if (CarPresets.Bases.Any(p => p.Key == id && id != "current")) return CarPresets.CreateBasis(id);
            try { return CarPresets.Get(id).Create(); } catch (ArgumentException) { return CarPresets.CreateBasis(CarPresets.AuthorBalanced); }
        }
        public string Describe(string key, string name)
        {
            lock (sync) return DescribeProfile(catalog.Profiles[key == CurrentKey ? CurrentProfileId : AssignedOrDefault(key)]);
        }
        private static string DescribeProfile(NamedProfile profile)
        {
            string description = L10n.F("Основа: {0}.", L10n.T(CarPresets.BasisLabel(profile.BasisId)));
            if (profile.UserEdited) description += " " + L10n.T("с вашими изменениями");
            return description;
        }
        private static string DisplayName(NamedProfile profile) => profile.LocalizedName ? L10n.T(profile.Name) : profile.Name;
        private static string UniqueName(ProfileCatalog result, string name)
        {
            string value = name; int suffix = 2;
            while (result.Profiles.Values.Any(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase))) value = name + " (" + suffix++ + ")";
            return value;
        }
        private static bool SameEffects(PedalFeelSettings a, PedalFeelSettings b) =>
            a.ThrottleStrength == b.ThrottleStrength && a.BrakeEngineTexture == b.BrakeEngineTexture && a.BrakeIdleTexture == b.BrakeIdleTexture &&
            a.BrakeEnabled == b.BrakeEnabled && a.ThrottleEnabled == b.ThrottleEnabled && a.EffectsGain == b.EffectsGain &&
            a.GripThreshold == b.GripThreshold && a.Strength == b.Strength && a.Texture == b.Texture &&
            a.AbsPunch == b.AbsPunch && a.TractionStrength == b.TractionStrength && a.EngineTexture == b.EngineTexture &&
            a.ShiftKick == b.ShiftKick && a.LimiterStrength == b.LimiterStrength && a.DownshiftKick == b.DownshiftKick &&
            a.IdleTexture == b.IdleTexture && a.SurfaceStrength == b.SurfaceStrength;
        private static void CopyDevice(PedalFeelSettings source, PedalFeelSettings target)
        {
            target.Enabled = source.Enabled;
            target.BrakeChannel = source.BrakeChannel; target.ThrottleChannel = source.ThrottleChannel;
            target.BrakeMinimum = (double[])source.BrakeMinimum.Clone(); target.BrakeMaximum = (double[])source.BrakeMaximum.Clone();
            target.ThrottleMinimum = (double[])source.ThrottleMinimum.Clone(); target.ThrottleMaximum = (double[])source.ThrottleMaximum.Clone();
        }
        private void ScheduleSave()
        {
            dirty = true;
            if (!disposed) saveTimer.Change(350, Timeout.Infinite);
        }
        public void Save()
        {
            lock (sync) {
                if (!dirty) return;
                if (recoveryReadOnly) { SaveError = "Не удалось сохранить настройки: исходный файл недоступен и оставлен без изменений."; return; }
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string temporary = path + ".tmp";
                    File.WriteAllText(temporary, JsonConvert.SerializeObject(catalog, Formatting.Indented));
                    if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                    else File.Move(temporary, path);
                    dirty = false; SaveError = "";
                } catch (Exception error) { SaveError = L10n.F("Не удалось сохранить настройки: {0}", error.Message); }
            }
        }
        public void Dispose()
        {
            lock (sync) { disposed = true; saveTimer.Dispose(); Save(); }
        }
    }
}
