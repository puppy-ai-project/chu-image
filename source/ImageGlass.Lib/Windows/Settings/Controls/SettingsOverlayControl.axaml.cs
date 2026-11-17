/*
ImageGlass - A Fast, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageGlass.Common.AppThemes;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.ServiceProviders.Update;
using ImageGlass.Common.Types;
using ImageGlass.UI.Windowing;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImageGlass.Common.Windows;

/// <summary>
/// 关联格式列表单行视图模型。
/// </summary>
public sealed class SettingsFormatRowModel
{
    public string IndexText { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string DecoderText { get; init; } = "–";
    public string EncoderText { get; init; } = "–";
    public IBrush DecoderForeground { get; init; } = Brushes.Gray;
    public IBrush EncoderForeground { get; init; } = Brushes.Gray;
    public bool IsCustom { get; init; }
    public bool IsPlugin { get; init; }
    public string BadgeText { get; init; } = string.Empty;
    public bool CanDelete { get; init; }
    public bool IsReadOnlyIndicatorVisible { get; init; }
    public string DeleteTooltip { get; init; } = string.Empty;
    public string ReadOnlyTooltip { get; init; } = string.Empty;
    public bool IsHighlighted { get; init; }
}


/// <summary>
/// 主窗口内部双栏现代设置遮盖层弹窗（当前包含「主题」与「关联格式」，方便未来继续扩展分页）。
/// </summary>
public partial class SettingsOverlayControl : UserControl
{
    private const string THEMES_URL = "https://image.withqq.com";
    private const string THEME_PACKAGE_PATTERN = "*.igtheme.zip";
    private const int PREVIEW_DECODE_WIDTH = 340;
    private const double CARD_PREVIEW_W = 112;
    private const double CARD_PREVIEW_H = 68;
    private const double THEME_ACTION_COL_GAP = 28;

    private static IBrush ThemeBrush(string dark, string light) => Brush.Parse(Core.Theme.Settings.IsDarkMode ? dark : light);
    private static IBrush _textPrimaryBrush => ThemeBrush("#D1D5DB", "#3F4C60");
    private static IBrush _textSecondaryBrush => ThemeBrush("#9CA3AF", "#59687B");
    private static IBrush _textPluginBrush => ThemeBrush("#C4B5FD", "#6D42B5");
    private static IBrush _textMutedBrush => ThemeBrush("#525866", "#738195");
    private static IBrush _hintNormalBrush => ThemeBrush("#8B92A0", "#64748B");
    private static IBrush _hintErrorBrush => ThemeBrush("#F87171", "#BE3346");
    private static IBrush _countNormalDotBrush => ThemeBrush("#60A5FA", "#2563B7");
    private static readonly IBrush _countDirtyDotBrush = Brush.Parse("#FBBF24");

    // 当前选中的导航标签 ("theme" | "associations")
    private string _activeSection = "theme";
    private Action<string?>? _previewBgCallback;
    private bool _isSaving;
    private string _initialAppearanceMode = "System";
    private string _appearanceMode = "System";

    // ---------------------------------------------------------
    // 主题与外观页工作副本状态
    // ---------------------------------------------------------
    private string _initialDarkTheme = Const.DEFAULT_THEME;
    private string _darkTheme = Const.DEFAULT_THEME;
    private string _initialLightTheme = Const.DEFAULT_LIGHT_THEME;
    private string _lightTheme = Const.DEFAULT_LIGHT_THEME;
    private string _initialBgColor = "#00000000";
    private string _bgColor = "#00000000";
    private BackdropStyle _initialBackdrop = BackdropStyle.Mica;
    private BackdropStyle _backdrop = BackdropStyle.Mica;
    private bool _isThemeLocked;
    private bool _isBgColorLocked;
    private bool _isBackdropLocked;
    private bool _suppressBgTextEvent;

    private List<IgTheme> _themes = [];
    private readonly List<ThemeCardItem> _themeCards = [];
    private int _themeListGen;
    private Bitmap? _largePreviewBmp;
    private string _themeFilter = "all";
    private string _themeSearchQuery = string.Empty;
    private readonly HashSet<string> _themeResourceOverrides = [];

    // ---------------------------------------------------------
    // 关联格式页工作副本状态
    // ---------------------------------------------------------
    private readonly HashSet<string> _initialExts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _defaultSet = new(Config.DefaultFileFormats, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pluginExts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pluginEncodeExts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CodecInfo> _codecs = [];
    private readonly Dictionary<string, CodecInfo?> _decoderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CodecInfo?> _encoderCache = new(StringComparer.OrdinalIgnoreCase);

    private string _filterMode = "all"; // all | custom | plugin
    private string _searchQuery = string.Empty;
    private string? _highlightedExt;
    private bool _isFormatsLocked;
    private int _highlightVersion;

    private static readonly FontFamily _codeFont = new(Const.FONT_CODE);

    private static bool IsZh =>
        Core.Lang.FilePath.Contains("Chinese", StringComparison.OrdinalIgnoreCase);

    private static string T(string zh, string en) => IsZh ? zh : en;


    public SettingsOverlayControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateSettingsCardHeight();
        ActualThemeVariantChanged += (_, _) =>
        {
            if (IsVisible) UpdateAppearanceMode();
        };
    }


    /// <summary>
    /// 打开内部设置弹窗并载入当前配置快照。
    /// </summary>
    /// <param name="section">目标分页（"theme" / "associations" / SettingsNavId 名，可空）。</param>
    /// <param name="previewBgCallback">查看器画布背景色实时预览回调。</param>
    public void Open(string? section = null, Action<string?>? previewBgCallback = null)
    {
        Core.ThemeChanged -= SettingsThemeChanged;
        Core.ThemeChanged += SettingsThemeChanged;
        ApplySettingsThemeResources();
        UpdateSettingsCardHeight();
        _isSaving = false;
        _previewBgCallback = previewBgCallback;

        // 1. 解析初始导航标签
        _activeSection = ResolveSection(section);
        _initialAppearanceMode = Core.Config.AppearanceMode;
        _appearanceMode = _initialAppearanceMode;
        UpdateAppearanceMode();

        // 2. 快照主题与外观配置
        _initialDarkTheme = Core.Config.Get(ConfigId.DarkTheme, Const.DEFAULT_THEME);
        _darkTheme = _initialDarkTheme;
        _initialLightTheme = Core.Config.Get(ConfigId.LightTheme, Const.DEFAULT_LIGHT_THEME);
        _lightTheme = _initialLightTheme;
        _initialBgColor = Core.Config.Get(ConfigId.BackgroundColor, "#00000000");
        _bgColor = _initialBgColor;
        _initialBackdrop = Core.Config.Get(ConfigId.WindowBackdrop, BackdropStyle.Mica);
        _backdrop = _initialBackdrop;

        _isThemeLocked = Config.IsConfigLocked(ConfigId.DarkTheme) || Config.IsConfigLocked(ConfigId.LightTheme);
        _isBgColorLocked = Config.IsConfigLocked(ConfigId.BackgroundColor);
        _isBackdropLocked = Config.IsConfigLocked(ConfigId.WindowBackdrop);

        // 3. 快照关联格式配置
        _highlightedExt = null;
        _filterMode = "all";
        _searchQuery = string.Empty;
        _initialExts.Clear();
        _exts.Clear();
        var storedExts = Core.Config.Get(ConfigId.FileFormats,
            new HashSet<string>(Config.DefaultFileFormats, StringComparer.OrdinalIgnoreCase));
        foreach (var ext in storedExts)
        {
            _initialExts.Add(ext);
            _exts.Add(ext);
        }
        _isFormatsLocked = Config.IsConfigLocked(ConfigId.FileFormats);

        // 4. 应用本地化文案、平台集成、渲染初始界面
        CloseLargeThemePreview();
        SnapshotCodecs();
        ApplyLocalizedTexts();
        ConfigureThemeAppearanceControls();
        ConfigureAssocPlatformIntegration();
        UpdateAdminLockStates();

        PART_SearchBox.Text = string.Empty;
        PART_BtnClearSearch.IsVisible = false;
        HideAddBar(clearText: true);
        UpdateFilterPillStyles();
        RebuildFormatsList();
        SwitchSection(_activeSection);
        _ = ReloadThemePacksAsync();

        IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (_activeSection == "associations")
            {
                PART_SearchBox.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void UpdateSettingsCardHeight()
    {
        PART_SettingsCard.MaxHeight = Bounds.Height > 0 ? Math.Min(600, Math.Max(0, Bounds.Height - 36)) : 600;
    }


    /// <summary>
    /// 响应 Escape 键：先关闭大图预览或内联添加栏，否则取消预览并关闭整个弹窗。
    /// </summary>
    public void HandleEscapeKey()
    {
        if (PART_ThemePreviewOverlay.IsVisible)
        {
            CloseLargeThemePreview();
            return;
        }

        if (_activeSection == "associations" && PART_AddBar.IsVisible)
        {
            HideAddBar(clearText: true);
            PART_SearchBox.Focus();
            return;
        }

        CancelAndClose();
    }


    /// <summary>
    /// 直接关闭弹窗并回滚尚未保存的实时预览改动。
    /// </summary>
    public void Close() => CancelAndClose();


    private void CancelAndClose()
    {
        HideOverlayAndDisposePreviews();
    }


    private void HideOverlayAndDisposePreviews()
    {
        Core.ThemeChanged -= SettingsThemeChanged;
        PART_AboutPage.Close();
        _previewBgCallback?.Invoke(null);
        CloseLargeThemePreview();
        HideAddBar(clearText: true);
        DisposeThemeCardBitmaps();
        IsVisible = false;
    }

    private void SettingsThemeChanged(object? sender, ThemePackChangedEventArgs e)
    {
        ApplySettingsThemeResources();
        if (IsVisible) RebuildThemeGridCards();
    }

    private void ApplySettingsThemeResources()
    {
        foreach (var key in _themeResourceOverrides) Resources.Remove(key);
        _themeResourceOverrides.Clear();

        // Keep the existing default palettes intact; named palettes derive their settings surfaces
        // and text from the same colors as the main window.
        if (Core.Theme.FolderName is Const.DEFAULT_THEME or Const.DEFAULT_LIGHT_THEME) return;

        var bg = BHelper.ColorFromHex(Core.Theme.Colors.BgColor);
        var surface = BHelper.ColorFromHex(Core.Theme.Colors.MenuBgColor);
        var text = BHelper.ColorFromHex(Core.Theme.Colors.TextColor);
        if (bg.A == 0 || surface.A == 0 || text.A == 0) return;
        var accent = Core.AccentColor;

        Color Mix(Color a, Color b, double amount) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * amount),
            (byte)(a.G + (b.G - a.G) * amount),
            (byte)(a.B + (b.B - a.B) * amount));

        void SetPalette(Color color, params string[] suffixes)
        {
            foreach (var suffix in suffixes)
            {
                var colorKey = "IgDarkColor" + suffix;
                var brushKey = "IgDarkBrush" + suffix;
                Resources[colorKey] = color;
                Resources[brushKey] = new SolidColorBrush(color);
                _themeResourceOverrides.Add(colorKey);
                _themeResourceOverrides.Add(brushKey);
            }
        }

        SetPalette(surface, "292C33", "23252B", "2C2E34", "242730", "22252C", "20232A");
        SetPalette(bg, "1B1D22", "191B20", "15171C", "181A20", "1D1F24");
        SetPalette(text, "F3F4F6", "FFFFFF", "F9FAFB", "E5E7EB", "D1D5DB");
        SetPalette(Mix(surface, text, 0.68), "9CA3AF", "8B92A0", "8E95A2", "7A8291", "747B89", "6F7685", "6F7785", "6B7280", "7C8391", "636B78", "848C9C");
        SetPalette(accent, "60A5FA", "93C5FD", "3B82F6", "356BBF", "2B59A1", "407AD2", "3368B8");
    }


    private static string ResolveSection(string? section)
    {
        if (string.Equals(section, "about", StringComparison.OrdinalIgnoreCase)) return "about";
        if (string.IsNullOrWhiteSpace(section)) return "theme";

        if (section.Contains("Assoc", StringComparison.OrdinalIgnoreCase)
            || section.Contains("Format", StringComparison.OrdinalIgnoreCase))
        {
            return "associations";
        }

        return "theme";
    }


    #region Navigation & Shared Texts

    private void SwitchSection(string section)
    {
        _activeSection = section is "theme" or "about" ? section : "associations";
        var isTheme = _activeSection == "theme";
        var isAbout = _activeSection == "about";

        PART_NavTheme.Classes.Set("active", isTheme);
        PART_NavAssociations.Classes.Set("active", !isTheme && !isAbout);
        PART_NavAbout.Classes.Set("active", isAbout);

        PART_PageTheme.IsVisible = isTheme;
        PART_PageAssociations.IsVisible = !isTheme && !isAbout;
        if (isAbout) PART_AboutPage.Open(); else PART_AboutPage.Close();

        PART_ThemeHeaderActions.IsVisible = isTheme;
        PART_AssocHeaderBadge.IsVisible = !isTheme && !isAbout;

        if (isAbout)
        {
            PART_SectionTitle.Text = Core.Lang[LangId.Menu_MnuAbout];
            PART_SectionSubtitle.Text = string.Empty;
        }
        else if (isTheme)
        {
            PART_SectionTitle.Text = Core.Lang[LangId.Settings_Theme];
            PART_SectionSubtitle.Text = T(
                "选择主题与调色板，即刻应用到全局工作台",
                "Select theme and palette to apply live across the workbench");
            _ = ReloadThemePacksAsync();
        }
        else
        {
            PART_SectionTitle.Text = Core.Lang[LangId.Settings_Nav_FileTypeAssociations];
            PART_SectionSubtitle.Text = T(
                "管理图像查看器可打开、浏览的文件扩展名及编解码器映射",
                "Manage image file extensions and decoder/encoder mappings supported by the viewer");
        }

        UpdateFooterState();
    }


    private void ApplyLocalizedTexts()
    {
        PART_SidebarTitle.Text = Core.Lang[LangId.Menu_MnuSettings];
        PART_SidebarGroupLabel.Text = T("偏好配置", "PREFERENCES");
        PART_TxtNavTheme.Text = Core.Lang[LangId.Settings_Theme];
        PART_TxtNavAssociations.Text = Core.Lang[LangId.Settings_Nav_FileTypeAssociations];
        PART_TxtNavAbout.Text = Core.Lang[LangId.Menu_MnuAbout];
        PART_SidebarTip.Text = T("按 Esc 关闭", "Press Esc to close");

        // 主题页文案
        PART_TxtInstallTheme.Text = Core.Lang[LangId.Settings_Theme_InstallTheme];
        ToolTip.SetTip(PART_BtnRefreshThemes, Core.Lang[LangId.Settings_Refresh]);
        ToolTip.SetTip(PART_BtnOpenThemeFolder, Core.Lang[LangId.Settings_Theme_OpenThemeFolder]);
        PART_BtnGetMoreThemes.Content = Core.Lang[LangId.Settings_Theme_GetMoreThemes];

        PART_ThemeLockWarningText.Text = Core.Lang[LangId.Settings_AdminLockWarning];
        PART_LblDarkSlotTitle.Text = T("暗色槽: ", "Dark slot: ");
        PART_LblLightSlotTitle.Text = T("亮色槽: ", "Light slot: ");

        PART_LblBgColorTitle.Text = Core.Lang[LangId._BackgroundColor];
        PART_LblBgColorDesc.Text = T(
            "点选预设色块或输入 HEX 颜色实时预览画布背景",
            "Pick a preset swatch or enter a HEX value for live preview");
        PART_BtnBgReset.Content = T("恢复工作台默认", "Reset default");

        ToolTip.SetTip(PART_SwatchDefault, T("工作台默认渐变", "Workbench default"));
        ToolTip.SetTip(PART_SwatchBlack, T("暗夜纯黑 (#0B0D10)", "Pure black (#0B0D10)"));
        ToolTip.SetTip(PART_SwatchSlate, T("石板暗灰 (#1E2026)", "Studio slate (#1E2026)"));
        ToolTip.SetTip(PART_SwatchNeutral, T("摄影中性灰 (#4A4F59)", "Neutral photo gray (#4A4F59)"));
        ToolTip.SetTip(PART_SwatchLightGray, T("柔和浅灰 (#E5E7EB)", "Soft light gray (#E5E7EB)"));
        ToolTip.SetTip(PART_SwatchWhite, T("纯净亮白 (#FFFFFF)", "Pure white (#FFFFFF)"));

        PART_LblBackdropTitle.Text = Core.Lang[LangId.Settings_WindowBackdrop];
        PART_LblThemePacksTitle.Text = Core.Lang[LangId.Settings_Theme];
        PART_ThemeEmptyText.Text = Core.Lang[LangId._Empty];
        PART_PreviewThemeTip.Text = T("点击任意位置关闭预览", "Click anywhere to dismiss");

        // 关联格式页文案
        PART_LockWarningText.Text = Core.Lang[LangId.Settings_AdminLockWarning];
        PART_SearchBox.PlaceholderText = T(
            "搜索扩展名或编解码器 (如 .webp, raw)...",
            Core.Lang[LangId._TypeToFilter]);

        PART_TxtAddFormat.Text = T("添加格式", Core.Lang[LangId._Add]);
        PART_AddInput.PlaceholderText = T("例如 avif 或 jxl; heic", "e.g. avif or jxl; heic");
        PART_BtnConfirmAdd.Content = T("加入", Core.Lang[LangId._Add]);
        PART_BtnCancelAdd.Content = Core.Lang[LangId._Cancel];
        ResetAddHint();

        PART_ColExtHeader.Text = Core.Lang[LangId._FileExtension];
        PART_ColDecoderHeader.Text = Core.Lang[LangId._Decoder];
        PART_ColEncoderHeader.Text = Core.Lang[LangId._Encoder];
        PART_ColActionHeader.Text = T("操作", "Action");
        PART_EmptyTitle.Text = T("未找到匹配的格式", "No matching formats found");
        PART_EmptyDesc.Text = T("请尝试更换筛选关键词或点击右上角「添加格式」", "Try another filter keyword or add a new format above");

        // 通用底栏按钮
        PART_TxtResetDefaults.Text = Core.Lang[LangId._ResetToDefault];
        PART_BtnCancel.Content = Core.Lang[LangId._Cancel];
        PART_BtnSave.Content = Core.Lang[LangId._Save];
    }


    private void UpdateAdminLockStates()
    {
        // 主题页锁
        var anyThemeLock = _isThemeLocked || _isBgColorLocked || _isBackdropLocked;
        PART_ThemeLockWarning.IsVisible = anyThemeLock;
        PART_BgHexInput.IsEnabled = !_isBgColorLocked;
        PART_BtnBgReset.IsEnabled = !_isBgColorLocked;
        PART_SwatchDefault.IsEnabled = !_isBgColorLocked;
        PART_SwatchBlack.IsEnabled = !_isBgColorLocked;
        PART_SwatchSlate.IsEnabled = !_isBgColorLocked;
        PART_SwatchNeutral.IsEnabled = !_isBgColorLocked;
        PART_SwatchLightGray.IsEnabled = !_isBgColorLocked;
        PART_SwatchWhite.IsEnabled = !_isBgColorLocked;
        PART_BackdropGroup.IsEnabled = !_isBackdropLocked;

        // 关联格式页锁
        PART_LockWarning.IsVisible = _isFormatsLocked;
        PART_BtnToggleAdd.IsEnabled = !_isFormatsLocked;

        UpdateFooterState();
    }


    private bool IsThemeDirty()
        => _appearanceMode != _initialAppearanceMode
        || !string.Equals(_darkTheme, _initialDarkTheme, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(_lightTheme, _initialLightTheme, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(NormalizeBgForCompare(_bgColor), NormalizeBgForCompare(_initialBgColor), StringComparison.OrdinalIgnoreCase)
        || _backdrop != _initialBackdrop;

    private bool IsAssocDirty() => !_exts.SetEquals(_initialExts);


    private void UpdateFooterState()
    {
        var themeDirty = IsThemeDirty();
        var assocDirty = IsAssocDirty();

        PART_NavThemeDirtyDot.IsVisible = themeDirty;
        PART_NavAssocDirtyDot.IsVisible = assocDirty;
        PART_AssocCountDot.Fill = assocDirty ? _countDirtyDotBrush : _countNormalDotBrush;

        var anyDirty = themeDirty || assocDirty;
        PART_DirtyBanner.IsVisible = anyDirty;

        if (anyDirty)
        {
            var dirtyParts = new List<string>(2);
            if (themeDirty) dirtyParts.Add(Core.Lang[LangId.Settings_Theme]);
            if (assocDirty) dirtyParts.Add(Core.Lang[LangId.Settings_Nav_FileTypeAssociations]);
            PART_DirtyText.Text = T(
                $"未保存改动: {string.Join("、", dirtyParts)}",
                $"Unsaved: {string.Join(", ", dirtyParts)}");
        }

        // 重置默认按钮随当前页面锁定状态控制
        PART_BtnResetDefaults.IsEnabled = _activeSection == "theme"
            ? !(_isThemeLocked && _isBgColorLocked && _isBackdropLocked)
            : !_isFormatsLocked;
    }

    #endregion // Navigation & Shared Texts


    #region Theme & Appearance Section

    private void ConfigureThemeAppearanceControls()
    {
        // Windows 11 专属窗口材质
        var isWin11 = BHelper.OS == OSType.Windows && !BHelper.IsWindows10;
        PART_BackdropGroup.IsVisible = isWin11;
        if (isWin11) UpdateBackdropPills();

        // 同步背景色框与色块高亮
        SyncBgColorUI(_bgColor);
        UpdateThemeSlotOverview();
    }


    private void UpdateBackdropPills()
    {
        PART_BackdropMica.Classes.Set("active", _backdrop == BackdropStyle.Mica);
        PART_BackdropMicaAlt.Classes.Set("active", _backdrop == BackdropStyle.MicaAlt);
        PART_BackdropAcrylic.Classes.Set("active", _backdrop == BackdropStyle.Acrylic);
        PART_BackdropNone.Classes.Set("active", _backdrop == BackdropStyle.None);
    }


    private void SyncBgColorUI(string hexText)
    {
        _suppressBgTextEvent = true;
        PART_BgHexInput.Text = hexText;
        PART_BgHexBox.Classes.Set("invalid", false);
        _suppressBgTextEvent = false;

        var normalized = NormalizeBgForCompare(hexText);
        var color = BHelper.ColorFromHex(hexText);

        // 工作台默认透明底色展示渐变斜纹示意色
        if (color.A == 0 || string.Equals(normalized, "#AAFFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            PART_BgPreviewDot.Background = Brush.Parse("#262932");
        }
        else
        {
            PART_BgPreviewDot.Background = new SolidColorBrush(color);
        }

        // 更新预设色块高亮
        var isDefault = color.A == 0 || string.Equals(normalized, "#AAFFFFFF", StringComparison.OrdinalIgnoreCase);
        PART_SwatchDefault.Classes.Set("selected", isDefault);
        PART_SwatchBlack.Classes.Set("selected", !isDefault && normalized == "#FF0B0D10");
        PART_SwatchSlate.Classes.Set("selected", !isDefault && normalized == "#FF1E2026");
        PART_SwatchNeutral.Classes.Set("selected", !isDefault && normalized == "#FF4A4F59");
        PART_SwatchLightGray.Classes.Set("selected", !isDefault && normalized == "#FFE5E7EB");
        PART_SwatchWhite.Classes.Set("selected", !isDefault && normalized == "#FFFFFFFF");
    }


    private static string NormalizeBgForCompare(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "#00000000";
        var c = BHelper.ColorFromHex(hex.Trim());
        return c.ToHex(skipAlpha: false).ToUpperInvariant();
    }


    private static bool TryParseHexColorStrict(string raw, out Color parsed)
    {
        parsed = default;
        var s = raw.Trim();
        if (!s.StartsWith('#')) return false;
        var digits = s[1..];
        if (digits.Length is not (3 or 4 or 6 or 8)) return false;

        foreach (var ch in digits)
        {
            var isHex = (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F');
            if (!isHex) return false;
        }

        parsed = BHelper.ColorFromHex(s, skipAlpha: false);
        return true;
    }


    private void UpdateThemeSlotOverview()
    {
        PART_DarkSlotValue.Text = GetThemeDisplayName(_darkTheme);
        PART_LightSlotValue.Text = GetThemeDisplayName(_lightTheme);
    }


    private string GetThemeDisplayName(string folderName)
    {
        var theme = _themes.FirstOrDefault(t =>
            t.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (theme is null) return folderName;

        return string.IsNullOrWhiteSpace(theme.Info.Name) ? theme.FolderName : theme.Info.Name;
    }


    private sealed record ThemeVisualData(
        string[] Palette,
        string BgHex,
        string HeaderBgHex,
        string KeywordColor,
        string FuncColor,
        string StringColor,
        string CommentColor,
        string NumberColor,
        string TextColor);

    private static ThemeVisualData GetThemePaletteData(IgTheme theme)
    {
        var folder = theme.FolderName;
        if (string.Equals(folder, "Tokyo-Night", StringComparison.OrdinalIgnoreCase))
        {
            return new ThemeVisualData(
                Palette: ["#7AA2F7", "#BB9AF7", "#7DCFFF", "#9ECE6A", "#E0AF68", "#F7768E"],
                BgHex: "#1A1B26",
                HeaderBgHex: "#1F2335",
                KeywordColor: "#BB9AF7",
                FuncColor: "#7AA2F7",
                StringColor: "#9ECE6A",
                CommentColor: "#565F89",
                NumberColor: "#FF9E64",
                TextColor: "#C0CAF5");
        }
        if (string.Equals(folder, "Fox", StringComparison.OrdinalIgnoreCase))
        {
            return new ThemeVisualData(
                Palette: ["#F6A878", "#719CD6", "#81B29A", "#DBC074", "#C94F6D", "#9D79D6"],
                BgHex: "#192330",
                HeaderBgHex: "#212E3F",
                KeywordColor: "#9D79D6",
                FuncColor: "#719CD6",
                StringColor: "#81B29A",
                CommentColor: "#71839B",
                NumberColor: "#F6A878",
                TextColor: "#CDCECF");
        }
        if (string.Equals(folder, "GitHub-Dimmed", StringComparison.OrdinalIgnoreCase))
        {
            return new ThemeVisualData(
                Palette: ["#539BF5", "#57AB5A", "#C69026", "#E5534B", "#B083F0", "#ADBAC7"],
                BgHex: "#22272E",
                HeaderBgHex: "#2D333B",
                KeywordColor: "#F47067",
                FuncColor: "#DCBDFB",
                StringColor: "#96D0FF",
                CommentColor: "#768390",
                NumberColor: "#6CB6FF",
                TextColor: "#ADBAC7");
        }
        if (string.Equals(folder, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            return new ThemeVisualData(
                Palette: ["#0969DA", "#2DA44E", "#BF8700", "#CF222E", "#8250DF", "#24292F"],
                BgHex: "#FFFFFF",
                HeaderBgHex: "#F6F8FA",
                KeywordColor: "#CF222E",
                FuncColor: "#8250DF",
                StringColor: "#0A3069",
                CommentColor: "#6E7781",
                NumberColor: "#0550AE",
                TextColor: "#24292F");
        }
        if (string.Equals(folder, "Default-Dark", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folder, "Kobe", StringComparison.OrdinalIgnoreCase))
        {
            return new ThemeVisualData(
                Palette: ["#3B82F6", "#38BDF8", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6"],
                BgHex: "#1D1F24",
                HeaderBgHex: "#23252A",
                KeywordColor: "#8B5CF6",
                FuncColor: "#3B82F6",
                StringColor: "#10B981",
                CommentColor: "#6B7280",
                NumberColor: "#F59E0B",
                TextColor: "#E5E7EB");
        }
        if (string.Equals(folder, "Default-Light", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folder, "Kobe-Light", StringComparison.OrdinalIgnoreCase))
        {
            return new ThemeVisualData(
                Palette: ["#2563EB", "#0284C7", "#059669", "#D97706", "#DC2626", "#7C3AED"],
                BgHex: "#F8FAFC",
                HeaderBgHex: "#F1F5F9",
                KeywordColor: "#7C3AED",
                FuncColor: "#2563EB",
                StringColor: "#059669",
                CommentColor: "#64748B",
                NumberColor: "#D97706",
                TextColor: "#0F172A");
        }

        var isDark = theme.Settings.IsDarkMode;
        var bg = !string.IsNullOrWhiteSpace(theme.Colors.BgColor) && theme.Colors.BgColor != "#00000000" && !theme.Colors.BgColor.StartsWith("#00")
            ? theme.Colors.BgColor : (isDark ? "#1E2026" : "#FFFFFF");
        var accent = !string.IsNullOrWhiteSpace(theme.Colors.AccentColor)
            ? theme.Colors.AccentColor : (isDark ? "#60A5FA" : "#2563EB");
        var txt = !string.IsNullOrWhiteSpace(theme.Colors.TextColor)
            ? theme.Colors.TextColor : (isDark ? "#E5E7EB" : "#1F2937");

        return new ThemeVisualData(
            Palette: isDark
                ? [accent, "#38BDF8", "#34D399", "#FBBF24", "#F87171", "#A78BFA"]
                : [accent, "#0284C7", "#059669", "#D97706", "#DC2626", "#7C3AED"],
            BgHex: bg,
            HeaderBgHex: isDark ? "#282C35" : "#F3F4F6",
            KeywordColor: isDark ? "#C084FC" : "#7C3AED",
            FuncColor: accent,
            StringColor: isDark ? "#34D399" : "#059669",
            CommentColor: isDark ? "#6B7280" : "#9CA3AF",
            NumberColor: isDark ? "#FBBF24" : "#D97706",
            TextColor: txt);
    }

    private async Task ReloadThemePacksAsync()
    {
        var gen = ++_themeListGen;
        var loaded = await Config.LoadAllThemePacksAsync();
        if (gen != _themeListGen) return;

        _themes = loaded;
        RebuildThemeGridCards();
        UpdateThemeSlotOverview();
    }

    private void DisposeThemeCardBitmaps()
    {
        PART_ThemeCardsGrid.Children.Clear();
    }

    private void RebuildThemeGridCards()
    {
        DisposeThemeCardBitmaps();

        var currentActive = Core.Theme.FolderName;
        var filtered = _themes.AsEnumerable();

        if (_themeFilter == "dark")
        {
            filtered = filtered.Where(t => t.Settings.IsDarkMode);
        }
        else if (_themeFilter == "light")
        {
            filtered = filtered.Where(t => !t.Settings.IsDarkMode);
        }

        if (!string.IsNullOrWhiteSpace(_themeSearchQuery))
        {
            var q = _themeSearchQuery.Trim();
            filtered = filtered.Where(t =>
                (t.Info.Name != null && t.Info.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                || t.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (t.Info.Description != null && t.Info.Description.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        var list = filtered.ToList();
        PART_ThemePacksCount.Text = list.Count.ToString();
        PART_ThemeEmptyText.IsVisible = list.Count == 0;

        foreach (var theme in list)
        {
            var isActive = string.Equals(theme.FolderName, currentActive, StringComparison.OrdinalIgnoreCase);
            PART_ThemeCardsGrid.Children.Add(BuildThemeGridCard(theme, isActive));
        }
    }

    private Border BuildThemeGridCard(IgTheme theme, bool isActive)
    {
        var data = GetThemePaletteData(theme);
        var isDark = theme.Settings.IsDarkMode;

        // 1. Mini Window Container (IDE preview mockup)
        var miniWindow = new Border
        {
            Height = 106,
            Margin = new Thickness(6, 6, 6, 0),
            Background = Brush.Parse(data.BgHex),
            BorderBrush = Brush.Parse(isDark ? "#24FFFFFF" : "#E2E8F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
        };

        // Window header bar (traffic light dots)
        var headerBar = new Border
        {
            Height = 18,
            Background = Brush.Parse(data.HeaderBgHex),
            Padding = new Thickness(7, 0),
        };
        var trafficLights = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        trafficLights.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Brush.Parse("#FF5F56") });
        trafficLights.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Brush.Parse("#FFBD2E") });
        trafficLights.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Brush.Parse("#27C93F") });

        var modeIcon = new Avalonia.Controls.Shapes.Path
        {
            Width = 9.5,
            Height = 9.5,
            Data = Resx.GetIcon(isDark ? ResxIconId.IconWeatherMoon : ResxIconId.IconWeatherSunny),
            Fill = Brush.Parse(isDark ? "#7A8293" : "#9CA3AF"),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        Grid.SetColumn(trafficLights, 0);
        Grid.SetColumn(modeIcon, 2);
        headerRow.Children.AddRange([trafficLights, modeIcon]);
        headerBar.Child = headerRow;

        // Code body (syntax highlighted code snippet)
        var codeBody = new StackPanel
        {
            Margin = new Thickness(8, 6, 8, 4),
            Spacing = 2,
        };

        var mutedTextBrush = Brush.Parse(isDark ? "#8E99AC" : "#57606A");

        // Line 1: function greet(name) {
        var line1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        line1.Children.Add(new TextBlock { Text = "function", Foreground = Brush.Parse(data.KeywordColor), FontSize = 9, FontFamily = _codeFont });
        line1.Children.Add(new TextBlock { Text = "greet", Foreground = Brush.Parse(data.FuncColor), FontSize = 9, FontFamily = _codeFont, FontWeight = FontWeight.SemiBold });
        line1.Children.Add(new TextBlock { Text = "(name) {", Foreground = mutedTextBrush, FontSize = 9, FontFamily = _codeFont });

        // Line 2: return "Hi, " + name;
        var line2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(7, 0, 0, 0) };
        line2.Children.Add(new TextBlock { Text = "return", Foreground = Brush.Parse(data.KeywordColor), FontSize = 9, FontFamily = _codeFont });
        line2.Children.Add(new TextBlock { Text = "\"Hi, \"", Foreground = Brush.Parse(data.StringColor), FontSize = 9, FontFamily = _codeFont });
        line2.Children.Add(new TextBlock { Text = "+ name;", Foreground = mutedTextBrush, FontSize = 9, FontFamily = _codeFont });

        // Line 3: const theme = "Name";
        var line3 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(7, 0, 0, 0) };
        line3.Children.Add(new TextBlock { Text = "const", Foreground = Brush.Parse(data.KeywordColor), FontSize = 9, FontFamily = _codeFont });
        line3.Children.Add(new TextBlock { Text = "theme", Foreground = Brush.Parse(data.NumberColor), FontSize = 9, FontFamily = _codeFont });
        line3.Children.Add(new TextBlock { Text = "=", Foreground = mutedTextBrush, FontSize = 9, FontFamily = _codeFont });
        line3.Children.Add(new TextBlock { Text = $"\"{theme.Info.Name}\";", Foreground = Brush.Parse(data.StringColor), FontSize = 9, FontFamily = _codeFont });

        // Line 4: }
        var line4 = new TextBlock { Text = "}", Foreground = mutedTextBrush, FontSize = 9, FontFamily = _codeFont };

        codeBody.Children.AddRange([line1, line2, line3, line4]);

        // Bottom Palette Strip (6 segmented colors)
        var paletteStrip = new Grid
        {
            Height = 4,
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*"),
        };
        for (int i = 0; i < 6 && i < data.Palette.Length; i++)
        {
            var segment = new Border { Background = Brush.Parse(data.Palette[i]) };
            Grid.SetColumn(segment, i);
            paletteStrip.Children.Add(segment);
        }

        var miniWindowGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(headerBar, 0);
        Grid.SetRow(codeBody, 1);
        Grid.SetRow(paletteStrip, 2);
        miniWindowGrid.Children.AddRange([headerBar, codeBody, paletteStrip]);
        miniWindow.Child = miniWindowGrid;

        // 2. Card Bottom Row (Theme Name + Active pill)
        var bottomGrid = new Grid
        {
            Margin = new Thickness(8, 8, 8, 8),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var displayName = string.IsNullOrWhiteSpace(theme.Info.Name) ? theme.FolderName : theme.Info.Name;
        var nameBlock = new TextBlock
        {
            Text = displayName,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = BHelper.ColorFromHex(Core.Theme.Colors.TextColor).ToBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameBlock, 0);

        var activeBadge = new Border
        {
            Padding = new Thickness(7, 2),
            Background = Brush.Parse("#1F3A5F"),
            BorderBrush = Brush.Parse("#2E5C9A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = isActive,
            Child = new TextBlock
            {
                Text = "Active",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#58A6FF"),
            },
        };
        Grid.SetColumn(activeBadge, 1);
        bottomGrid.Children.AddRange([nameBlock, activeBadge]);

        // 3. Card Outer Border
        var cardGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            IsHitTestVisible = false,
        };
        Grid.SetRow(miniWindow, 0);
        Grid.SetRow(bottomGrid, 1);
        cardGrid.Children.AddRange([miniWindow, bottomGrid]);

        var card = new Border
        {
            Margin = new Thickness(4),
            Child = cardGrid,
        };
        card.Classes.Add("theme-grid-card");
        if (isActive)
        {
            card.Classes.Add("active");
        }

        card.DoubleTapped += async (_, _) => await ShowLargeThemePreviewAsync(theme);
        card.PointerPressed += async (_, _) => await SelectAndApplyThemeAsync(theme);

        return card;
    }

    private async Task SelectAndApplyThemeAsync(IgTheme theme)
    {
        if (_isThemeLocked) return;

        if (theme.Settings.IsDarkMode)
        {
            _darkTheme = theme.FolderName;
            Core.Config.DarkTheme = _darkTheme;
            if (Core.Config.AppearanceMode == "Light"
                || (Core.Config.AppearanceMode == "System" && !Core.IsSystemDarkMode))
            {
                _appearanceMode = "Dark";
                Core.Config.AppearanceMode = "Dark";
            }
        }
        else
        {
            _lightTheme = theme.FolderName;
            Core.Config.LightTheme = _lightTheme;
            if (Core.Config.AppearanceMode == "Dark"
                || (Core.Config.AppearanceMode == "System" && Core.IsSystemDarkMode))
            {
                _appearanceMode = "Light";
                Core.Config.AppearanceMode = "Light";
            }
        }

        if (Application.Current is App app)
        {
            await app.ApplyThemePackAsync(Core.IsSystemDarkMode);
        }
        await Core.Config.SaveAsync();

        RebuildThemeGridCards();
        UpdateThemeSlotOverview();
        UpdateAppearanceMode();
        UpdateFooterState();
    }

    private void ThemeFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        _themeFilter = mode;
        PART_ThemeFilterAll.Classes.Set("active", mode == "all");
        PART_ThemeFilterDark.Classes.Set("active", mode == "dark");
        PART_ThemeFilterLight.Classes.Set("active", mode == "light");
        RebuildThemeGridCards();
    }

    private void ThemeSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _themeSearchQuery = PART_ThemeSearchBox.Text ?? string.Empty;
        PART_BtnClearThemeSearch.IsVisible = _themeSearchQuery.Length > 0;
        RebuildThemeGridCards();
    }

    private void ClearThemeSearch_Click(object? sender, RoutedEventArgs e)
    {
        PART_ThemeSearchBox.Text = string.Empty;
        PART_ThemeSearchBox.Focus();
    }

    private async Task ShowLargeThemePreviewAsync(IgTheme theme)
    {
        if (string.IsNullOrEmpty(theme.Settings.PreviewImage)) return;

        var path = Path.Combine(theme.FolderPath, theme.Settings.PreviewImage);
        if (!File.Exists(path)) return;

        Bitmap? bmp;
        try
        {
            bmp = await Task.Run(() => DecodePreview(path, 0));
        }
        catch { return; }
        if (bmp is null || !IsVisible)
        {
            bmp?.Dispose();
            return;
        }

        CloseLargeThemePreview();
        _largePreviewBmp = bmp;
        PART_PreviewThemeTitle.Text = string.IsNullOrWhiteSpace(theme.Info.Name) ? theme.FolderName : theme.Info.Name;
        PART_PreviewLargeImage.Source = bmp;
        PART_ThemePreviewOverlay.IsVisible = true;
    }


    private void CloseLargeThemePreview()
    {
        PART_ThemePreviewOverlay.IsVisible = false;
        PART_PreviewLargeImage.Source = null;
        _largePreviewBmp?.Dispose();
        _largePreviewBmp = null;
    }


    private static Bitmap? DecodePreview(string path, int decodeWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return decodeWidth > 0 ? Bitmap.DecodeToWidth(stream, decodeWidth) : new Bitmap(stream);
        }
        catch
        {
            try
            {
                using var skbmp = SKBitmap.Decode(path);
                return SkiaCodec.ToWritableBitmap(skbmp);
            }
            catch { return null; }
        }
    }


    private async Task InstallThemesAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Core.Lang[LangId.Settings_Theme])
                {
                    Patterns = [THEME_PACKAGE_PATTERN],
                },
            ],
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;

        var result = await Config.InstallThemePacksAsync(paths);
        await ReloadThemePacksAsync();

        if (result.IncompatiblePackNames.Count > 0)
        {
            var details = string.Join(Environment.NewLine, result.IncompatiblePackNames.Select(n => $"- {n}"));
            await ModalWindow.ShowErrorAsync(topLevel as PhWindow, new ModalWindowOptions
            {
                Title = Core.Lang[LangId.Settings_Theme_InstallTheme],
                Heading = Core.Lang[LangId._IncompatibleTheme],
                Description = Core.Lang[LangId._IncompatibleTheme_Description],
                Details = details,
            });
        }
    }


    private async Task UninstallThemeAsync(IgTheme theme)
    {
        if (_isThemeLocked || !Config.UninstallThemePack(theme)) return;

        if (string.Equals(theme.FolderName, _darkTheme, StringComparison.OrdinalIgnoreCase))
        {
            _darkTheme = Const.DEFAULT_THEME;
        }
        if (string.Equals(theme.FolderName, _lightTheme, StringComparison.OrdinalIgnoreCase))
        {
            _lightTheme = Const.DEFAULT_LIGHT_THEME;
        }

        await ReloadThemePacksAsync();
        UpdateFooterState();
    }


    private sealed class ThemeCardItem(IgTheme theme, Button darkBtn, Button lightBtn)
    {
        public IgTheme Theme { get; } = theme;
        public Button DarkBtn { get; } = darkBtn;
        public Button LightBtn { get; } = lightBtn;
        public Bitmap? PreviewBmp { get; set; }
    }

    #endregion // Theme & Appearance Section


    #region File Type Associations Section

    private void ConfigureAssocPlatformIntegration()
    {
        var showWin = OperatingSystem.IsWindows()
            && Core.ShellProvider?.IsDefaultViewerConfigurable == true;
        var showLinuxMenu = Core.ShellProvider?.CanRegisterAppMenuEntry == true;

        PART_WinIntegrationRow.IsVisible = showWin;
        PART_LinuxAppMenuRow.IsVisible = showLinuxMenu;
        PART_SysIntegrationBar.IsVisible = showWin || showLinuxMenu;

        if (showWin)
        {
            var scope = Core.ShellProvider?.GetDefaultViewerScope() ?? DefaultAppScope.CurrentUser;
            PART_WinDefaultTitle.Text = Core.Lang[LangId.Settings_DefaultPhotoViewer];
            PART_WinDefaultScope.Text = Core.Lang[scope == DefaultAppScope.LocalMachine
                ? LangId.Settings_DefaultPhotoViewer_ScopePerMachine
                : LangId.Settings_DefaultPhotoViewer_ScopePerUser];

            PART_BtnWinRegister.Content = Core.Lang[LangId._Register];
            PART_BtnWinUnregister.Content = Core.Lang[LangId._Unregister];
            PART_BtnWinDefaultApps.Content = Core.Lang[LangId.Settings_OpenDefaultAppsSetting];
            PART_BtnWinExtIcons.Content = Core.Lang[LangId.Settings_OpenExtensionIconFolder];

            ToolTip.SetTip(PART_BtnWinRegister, Core.Lang[LangId.Settings_UnmanagedSettingReminder]);
            ToolTip.SetTip(PART_BtnWinExtIcons, Core.Lang[LangId.Settings_FileExtensionIcons]);
        }

        if (showLinuxMenu)
        {
            PART_LinuxAppMenuTitle.Text = Core.Lang[LangId.Settings_AppMenuEntry];
            PART_BtnLinuxRegister.Content = Core.Lang[LangId._Register];
            PART_BtnLinuxUnregister.Content = Core.Lang[LangId._Unregister];
            ToolTip.SetTip(PART_BtnLinuxRegister, Core.Lang[LangId.Settings_UnmanagedSettingReminder]);
        }
    }


    private void SnapshotCodecs()
    {
        _codecs = Core.CodecRegistry.GetCodecInfos();
        _pluginExts.Clear();
        _pluginEncodeExts.Clear();
        _decoderCache.Clear();
        _encoderCache.Clear();

        foreach (var codec in _codecs)
        {
            if (!codec.IsPlugin) continue;
            foreach (var ext in codec.DecodingExtensions) _pluginExts.Add(ext);
            foreach (var ext in codec.EncodingExtensions) _pluginEncodeExts.Add(ext);
        }
    }


    private CodecInfo? DecodeCodecFor(string ext)
    {
        if (_decoderCache.TryGetValue(ext, out var cached)) return cached;

        CodecInfo? found = null;
        foreach (var codec in _codecs)
        {
            if (codec.DecodingExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                found = codec;
                break;
            }
        }
        found ??= _codecs.FirstOrDefault(c => c.IsFallback);

        _decoderCache[ext] = found;
        return found;
    }


    private CodecInfo? EncodeCodecFor(string ext)
    {
        if (_encoderCache.TryGetValue(ext, out var cached)) return cached;

        var found = Core.CodecRegistry.GetEncodeCodecInfo(ext);
        _encoderCache[ext] = found;
        return found;
    }


    private void RebuildFormatsList()
    {
        var browsable = new HashSet<string>(_exts, StringComparer.OrdinalIgnoreCase);
        foreach (var ext in _pluginExts) browsable.Add(ext);

        var all = new HashSet<string>(browsable, StringComparer.OrdinalIgnoreCase);
        foreach (var ext in _pluginEncodeExts) all.Add(ext);

        var customCount = _exts.Count(e => !_defaultSet.Contains(e));
        var pluginOnlyCount = all.Count(e => !_exts.Contains(e));

        PART_FilterAll.Content = T($"全部 ({all.Count})", $"All ({all.Count})");
        PART_FilterCustom.Content = customCount > 0
            ? T($"自定义 ({customCount})", $"Custom ({customCount})")
            : T("自定义", "Custom");
        PART_FilterPlugin.IsVisible = pluginOnlyCount > 0;
        PART_FilterPlugin.Content = T($"插件 ({pluginOnlyCount})", $"Plugin ({pluginOnlyCount})");

        if (_filterMode == "plugin" && pluginOnlyCount == 0)
        {
            _filterMode = "all";
            UpdateFilterPillStyles();
        }

        var query = _searchQuery.Trim();
        var filtered = all.Where(ext =>
        {
            var inExts = _exts.Contains(ext);
            var isCustom = inExts && !_defaultSet.Contains(ext);
            var isPluginOnly = !inExts;

            if (_filterMode == "custom" && !isCustom) return false;
            if (_filterMode == "plugin" && !isPluginOnly) return false;

            if (query.Length == 0) return true;

            var decName = DecodeCodecFor(ext)?.CodecName ?? string.Empty;
            var encName = EncodeCodecFor(ext)?.CodecName ?? string.Empty;
            return ext.Contains(query, StringComparison.OrdinalIgnoreCase)
                || decName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || encName.Contains(query, StringComparison.OrdinalIgnoreCase);
        })
        .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
        .ToList();

        var customLabel = T("自定义", "Custom");
        var pluginLabel = T("插件", "Plugin");
        var deleteTip = Core.Lang[LangId._Delete];
        var readOnlyTip = _isFormatsLocked
            ? Core.Lang[LangId.Settings_AdminLockWarning]
            : T("由编解码器插件提供支持，不可在此删除", "Provided by a codec plugin");

        var rows = new List<SettingsFormatRowModel>(filtered.Count);
        for (var i = 0; i < filtered.Count; i++)
        {
            var ext = filtered[i];
            var inExts = _exts.Contains(ext);
            var isCustom = inExts && !_defaultSet.Contains(ext);
            var isPluginOnly = !inExts;

            var dec = DecodeCodecFor(ext);
            var enc = EncodeCodecFor(ext);

            rows.Add(new SettingsFormatRowModel
            {
                IndexText = (i + 1).ToString("D2"),
                Extension = ext,
                DecoderText = string.IsNullOrEmpty(dec?.CodecName) ? "–" : dec.CodecName,
                EncoderText = string.IsNullOrEmpty(enc?.CodecName) ? "–" : enc.CodecName,
                DecoderForeground = string.IsNullOrEmpty(dec?.CodecName)
                    ? _textMutedBrush
                    : (!string.IsNullOrEmpty(dec?.PluginId) ? _textPluginBrush : _textPrimaryBrush),
                EncoderForeground = string.IsNullOrEmpty(enc?.CodecName)
                    ? _textMutedBrush
                    : (!string.IsNullOrEmpty(enc?.PluginId) ? _textPluginBrush : _textSecondaryBrush),
                IsCustom = isCustom,
                IsPlugin = isPluginOnly,
                BadgeText = isCustom ? customLabel : (isPluginOnly ? pluginLabel : string.Empty),
                CanDelete = inExts && !_isFormatsLocked,
                IsReadOnlyIndicatorVisible = !inExts || _isFormatsLocked,
                DeleteTooltip = deleteTip,
                ReadOnlyTooltip = readOnlyTip,
                IsHighlighted = _highlightedExt is not null
                    && string.Equals(ext, _highlightedExt, StringComparison.OrdinalIgnoreCase),
            });
        }

        PART_FormatsList.ItemsSource = rows;
        PART_EmptyState.IsVisible = rows.Count == 0;
        PART_TotalFormatsText.Text = string.Format(Core.Lang[LangId.Settings_TotalSupportedFormats], browsable.Count);

        UpdateFooterState();
    }


    private void UpdateFilterPillStyles()
    {
        PART_FilterAll.Classes.Set("active", _filterMode == "all");
        PART_FilterCustom.Classes.Set("active", _filterMode == "custom");
        PART_FilterPlugin.Classes.Set("active", _filterMode == "plugin");
    }


    private void HighlightAndScrollTo(string ext)
    {
        _highlightedExt = ext;
        var version = ++_highlightVersion;
        RebuildFormatsList();

        Dispatcher.UIThread.Post(() =>
        {
            if (PART_FormatsList.ItemsSource is IList<SettingsFormatRowModel> items)
            {
                var idx = -1;
                for (var i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i].Extension, ext, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0 && PART_FormatsList.ContainerFromIndex(idx) is Control rowControl)
                {
                    rowControl.BringIntoView();
                }
            }
        }, DispatcherPriority.Loaded);

        _ = Task.Delay(1800).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_highlightVersion == version && IsVisible)
                {
                    _highlightedExt = null;
                    RebuildFormatsList();
                }
            });
        });
    }


    private void ShowAddBar()
    {
        if (_isFormatsLocked) return;
        PART_AddBar.IsVisible = true;
        PART_AddInput.Text = string.Empty;
        PART_AddInputBox.Classes.Set("invalid", false);
        ResetAddHint();

        Dispatcher.UIThread.Post(() => PART_AddInput.Focus(), DispatcherPriority.Input);
    }


    private void HideAddBar(bool clearText)
    {
        PART_AddBar.IsVisible = false;
        PART_AddInputBox.Classes.Set("invalid", false);
        if (clearText) PART_AddInput.Text = string.Empty;
        ResetAddHint();
    }


    private void ResetAddHint()
    {
        PART_AddHintText.Foreground = _hintNormalBrush;
        PART_AddHintText.Text = T(
            "可输入如 .avif 或 jxl（支持以分号或逗号分隔批量加入）",
            "Enter extensions like .avif or jxl (separate multiple with ; or ,)");
    }


    private bool TryParseExtensions(string rawInput, out List<string> validExts, out string? errorMsg)
    {
        validExts = [];
        errorMsg = null;

        var tokens = rawInput.Split([';', ',', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            errorMsg = Core.Lang[LangId._Validation_Required];
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            var normalized = token.Trim().ToLowerInvariant();
            if (!normalized.StartsWith('.')) normalized = "." + normalized;

            var body = normalized.Length > 1 ? normalized[1..] : string.Empty;
            if (body.Length == 0 || body.Contains('.') || body.IndexOfAny(invalidChars) >= 0)
            {
                errorMsg = T($"无效的文件扩展名格式: {token}", $"Invalid extension: {token}");
                return false;
            }

            if (seen.Add(normalized))
            {
                validExts.Add(normalized);
            }
        }

        return validExts.Count > 0;
    }


    private void CommitInlineAdd()
    {
        if (_isFormatsLocked) return;

        var raw = PART_AddInput.Text?.Trim() ?? string.Empty;
        if (!TryParseExtensions(raw, out var candidates, out var errorMsg))
        {
            PART_AddInputBox.Classes.Set("invalid", true);
            PART_AddHintText.Foreground = _hintErrorBrush;
            PART_AddHintText.Text = errorMsg;
            PART_AddInput.Focus();
            return;
        }

        var addedExts = new List<string>();
        foreach (var ext in candidates)
        {
            if (!_exts.Contains(ext) && !_pluginExts.Contains(ext) && !_pluginEncodeExts.Contains(ext))
            {
                if (_exts.Add(ext)) addedExts.Add(ext);
            }
        }

        if (addedExts.Count == 0 && candidates.Count == 1)
        {
            var existing = candidates[0];
            _filterMode = "all";
            _searchQuery = string.Empty;
            PART_SearchBox.Text = string.Empty;
            PART_BtnClearSearch.IsVisible = false;
            UpdateFilterPillStyles();

            PART_AddInputBox.Classes.Set("invalid", false);
            PART_AddHintText.Foreground = _countDirtyDotBrush;
            PART_AddHintText.Text = T(
                $"格式 {existing} 已存在于列表中，已为您高亮定位",
                $"{existing} is already in the list and has been highlighted");

            HighlightAndScrollTo(existing);
            return;
        }

        var focusTarget = addedExts.Count > 0 ? addedExts[^1] : candidates[0];
        if (_filterMode == "plugin")
        {
            _filterMode = "all";
            UpdateFilterPillStyles();
        }
        if (_searchQuery.Length > 0)
        {
            _searchQuery = string.Empty;
            PART_SearchBox.Text = string.Empty;
            PART_BtnClearSearch.IsVisible = false;
        }

        HideAddBar(clearText: true);
        HighlightAndScrollTo(focusTarget);
    }

    #endregion // File Type Associations Section


    #region Unified Save & Reset

    private async Task SaveAndCloseAsync()
    {
        if (_isSaving) return;
        _isSaving = true;
        PART_BtnSave.IsEnabled = false;

        try
        {
            var darkChanged = !_isThemeLocked
                && !string.Equals(_darkTheme, _initialDarkTheme, StringComparison.OrdinalIgnoreCase);
            var lightChanged = !_isThemeLocked
                && !string.Equals(_lightTheme, _initialLightTheme, StringComparison.OrdinalIgnoreCase);
            var bgChanged = !_isBgColorLocked
                && !string.Equals(NormalizeBgForCompare(_bgColor), NormalizeBgForCompare(_initialBgColor), StringComparison.OrdinalIgnoreCase);
            var backdropChanged = !_isBackdropLocked && _backdrop != _initialBackdrop;
            var assocChanged = !_isFormatsLocked && IsAssocDirty();

            var appearanceChanged = _appearanceMode != _initialAppearanceMode;
            var anyChanged = appearanceChanged || darkChanged || lightChanged || bgChanged || backdropChanged || assocChanged;
            if (!anyChanged)
            {
                HideOverlayAndDisposePreviews();
                return;
            }

            // 1. 写入全部未锁定的工作副本到 Core.Config
            if (darkChanged) Core.Config.Set(ConfigId.DarkTheme, _darkTheme);
            if (appearanceChanged) Core.Config.AppearanceMode = _appearanceMode;
            if (lightChanged) Core.Config.Set(ConfigId.LightTheme, _lightTheme);
            if (backdropChanged) Core.Config.Set(ConfigId.WindowBackdrop, _backdrop);
            if (bgChanged) Core.Config.Set(ConfigId.BackgroundColor, _bgColor);
            if (assocChanged) Core.Config.Set(ConfigId.FileFormats, new HashSet<string>(_exts, StringComparer.OrdinalIgnoreCase));

            // 2. 应用主题包变更 (可能覆盖主题自身 BackgroundColor，所以如果用户设置了自定义色将在之后保底还原)
            if ((appearanceChanged || darkChanged || lightChanged) && Application.Current is App app)
            {
                await app.ApplyThemePackAsync(Core.IsSystemDarkMode);

                // 确保用户刚才挑选或保持的自定义查看器背景色不被 LoadCurrentThemeAsync 覆盖
                if (bgChanged)
                {
                    Core.Config.Set(ConfigId.BackgroundColor, _bgColor);
                }
            }

            // 3. 持久化保存配置
            var isSaved = await Core.Config.SaveAsync();
            if (!isSaved)
            {
                await ModalWindow.ShowSettingsSaveErrorAsync(
                    TopLevel.GetTopLevel(this) as PhWindow,
                    Core.Lang[LangId.Menu_MnuSettings]);
                return;
            }

            // 4. 若关联格式发生变化且正在浏览文件夹，刷新当前文件夹图片清单
            if (assocChanged && Application.Current is App appForReload)
            {
                appForReload.ReloadAllImageLists();
            }

            HideOverlayAndDisposePreviews();
        }
        finally
        {
            _isSaving = false;
            PART_BtnSave.IsEnabled = true;
        }
    }


    private async void ResetCurrentPageDefaults_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeSection == "theme")
        {
            await ApplyAppearanceModeAsync("System");
            return;
        }

        if (_isFormatsLocked) return;
        _exts.Clear();
        foreach (var ext in Config.DefaultFileFormats) _exts.Add(ext);

        if (_filterMode == "custom")
        {
            _filterMode = "all";
            UpdateFilterPillStyles();
        }
        _highlightedExt = null;
        RebuildFormatsList();
    }

    #endregion // Unified Save & Reset


    #region UI Event Handlers

    private void Cancel_Click(object? sender, RoutedEventArgs e) => CancelAndClose();

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAndCloseAsync();

    private void NavTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target })
        {
            SwitchSection(target);
        }
    }

    private void UpdateAppearanceMode()
    {
        PART_ModeSystem.Classes.Set("active", _appearanceMode == "System");
        PART_ModeDark.Classes.Set("active", _appearanceMode == "Dark");
        PART_ModeLight.Classes.Set("active", _appearanceMode == "Light");
    }

    private async void AppearanceMode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode })
        {
            await ApplyAppearanceModeAsync(mode);
        }
    }

    private async Task ApplyAppearanceModeAsync(string mode)
    {
        if (_appearanceMode == mode) return;
        PART_ModeSystem.IsEnabled = PART_ModeDark.IsEnabled = PART_ModeLight.IsEnabled = false;
        try
        {
            _appearanceMode = mode;
            Core.Config.AppearanceMode = mode;
            if (Application.Current is App app) await app.ApplyThemePackAsync(Core.IsSystemDarkMode);
            UpdateAppearanceMode();
            RebuildThemeGridCards();
            RebuildFormatsList();
            _initialAppearanceMode = mode;
            UpdateFooterState();
            if (!await Core.Config.SaveAsync())
                await ModalWindow.ShowSettingsSaveErrorAsync(TopLevel.GetTopLevel(this) as PhWindow, Core.Lang[LangId.Menu_MnuSettings]);
        }
        finally
        {
            PART_ModeSystem.IsEnabled = PART_ModeDark.IsEnabled = PART_ModeLight.IsEnabled = true;
        }
    }

    // ---- 主题页事件 ----

    private async void InstallTheme_Click(object? sender, RoutedEventArgs e) => await InstallThemesAsync();

    private async void RefreshThemes_Click(object? sender, RoutedEventArgs e) => await ReloadThemePacksAsync();

    private void OpenThemeFolder_Click(object? sender, RoutedEventArgs e)
        => BHelper.OpenFolderPath(BHelper.GetRealPlatformConfigDir(Dir.Themes));

    private async void GetMoreThemes_Click(object? sender, RoutedEventArgs e)
        => await BHelper.OpenUrlAsync(TopLevel.GetTopLevel(this) as PhWindow, THEMES_URL, "from_setting_appearance");

    private void CloseThemePreview_Click(object? sender, RoutedEventArgs e) => CloseLargeThemePreview();

    private void CloseThemePreview_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseLargeThemePreview();
        e.Handled = true;
    }

    private void PresetBgSwatch_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBgColorLocked) return;
        if (sender is not Button { Tag: string hex }) return;

        _bgColor = hex;
        SyncBgColorUI(hex);
        _previewBgCallback?.Invoke(hex);
        UpdateFooterState();
    }

    private void ResetBgColor_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBgColorLocked) return;

        _bgColor = "#00000000";
        SyncBgColorUI(_bgColor);
        _previewBgCallback?.Invoke(_bgColor);
        UpdateFooterState();
    }

    private void BgHexInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressBgTextEvent || _isBgColorLocked) return;

        var raw = PART_BgHexInput.Text?.Trim() ?? string.Empty;
        if (!TryParseHexColorStrict(raw, out var color))
        {
            PART_BgHexBox.Classes.Set("invalid", raw.Length > 0);
            return;
        }

        PART_BgHexBox.Classes.Set("invalid", false);
        var formatted = color.ToHex(skipAlpha: false);
        _bgColor = formatted;

        if (color.A == 0 || string.Equals(formatted, "#AAFFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            PART_BgPreviewDot.Background = Brush.Parse("#262932");
        }
        else
        {
            PART_BgPreviewDot.Background = new SolidColorBrush(color);
        }

        // 更新预设圆点激活状态但不重设 TextBox 光标位置
        var normalized = NormalizeBgForCompare(formatted);
        var isDefault = color.A == 0 || normalized == "#AAFFFFFF";
        PART_SwatchDefault.Classes.Set("selected", isDefault);
        PART_SwatchBlack.Classes.Set("selected", !isDefault && normalized == "#FF0B0D10");
        PART_SwatchSlate.Classes.Set("selected", !isDefault && normalized == "#FF1E2026");
        PART_SwatchNeutral.Classes.Set("selected", !isDefault && normalized == "#FF4A4F59");
        PART_SwatchLightGray.Classes.Set("selected", !isDefault && normalized == "#FFE5E7EB");
        PART_SwatchWhite.Classes.Set("selected", !isDefault && normalized == "#FFFFFFFF");

        _previewBgCallback?.Invoke(formatted);
        UpdateFooterState();
    }

    private void BackdropPill_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBackdropLocked) return;
        if (sender is Button { Tag: string styleName } && Enum.TryParse<BackdropStyle>(styleName, out var style))
        {
            _backdrop = style;
            UpdateBackdropPills();
            UpdateFooterState();
        }
    }

    // ---- 关联格式页事件 ----

    private void ToggleAddBar_Click(object? sender, RoutedEventArgs e)
    {
        if (PART_AddBar.IsVisible) HideAddBar(clearText: true);
        else ShowAddBar();
    }

    private void ConfirmAdd_Click(object? sender, RoutedEventArgs e) => CommitInlineAdd();

    private void CancelAdd_Click(object? sender, RoutedEventArgs e) => HideAddBar(clearText: true);

    private void AddInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (PART_AddInputBox.Classes.Contains("invalid"))
        {
            PART_AddInputBox.Classes.Set("invalid", false);
            ResetAddHint();
        }
    }

    private void AddInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitInlineAdd();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideAddBar(clearText: true);
            PART_SearchBox.Focus();
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchQuery = PART_SearchBox.Text ?? string.Empty;
        PART_BtnClearSearch.IsVisible = _searchQuery.Length > 0;
        _highlightedExt = null;
        RebuildFormatsList();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _searchQuery.Length > 0)
        {
            e.Handled = true;
            PART_SearchBox.Text = string.Empty;
        }
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        PART_SearchBox.Text = string.Empty;
        PART_SearchBox.Focus();
    }

    private void FilterPill_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        _filterMode = mode;
        _highlightedExt = null;
        UpdateFilterPillStyles();
        RebuildFormatsList();
    }

    private void DeleteFormatRow_Click(object? sender, RoutedEventArgs e)
    {
        if (_isFormatsLocked) return;
        if (sender is not Button { Tag: string ext }) return;

        if (_exts.Remove(ext))
        {
            if (string.Equals(_highlightedExt, ext, StringComparison.OrdinalIgnoreCase))
            {
                _highlightedExt = null;
            }
            RebuildFormatsList();
        }
    }

    private async void WinRegister_Click(object? sender, RoutedEventArgs e)
        => await AppAPIProvider.IG_SetDefaultPhotoViewerAsync();

    private async void WinUnregister_Click(object? sender, RoutedEventArgs e)
        => await AppAPIProvider.IG_RemoveDefaultPhotoViewerAsync();

    private async void WinOpenDefaultApps_Click(object? sender, RoutedEventArgs e)
    {
        var scope = Core.ShellProvider?.GetDefaultViewerScope() ?? DefaultAppScope.CurrentUser;
        var appQueryKey = scope == DefaultAppScope.LocalMachine ? "registeredAppMachine" : "registeredAppUser";
        var defaultAppsUri = $"ms-settings:defaultapps?{appQueryKey}={Uri.EscapeDataString(BHelper.AppName)}";
        await BHelper.OpenUrlAsync(TopLevel.GetTopLevel(this) as PhWindow, defaultAppsUri, "from_default_apps");
    }

    private void WinOpenExtIcons_Click(object? sender, RoutedEventArgs e)
        => BHelper.OpenFolderPath(BHelper.GetRealPlatformConfigDir(Dir.ExtIcons));

    private async void LinuxRegister_Click(object? sender, RoutedEventArgs e)
        => await AppAPIProvider.RegisterAppMenuEntryAsync(true);

    private async void LinuxUnregister_Click(object? sender, RoutedEventArgs e)
        => await AppAPIProvider.RegisterAppMenuEntryAsync(false);

    public void ApplyAboutUpdateResult(UpdateCheckResult result)
    {
        PART_AboutPage.ApplyUpdateResult(result);
    }

    public void TriggerAboutUpdateCheck()
    {
        _ = PART_AboutPage.CheckForUpdateInPanelAsync();
    }

    #endregion // UI Event Handlers
}
