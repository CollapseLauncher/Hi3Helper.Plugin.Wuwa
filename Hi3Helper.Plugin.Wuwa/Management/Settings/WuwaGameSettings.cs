using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hi3Helper.Plugin.Wuwa.Management.Settings;

internal sealed class WuwaGameSettings
{
    private const string GameUserSettingsSection = "/Script/Engine.GameUserSettings";
    private const string LocalKeyPrefix = "local.";

    private static readonly LocalSettingSection[] LocalSettingSections =
    [
        new("Graphics", [
            Toggle("AutoAdjustImageQuality", "Automatically adjust image quality"),
            Level("ImageQuality", "Image quality preset", 4),
            Level("ImageDetail", "Image detail", 4),
            Level("ShadowQuality", "Shadow quality", 4),
            Level("AntiAliasing", "Anti-aliasing", 4),
            Level("AnisoLevel", "Anisotropic filtering", 4),
            Level("MotionBlur", "Motion blur", 3),
            Level("NiagaraQuality", "Effects quality", 4),
            Level("NpcDensity", "NPC density", 4),
            Level("VegetationDensity", "Vegetation density", 4),
            Level("LoadingRangeScaleLevel", "View distance", 4),
            Level("VolumeFog", "Volumetric fog", 4),
            Level("VolumeLight", "Volumetric lighting", 4),
            Toggle("SceneAo", "Ambient occlusion"),
            Toggle("BloomEnable", "Bloom"),
            Toggle("AutoExposure", "Automatic exposure"),
            Toggle("DynamicBones", "Character dynamic bones"),
            Toggle("TeammateFx", "Teammate effects"),
            Toggle("VegetationDither", "Vegetation dithering"),
            Toggle("WaterInteract", "Water interaction"),
            Toggle("FlowAdaptation", "Flow adaptation"),
            Toggle("RayTracing", "Ray tracing"),
            Toggle("NvidiaSuperSamplingEnable", "NVIDIA super sampling"),
            Choice("NvidiaSuperSamplingQuality", "NVIDIA super-sampling preset", [
                new("99", "Automatic"), new("0", "Preset 0"), new("1", "Preset 1"),
                new("2", "Preset 2"), new("3", "Preset 3"), new("4", "Preset 4")
            ]),
            Slider("NvidiaSuperSamplingSharpness", "NVIDIA sharpening", 0, 100),
            Slider("Brightness", "Brightness", -100, 100),
            Slider("ContrastNew", "Contrast", 0, 100),
            Slider("SaturationNew", "Saturation", 0, 100)
        ]),
        new("Audio", [
            Slider("MasterVolume", "Master volume", 0, 100),
            Slider("MusicVolume", "Music volume", 0, 100),
            Slider("SFXVolume", "Sound-effects volume", 0, 100),
            Slider("VoiceVolume", "Voice volume", 0, 100),
            Slider("AMBVolume", "Ambient volume", 0, 100),
            Slider("UIVolume", "UI volume", 0, 100),
            Toggle("BackendVolume", "Play audio in the background")
        ]),
        new("Camera and controls", [
            Toggle("AimAssistEnable", "Aim assist"),
            Toggle("AutoRun", "Auto-run"),
            Toggle("AutoSprint", "Auto-sprint"),
            Toggle("HorizontalViewRevert", "Invert horizontal camera"),
            Toggle("VerticalViewRevert", "Invert vertical camera"),
            Toggle("IsSidestepCameraEnable", "Sidestep camera"),
            Toggle("IsSoftLockCameraEnable", "Soft-lock camera"),
            Toggle("AdjustiveGamePadTrigger", "Adaptive controller triggers"),
            Slider("HorizontalViewSensitivity", "Horizontal camera sensitivity", 0, 100),
            Slider("VerticalViewSensitivity", "Vertical camera sensitivity", 0, 100),
            Slider("AimHorizontalViewSensitivity", "Horizontal aim sensitivity", 0, 100),
            Slider("AimVerticalViewSensitivity", "Vertical aim sensitivity", 0, 100),
            Slider("GamepadLeftStickDeadZone", "Left-stick dead zone", 0, 100),
            Slider("GamepadRightStickDeadZone", "Right-stick dead zone", 0, 100),
            Slider("JoystickShakeStrength", "Controller vibration strength", 0, 100),
            Level("JoystickShakeType", "Controller vibration mode", 3),
            Level("CameraShakeStrength", "Camera shake", 3),
            Level("CommonSpringArmLength", "Exploration camera distance", 3),
            Level("FightSpringArmLength", "Combat camera distance", 3),
            Level("KeyboardLockEnemyMode", "Keyboard enemy-lock mode", 3),
            Level("GamepadLockEnemyMode", "Controller enemy-lock mode", 3),
            Level("SkillLockEnemyMode", "Skill enemy-lock mode", 3)
        ]),
        new("Gameplay and accessibility", [
            Level("EnemyHitDisplayMode", "Enemy hit display", 3),
            Level("FlyControlMode", "Flight control mode", 3),
            Toggle("ShowDamage", "Show damage numbers"),
            Toggle("ShowOtherName", "Show other player names"),
            Toggle("SubTitleOption", "Subtitles"),
            Toggle("PhotoAndShareShowPlayerName", "Show player name in photos"),
            Slider("WalkOrRunRate", "Walk/run transition", 0, 1, 0.05),
            Toggle("EyeProtection", "Eye-protection mode"),
            Level("EyeProtectionMode", "Eye-protection preset", 3),
            Slider("EyeProtectionBrightness", "Eye-protection brightness", 0, 1, 0.05),
            Slider("EyeProtectionStrength", "Eye-protection strength", 0, 1, 0.05),
            Slider("EyeProtectionTexture", "Eye-protection texture", 0, 1, 0.05),
            Slider("EyeProtectionTemp", "Eye-protection color temperature", 1000, 10000, 100)
        ]),
        new("Language", [
            Level("TextLanguage", "Text language", 20),
            Level("VoiceLanguage", "Voice-over language", 20)
        ], "Language values are game-defined numeric identifiers. Change them only if you know the identifier used by your installed client.")
    ];

    private readonly string _gameUserSettingsPath;
    private readonly string? _localStoragePath;
    private readonly Dictionary<string, string> _localValues;
    private readonly HashSet<string> _changedLocalKeys = new(StringComparer.Ordinal);

    private WuwaGameSettings(string gamePath, string? localStoragePath, WuwaIniDocument ini,
                             Dictionary<string, string> localValues)
    {
        _gameUserSettingsPath = Path.Combine(gamePath, "Client", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
        _localStoragePath = localStoragePath;
        _localValues = localValues;
        ResolutionWidth = ParseInt(ini.Get(GameUserSettingsSection, "ResolutionSizeX"), 1920);
        ResolutionHeight = ParseInt(ini.Get(GameUserSettingsSection, "ResolutionSizeY"), 1080);
        WindowMode = Math.Clamp(ParseInt(ini.Get(GameUserSettingsSection, "FullscreenMode"), 1), 0, 2);
        VSync = bool.TryParse(ini.Get(GameUserSettingsSection, "bUseVSync"), out bool vsync) && vsync;
        DynamicResolution = bool.TryParse(ini.Get(GameUserSettingsSection, "bUseDynamicResolution"), out bool dynamicResolution) && dynamicResolution;
        Hdr = bool.TryParse(ini.Get(GameUserSettingsSection, "bUseHDRDisplayOutput"), out bool hdr) && hdr;
        HdrNits = ParseInt(ini.Get(GameUserSettingsSection, "HDRDisplayOutputNits"), 1000);
        FrameRate = ParseDouble(ini.Get(GameUserSettingsSection, "FrameRateLimit"), 60);
    }

    internal int ResolutionWidth { get; private set; }
    internal int ResolutionHeight { get; private set; }
    internal int WindowMode { get; private set; }
    internal bool VSync { get; private set; }
    internal bool DynamicResolution { get; private set; }
    internal bool Hdr { get; private set; }
    internal int HdrNits { get; private set; }
    internal double FrameRate { get; private set; }

    internal static WuwaGameSettings Load(IPluginPresetConfig presetConfig)
    {
        presetConfig.comGet_GameManager(out var gameManager);
        string? gamePath = null;
        gameManager?.GetGamePath(out gamePath);
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            throw new DirectoryNotFoundException("Wuthering Waves is not installed or its game path is unavailable.");

        string iniPath = Path.Combine(gamePath, "Client", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
        string localStorageDirectory = Path.Combine(gamePath, "Client", "Saved", "LocalStorage");
        string? databasePath = Directory.Exists(localStorageDirectory)
            ? Directory.EnumerateFiles(localStorageDirectory, "LocalStorage*.db", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

        Dictionary<string, string> localValues = new(StringComparer.Ordinal);
        if (databasePath != null)
        {
            using WuwaLocalStorage storage = new(databasePath);
            foreach (string key in LocalSettingSections.SelectMany(section => section.Settings).Select(setting => setting.Key).Distinct(StringComparer.Ordinal))
            {
                if (storage.Read(key) is { } value)
                    localValues[key] = value;
            }
        }

        return new WuwaGameSettings(gamePath, databasePath, WuwaIniDocument.Load(iniPath), localValues);
    }

    internal GameSettingsPage CreatePage()
    {
        List<GameSettingsSection> sections =
        [
            new("⚠ Ban-risk warning", [])
            {
                Description = "Editing Wuthering Waves files is unofficial and may carry a risk of account suspension or a ban. " +
                              "There are no known reports of bans from these settings at this time, but safety cannot be guaranteed. " +
                              "Continue only if you accept that risk. This warning is shown every time you open this page."
            },
            new("Display", [
                GameSettingEntry.Number("display.width", "Resolution width", ResolutionWidth, 640, 16384, 1),
                GameSettingEntry.Number("display.height", "Resolution height", ResolutionHeight, 480, 16384, 1),
                GameSettingEntry.Choice("display.mode", "Window mode", WindowMode.ToString(CultureInfo.InvariantCulture), [
                    new("0", "Fullscreen"), new("1", "Borderless"), new("2", "Windowed")
                ]),
                GameSettingEntry.Toggle("display.vsync", "VSync", VSync),
                GameSettingEntry.Toggle("display.dynamicResolution", "Dynamic resolution", DynamicResolution),
                GameSettingEntry.Toggle("display.hdr", "HDR output", Hdr),
                GameSettingEntry.Number("display.hdrNits", "HDR peak brightness", HdrNits, 400, 10000, 50),
                GameSettingEntry.Number("display.frameRate", "Frame-rate limit", FrameRate, 30, 360, 1,
                    "Values not offered by the game may be unstable and can increase the account risk described above.")
            ])
        ];

        foreach (LocalSettingSection localSection in LocalSettingSections)
        {
            List<GameSettingEntry> entries = [];
            foreach (LocalSetting setting in localSection.Settings)
            {
                if (_localValues.TryGetValue(setting.Key, out string? value))
                    entries.Add(CreateLocalEntry(setting, value));
            }

            if (entries.Count > 0)
                sections.Add(new GameSettingsSection(localSection.Title, entries) { Description = localSection.Description });
        }

        return new GameSettingsPage(sections) { Title = "Wuthering Waves Game Settings" };
    }

    internal void SetValue(string key, string value)
    {
        switch (key)
        {
            case "display.width": ResolutionWidth = ParseRangeInt(value, 640, 16384, key); return;
            case "display.height": ResolutionHeight = ParseRangeInt(value, 480, 16384, key); return;
            case "display.mode": WindowMode = ParseRangeInt(value, 0, 2, key); return;
            case "display.vsync": VSync = bool.Parse(value); return;
            case "display.dynamicResolution": DynamicResolution = bool.Parse(value); return;
            case "display.hdr": Hdr = bool.Parse(value); return;
            case "display.hdrNits": HdrNits = ParseRangeInt(value, 400, 10000, key); return;
            case "display.frameRate": FrameRate = ParseRangeDouble(value, 30, 360, key); return;
        }

        if (!key.StartsWith(LocalKeyPrefix, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown Wuthering Waves setting.");

        string localKey = key[LocalKeyPrefix.Length..];
        LocalSetting setting = FindLocalSetting(localKey);
        if (!_localValues.ContainsKey(localKey))
            throw new ArgumentOutOfRangeException(nameof(key), key, "This setting is not available in the installed game configuration.");

        string storedValue = setting.Kind switch
        {
            LocalSettingKind.Toggle => bool.Parse(value) ? "1" : "0",
            LocalSettingKind.Number => ParseRangeDouble(value, setting.Minimum, setting.Maximum, key)
                .ToString("0.########", CultureInfo.InvariantCulture),
            LocalSettingKind.Choice when setting.Choices?.Any(choice => choice.Value == value) == true => value,
            LocalSettingKind.Choice => throw new ArgumentOutOfRangeException(nameof(value), value, $"Unsupported value for {localKey}."),
            _ => throw new ArgumentOutOfRangeException(nameof(setting.Kind))
        };

        _localValues[localKey] = storedValue;
        _changedLocalKeys.Add(localKey);
    }

    internal void Apply()
    {
        if (Process.GetProcessesByName("Client-Win64-Shipping").Length != 0)
            throw new InvalidOperationException("Close Wuthering Waves before applying game settings.");

        Backup(_gameUserSettingsPath);
        WuwaIniDocument ini = WuwaIniDocument.Load(_gameUserSettingsPath);
        ini.Set(GameUserSettingsSection, "ResolutionSizeX", ResolutionWidth.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "ResolutionSizeY", ResolutionHeight.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "LastUserConfirmedResolutionSizeX", ResolutionWidth.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "LastUserConfirmedResolutionSizeY", ResolutionHeight.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "FullscreenMode", WindowMode.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "LastConfirmedFullscreenMode", WindowMode.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "PreferredFullscreenMode", WindowMode.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "bUseVSync", VSync ? bool.TrueString : bool.FalseString);
        ini.Set(GameUserSettingsSection, "bUseDynamicResolution", DynamicResolution ? bool.TrueString : bool.FalseString);
        ini.Set(GameUserSettingsSection, "bUseHDRDisplayOutput", Hdr ? bool.TrueString : bool.FalseString);
        ini.Set(GameUserSettingsSection, "HDRDisplayOutputNits", HdrNits.ToString(CultureInfo.InvariantCulture));
        ini.Set(GameUserSettingsSection, "FrameRateLimit", FrameRate.ToString("0.000000", CultureInfo.InvariantCulture));
        ini.Save(_gameUserSettingsPath);

        if (_localStoragePath == null) return;

        string? frameRatePreset = GetNativeFrameRatePreset(FrameRate);
        if (_changedLocalKeys.Count == 0 && frameRatePreset == null) return;

        Backup(_localStoragePath);
        using WuwaLocalStorage storage = new(_localStoragePath);
        foreach (string key in _changedLocalKeys)
            storage.Write(key, _localValues[key]);

        if (frameRatePreset != null)
            storage.Write("CustomFrameRate", frameRatePreset);
    }

    private static GameSettingEntry CreateLocalEntry(LocalSetting setting, string value)
    {
        string pageKey = LocalKeyPrefix + setting.Key;
        return setting.Kind switch
        {
            LocalSettingKind.Toggle => GameSettingEntry.Toggle(pageKey, setting.Title, value == "1" || bool.TryParse(value, out bool enabled) && enabled, setting.Description),
            LocalSettingKind.Number => GameSettingEntry.Slider(pageKey, setting.Title, ParseDouble(value, setting.Minimum), setting.Minimum, setting.Maximum, setting.Step, setting.Description),
            LocalSettingKind.Choice => GameSettingEntry.Choice(pageKey, setting.Title, value, AddUnknownChoice(setting.Choices!, value), setting.Description),
            _ => throw new ArgumentOutOfRangeException(nameof(setting.Kind))
        };
    }

    private static IReadOnlyList<GameSettingChoice> AddUnknownChoice(IReadOnlyList<GameSettingChoice> choices, string value) =>
        choices.Any(choice => choice.Value == value) ? choices : [.. choices, new(value, $"Unknown ({value})")];

    private static LocalSetting FindLocalSetting(string key) =>
        LocalSettingSections.SelectMany(section => section.Settings).FirstOrDefault(setting => setting.Key == key)
        ?? throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown Wuthering Waves setting.");

    private static LocalSetting Toggle(string key, string title, string? description = null) =>
        new(key, title, LocalSettingKind.Toggle, 0, 1, 1, null, description);

    private static LocalSetting Slider(string key, string title, double minimum, double maximum, double step = 1,
                                       string? description = null) =>
        new(key, title, LocalSettingKind.Number, minimum, maximum, step, null, description);

    private static LocalSetting Level(string key, string title, int maximum, string? description = null) =>
        Choice(key, title, [.. Enumerable.Range(0, maximum + 1).Select(level => new GameSettingChoice(level.ToString(CultureInfo.InvariantCulture), $"Level {level}"))], description);

    private static LocalSetting Choice(string key, string title, IReadOnlyList<GameSettingChoice> choices,
                                       string? description = null) =>
        new(key, title, LocalSettingKind.Choice, 0, 0, 1, choices, description);

    private static string? GetNativeFrameRatePreset(double frameRate) =>
        frameRate switch
        {
            30 => "0",
            45 => "1",
            60 => "2",
            120 => "3",
            _ => null
        };

    private static void Backup(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".collapse.bak", true);
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;

    private static int ParseRangeInt(string value, int minimum, int maximum, string key)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentOutOfRangeException(key, value, $"Value must be between {minimum} and {maximum}.");
        return parsed;
    }

    private static double ParseRangeDouble(string value, double minimum, double maximum, string key)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentOutOfRangeException(key, value, $"Value must be between {minimum} and {maximum}.");
        return parsed;
    }

    private enum LocalSettingKind
    {
        Toggle,
        Number,
        Choice
    }

    private sealed record LocalSetting(string Key, string Title, LocalSettingKind Kind,
                                       double Minimum, double Maximum, double Step,
                                       IReadOnlyList<GameSettingChoice>? Choices, string? Description);

    private sealed record LocalSettingSection(string Title, IReadOnlyList<LocalSetting> Settings,
                                              string? Description = null);
}
