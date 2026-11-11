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
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ImageGlass.Common.Localization;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.Types;
using ImageGlass.UI.Windowing;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ImageGlass.Common.Windows;

/// <summary>
/// 开源组件致谢卡片的数据项。
/// </summary>
public sealed class AboutCreditItem
{
    public string Name { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public string Copyright { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public IBrush BadgeBackground { get; init; } = Brushes.Transparent;
    public IBrush BadgeForeground { get; init; } = Brushes.Gray;
    public bool HasLicense => !string.IsNullOrWhiteSpace(License);
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
}


/// <summary>
/// 应用内「关于」弹窗遮盖层控件，适配亮/暗双主题，保留并替代独立 AboutWindow 交互。
/// </summary>
public partial class AboutOverlayControl : UserControl
{
    private const int HERO_INTRO_DELAY_MS = 450;

    private IDisposable? _heroIntroTimer;
    private IDisposable? _copyFeedbackTimer;
    private string _activeTab = "overview";
    private string _fullVersionClipboardText = string.Empty;


    public AboutOverlayControl()
    {
        InitializeComponent();

        ActualThemeVariantChanged += (_, _) =>
        {
            if (IsVisible) RefreshCreditsList();
        };
    }


    /// <summary>
    /// 打开应用内关于弹窗并刷新版本、语言及主题样式。
    /// </summary>
    public void Open()
    {
        _activeTab = "overview";
        SwitchTab("overview");
        SetupEditionChip();
        ApplyLocalizedTexts();
        RefreshVersionCards();
        RefreshCreditsList();
        SetupHero();

        IsVisible = true;

        _heroIntroTimer?.Dispose();
        _heroIntroTimer = DispatcherTimer.RunOnce(PlayHeroBurst,
            TimeSpan.FromMilliseconds(HERO_INTRO_DELAY_MS));
    }


    /// <summary>
    /// 关闭弹窗并清理一次性动画与反馈定时器。
    /// </summary>
    public void Close()
    {
        _heroIntroTimer?.Dispose();
        _heroIntroTimer = null;
        _copyFeedbackTimer?.Dispose();
        _copyFeedbackTimer = null;
        IsVisible = false;
    }


    private static string T(string zhText, string enText)
        => Core.Lang.FilePath.Contains("Chinese", StringComparison.OrdinalIgnoreCase)
            ? zhText
            : enText;


    private void ApplyLocalizedTexts()
    {
        PART_LblSlogan.Text = Core.Lang[LangId._Slogan];
        PART_LblCopyright.Text = $"Copyright © 2010-{DateTime.UtcNow.Year} Dương Diệu Pháp · 二次开发：puppy";

        PART_TxtTabOverview.Text = T("版本与链接", Core.Lang[LangId.Menu_MnuAbout]);
        PART_TxtTabCredits.Text = Core.Lang[LangId._Credits];
        PART_TxtCopyVersion.Text = T("复制版本", "Copy info");

        PART_LblCardVersionTitle.Text = Core.Lang[LangId._AboutVersion].TrimEnd(':', '：', ' ');
        PART_LblCardMagickTitle.Text = "Magick.NET";
        PART_LblCardRuntimeTitle.Text = ".NET Runtime";
        PART_LblLinksHeading.Text = T("相关链接与协议", "Resources & Legal");

        PART_TxtWebsiteTitle.Text = Core.Lang[LangId._Homepage];
        PART_TxtGitHubTitle.Text = "GitHub";
        PART_TxtGitHubSub.Text = T("项目主页与帮助文档", "Project homepage & docs");
        PART_TxtEulaTitle.Text = Core.Lang[LangId._License];
        PART_TxtPrivacyTitle.Text = Core.Lang[LangId._Privacy];
        PART_TxtPrivacySub.Text = T("本地离线安全浏览", "Offline private viewing");

        PART_TxtDonate.Text = Core.Lang[LangId._Donate];
        PART_TxtCheckUpdate.Text = Core.Lang[LangId._CheckForUpdate];
        PART_BtnDone.Content = Core.Lang[LangId._Close];

        ToolTip.SetTip(PART_BtnEdition, Core.Lang[Core.IsProEnabled
            ? LangId.Menu_MnuManageLicense
            : LangId.Menu_MnuUpgradeLicense]);
        ToolTip.SetTip(PART_BtnClose, Core.Lang[LangId._Close]);
        ToolTip.SetTip(PART_Hero, T("点击播放星芒动效", "Click to play star burst"));
    }


    private void RefreshVersionCards()
    {
        var appVer = Core.BuildInfo.Version;
        var magickFull = MagickNET.Version;
        var magickCompact = ExtractCompactMagickVersion(magickFull);
        var runtime = RuntimeInformation.FrameworkDescription;

        PART_ValAppVersion.Text = appVer;
        PART_ValMagickVersion.Text = magickCompact;
        PART_ValRuntimeVersion.Text = runtime;
        ToolTip.SetTip(PART_ValMagickVersion, magickFull);

        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var osDesc = RuntimeInformation.OSDescription.Trim();
        PART_LblFullVersionLine.Text = $"{magickFull}  ·  {osDesc} ({arch})";

        _fullVersionClipboardText = $"""
            ImageGlass 10 {(Core.IsProEnabled ? "Pro" : "Classic")}
            {Core.Lang[LangId._AboutVersion]} {appVer}
            {magickFull}
            {runtime}
            {osDesc} ({arch})
            """;
    }


    private static string ExtractCompactMagickVersion(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "-";
        // 将如 "Magick.NET-Q16-HDRI-arm64 AnyCPU 14.0.0" 或含 "Q16-HDRI" 与版本号的字符串提炼清晰
        var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"{parts[^2]} {parts[^1]}" : full;
    }


    private void SetupEditionChip()
    {
        var isPro = Core.IsProEnabled;
        PART_TxtEdition.Text = isPro ? "Pro" : "Classic";
        PART_EditionStarIcon.IsVisible = isPro;
        PART_BtnEdition.Classes.Set("pro", isPro);
    }


    private void SetupHero()
    {
        try
        {
            PART_HeroStars.EnableStars = Core.IsProEnabled;
            PART_HeroStars.BobTarget = PART_Logo;
        }
        catch { }
    }


    private void PlayHeroBurst()
    {
        _heroIntroTimer?.Dispose();
        _heroIntroTimer = null;
        try
        {
            PART_HeroStars?.Play();
        }
        catch { }
    }


    private void SwitchTab(string tab)
    {
        _activeTab = tab == "credits" ? "credits" : "overview";
        var isOverview = _activeTab == "overview";

        PART_TabOverview.Classes.Set("active", isOverview);
        PART_TabCredits.Classes.Set("active", !isOverview);

        PART_OverviewPanel.IsVisible = isOverview;
        PART_CreditsPanel.IsVisible = !isOverview;
    }


    private void RefreshCreditsList()
    {
        var isDark = Core.Theme.Settings.IsDarkMode;
        IBrush gplBg = Brush.Parse(isDark ? "#22F59E0B" : "#1AD97706");
        IBrush gplFg = Brush.Parse(isDark ? "#FCD34D" : "#B45309");
        IBrush mitBg = Brush.Parse(isDark ? "#223B82F6" : "#1A2563EB");
        IBrush mitFg = Brush.Parse(isDark ? "#93C5FD" : "#1D4ED8");
        IBrush apacheBg = Brush.Parse(isDark ? "#228B5CF6" : "#1A7C3AED");
        IBrush apacheFg = Brush.Parse(isDark ? "#C4B5FD" : "#6D28D9");
        IBrush designBg = Brush.Parse(isDark ? "#2210B981" : "#1A059669");
        IBrush designFg = Brush.Parse(isDark ? "#6EE7B7" : "#047857");

        var items = new List<AboutCreditItem>
        {
            new() { Name = "ImageGlass", License = "GPL-3.0", Url = "https://github.com/d2phap/ImageGlass", Copyright = "Copyright (c) 2010-2026 Dương Diệu Pháp", BadgeBackground = gplBg, BadgeForeground = gplFg },
            new() { Name = "Avalonia", License = "MIT", Url = "https://github.com/AvaloniaUI/Avalonia", Copyright = "Copyright (c) AvaloniaUI OÜ All Rights Reserved", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "Magick.NET", License = "Apache-2.0", Url = "https://github.com/dlemstra/Magick.NET", Copyright = "Copyright (c) 2013-2026 Dirk Lemstra", BadgeBackground = apacheBg, BadgeForeground = apacheFg },
            new() { Name = "SkiaSharp", License = "MIT", Url = "https://github.com/mono/SkiaSharp", Copyright = "Copyright (c) 2015-2016 Xamarin, Inc · 2017-2018 Microsoft Corporation", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "Svg.Skia", License = "MIT", Url = "https://github.com/wieslawsoltes/Svg.Skia", Copyright = "Copyright (c) 2020 Wiesław Šoltés", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "NativeMemoryArray", License = "MIT", Url = "https://github.com/Cysharp/NativeMemoryArray", Copyright = "Copyright (c) 2021 Cysharp, Inc.", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "NetCoreAudio", License = "MIT", Url = "https://github.com/mobiletechtracker/NetCoreAudio", Copyright = "Copyright (c) 2020-2024 Fiodar Sazanavets (Scientific Programmer Ltd)", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "CsWin32", License = "MIT", Url = "https://github.com/microsoft/CsWin32", Copyright = "Copyright (c) Microsoft Corporation", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "Fluent Emoji", License = "MIT", Url = "https://github.com/microsoft/fluentui-emoji", Copyright = "Copyright (c) Microsoft Corporation", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "D2Phap.EggShell", License = "Apache-2.0", Url = string.Empty, Copyright = "Copyright (c) 2024-2026 Dương Diệu Pháp", BadgeBackground = apacheBg, BadgeForeground = apacheFg },
            new() { Name = "D2Phap.FileWatcherEx", License = "MIT", Url = "https://github.com/d2phap/FileWatcherEx", Copyright = "Copyright (c) 2018-2026 Dương Diệu Pháp", BadgeBackground = mitBg, BadgeForeground = mitFg },
            new() { Name = "ImageGlass logo idea", License = "Design", Url = string.Empty, Copyright = "Nguyễn Quốc Tuấn", BadgeBackground = designBg, BadgeForeground = designFg },
        };

        PART_CreditsList.ItemsSource = items;
        PART_TxtCreditsCount.Text = items.Count.ToString();
    }


    #region UI Event Handlers

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Hero_PointerPressed(object? sender, PointerPressedEventArgs e) => PlayHeroBurst();

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
        {
            SwitchTab(tab);
        }
    }

    private async void CopyVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_fullVersionClipboardText)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(_fullVersionClipboardText);
        PART_TxtCopyVersion.Text = T("已复制 ✓", "Copied ✓");

        _copyFeedbackTimer?.Dispose();
        _copyFeedbackTimer = DispatcherTimer.RunOnce(() =>
        {
            PART_TxtCopyVersion.Text = T("复制版本", "Copy info");
        }, TimeSpan.FromSeconds(1.8));
    }

    private async void Link_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            await BHelper.OpenUrlAsync(TopLevel.GetTopLevel(this) as PhWindow, url, "from_about");
        }
    }

    private async void Edition_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var owner = TopLevel.GetTopLevel(this) as PhWindow;
            _ = await new ManageLicenseWindow().ShowAsync(owner);
        }
        catch { }
    }

    private async void CheckForUpdate_Click(object? sender, RoutedEventArgs e)
    {
        _ = await Core.API.RunApiAsync(API.IG_CheckForUpdate, "true");
    }

    #endregion // UI Event Handlers
}
