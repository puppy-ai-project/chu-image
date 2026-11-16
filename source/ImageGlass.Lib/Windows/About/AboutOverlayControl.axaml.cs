/*
ChuImage - A Modern, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Secondary development by puppy.
*/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ImageGlass.Common;
using ImageGlass.Common.Localization;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.ServiceProviders.Update;
using ImageGlass.Common.Types;
using ImageGlass.UI.Windowing;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
/// 应用内「关于」弹窗遮盖层控件，集成软件更新完整工作台（检查、对比、下载、升级就绪一体化完成，无需额外弹窗）。
/// </summary>
public partial class AboutOverlayControl : UserControl
{
    private const int HERO_INTRO_DELAY_MS = 450;

    private enum UpdateFlowState
    {
        Idle,
        Checking,
        NoUpdate,
        UpdateAvailable,
        Downloading,
        ReadyToInstall,
        Failed
    }

    private UpdateFlowState _updateFlowState = UpdateFlowState.Idle;
    private UpdateCheckResult? _lastCheckResult;
    private UpdateReleaseInfo? _currentRelease;
    private CancellationTokenSource? _downloadCts;
    private string? _downloadedFilePath;
    private DispatcherTimer? _updateBadgeSpinTimer;
    private double _updateBadgeSpinAngle;

    private IDisposable? _heroIntroTimer;
    private IDisposable? _copyFeedbackTimer;
    private IDisposable? _updateFeedbackTimer;
    private string _activeTab = "update";
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
    /// 打开应用内关于弹窗并刷新版本、语言及主题样式，默认切换至第一位 Tab（软件更新）。
    /// </summary>
    public void Open(string? initialTab = "update")
    {
        SwitchTab(initialTab ?? "update");
        SetupEditionChip();
        ApplyLocalizedTexts();
        RefreshVersionCards();
        RefreshCreditsList();
        SetupHero();

        IsVisible = true;

        _heroIntroTimer?.Dispose();
        _heroIntroTimer = DispatcherTimer.RunOnce(PlayHeroBurst,
            TimeSpan.FromMilliseconds(HERO_INTRO_DELAY_MS));

        // 如果切到更新 Tab 且尚未检查过更新，自动检查一次
        if (_activeTab == "update" && _updateFlowState == UpdateFlowState.Idle)
        {
            _ = CheckForUpdateInPanelAsync();
        }
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
        _updateFeedbackTimer?.Dispose();
        _updateFeedbackTimer = null;
        StopUpdateBadgeSpin();

        if (_updateFlowState == UpdateFlowState.Downloading)
        {
            CancelDownload();
        }

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

        PART_TxtTabUpdate.Text = T("软件更新", "Software Update");
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
        var checkText = Core.Lang[LangId._CheckForUpdate];
        PART_BtnDone.Content = Core.Lang[LangId._Close];

        ToolTip.SetTip(PART_BtnCardCheckUpdate, checkText);
        ToolTip.SetTip(PART_BtnEdition, Core.Lang[Core.IsProEnabled
            ? LangId.Menu_MnuManageLicense
            : LangId.Menu_MnuUpgradeLicense]);
        ToolTip.SetTip(PART_BtnClose, Core.Lang[LangId._Close]);
        ToolTip.SetTip(PART_Hero, T("点击播放星芒动效", "Click to play star burst"));
    }


    private void RefreshVersionCards()
    {
        var appVer = Core.BuildInfo.Version;
        var magickVer = CleanVersionString(MagickNET.Version);
        var runtimeVer = CleanVersionString(RuntimeInformation.FrameworkDescription);

        PART_ValAppVersion.Text = appVer;
        PART_ValMagickVersion.Text = magickVer;
        PART_ValRuntimeVersion.Text = runtimeVer;

        var fullRuntime = RuntimeInformation.FrameworkDescription;
        var osDesc = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        _fullVersionClipboardText =
            $"ChuImage {appVer}{Environment.NewLine}" +
            $".NET Runtime: {fullRuntime} ({arch}){Environment.NewLine}" +
            $"Magick.NET: {MagickNET.Version}{Environment.NewLine}" +
            $"OS: {osDesc} ({arch}){Environment.NewLine}" +
            $"Homepage: https://image.withqq.com";
    }


    private static string CleanVersionString(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "-";
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


    public void SwitchTab(string tab)
    {
        _activeTab = tab switch
        {
            "overview" => "overview",
            "credits" => "credits",
            _ => "update"
        };

        var isUpdate = _activeTab == "update";
        var isOverview = _activeTab == "overview";
        var isCredits = _activeTab == "credits";

        PART_TabUpdate.Classes.Set("active", isUpdate);
        PART_TabOverview.Classes.Set("active", isOverview);
        PART_TabCredits.Classes.Set("active", isCredits);

        PART_UpdatePanel.IsVisible = isUpdate;
        PART_OverviewPanel.IsVisible = isOverview;
        PART_CreditsPanel.IsVisible = isCredits;
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


    #region Software Update In-Panel Flow

    /// <summary>
    /// 外部直接应用检查结果并展示在软件更新工作台。
    /// </summary>
    public void ApplyUpdateResult(UpdateCheckResult result)
    {
        SwitchTab("update");
        StopUpdateBadgeSpin();
        _lastCheckResult = result;
        _currentRelease = result.Release;

        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is not null)
        {
            SetUpdateAvailableState(result.Release);
        }
        else if (result.Status == UpdateCheckStatus.CheckFailed)
        {
            SetUpdateFailedState(string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "连接更新服务器失败，请检查网络设置后重试。"
                : result.ErrorMessage);
        }
        else
        {
            SetUpdateNoUpdateState(result.Release);
        }
    }

    /// <summary>
    /// 在面板内部直接触发检查更新流程。
    /// </summary>
    public async Task CheckForUpdateInPanelAsync()
    {
        if (_updateFlowState == UpdateFlowState.Checking || _updateFlowState == UpdateFlowState.Downloading) return;

        SwitchTab("update");
        SetUpdateCheckingState();

        try
        {
            var result = await Core.UpdateProvider.CheckForUpdateAsync(CancellationToken.None, false);
            ApplyUpdateResult(result);
        }
        catch (Exception ex)
        {
            SetUpdateFailedState($"检查更新异常：{ex.Message}");
        }
    }

    private void SetUpdateCheckingState()
    {
        _updateFlowState = UpdateFlowState.Checking;
        PART_UpdateBadgeBorder.Background = Brush.Parse("#1E3B82F6");
        PART_UpdateBadgeBorder.BorderBrush = Brush.Parse("#363B82F6");
        HideAllUpdateIcons();
        PART_UpdateIconChecking.IsVisible = true;
        StartUpdateBadgeSpin();

        PART_TxtUpdateTitle.Text = "正在检查更新...";
        PART_TxtUpdateSubtitle.Text = "正在连接官方服务器获取最新版本信息";

        PART_UpdateVersionPill.IsVisible = false;
        PART_UpdateReleaseCard.IsVisible = false;
        PART_UpDownloadCard.IsVisible = false;
        PART_UpReadyCard.IsVisible = false;
        PART_UpFailedCard.IsVisible = false;

        PART_BtnUpSecondary.IsVisible = false;
        PART_BtnUpPrimary.IsVisible = false;
        PART_BtnTriggerCheck.IsEnabled = false;
    }

    private void SetUpdateNoUpdateState(UpdateReleaseInfo? release)
    {
        _updateFlowState = UpdateFlowState.NoUpdate;
        PART_UpdateBadgeBorder.Background = Brush.Parse("#1A10B981");
        PART_UpdateBadgeBorder.BorderBrush = Brush.Parse("#3310B981");
        HideAllUpdateIcons();
        PART_UpdateIconNoUpdate.IsVisible = true;
        StopUpdateBadgeSpin();

        var currentVer = Core.BuildInfo.Version;
        PART_TxtUpdateTitle.Text = "当前已是最新版本";
        PART_TxtUpdateSubtitle.Text = $"当前版本 v{currentVer} 已是官方最新版本，暂无可用更新。";

        PART_UpdateVersionPill.IsVisible = false;
        PART_UpDownloadCard.IsVisible = false;
        PART_UpReadyCard.IsVisible = false;
        PART_UpFailedCard.IsVisible = false;
        PART_UpdateBadgeDot.IsVisible = false;

        PART_BtnUpSecondary.IsVisible = false;
        PART_BtnUpPrimary.IsVisible = false;
        PART_BtnTriggerCheck.IsVisible = true;
        PART_BtnTriggerCheck.IsEnabled = true;

        if (release is not null && !string.IsNullOrWhiteSpace(release.PublishedDate))
        {
            PART_TxtUpPublishedDate.Text = $"发布时间：{release.PublishedDate}";
            var hasNotes = !string.IsNullOrWhiteSpace(release.Description);
            PART_UpNotesScroller.IsVisible = hasNotes;
            if (hasNotes) PART_TxtUpNotes.Text = release.Description;
            PART_BtnUpChangelog.IsVisible = !string.IsNullOrWhiteSpace(release.ChangelogUrl);
            PART_UpdateReleaseCard.IsVisible = true;
        }
        else
        {
            PART_UpdateReleaseCard.IsVisible = false;
        }
    }

    private void SetUpdateAvailableState(UpdateReleaseInfo release)
    {
        _updateFlowState = UpdateFlowState.UpdateAvailable;
        PART_UpdateBadgeBorder.Background = Brush.Parse("#1E3B82F6");
        PART_UpdateBadgeBorder.BorderBrush = Brush.Parse("#3D60A5FA");
        HideAllUpdateIcons();
        PART_UpdateIconAvailable.IsVisible = true;
        StopUpdateBadgeSpin();

        PART_TxtUpdateTitle.Text = "发现新版本！";
        PART_TxtUpdateSubtitle.Text = "ChuImage 发布了新版本，建议立即更新以获得更佳体验。";

        PART_TxtUpCurrentVer.Text = $"v{Core.BuildInfo.Version}";
        PART_TxtUpLatestVer.Text = $"v{release.Version}";
        PART_UpdateVersionPill.IsVisible = true;

        PART_TxtUpPublishedDate.Text = string.IsNullOrWhiteSpace(release.PublishedDate)
            ? $"版本号：{release.Version}"
            : $"发布时间：{release.PublishedDate}";

        var hasNotes = !string.IsNullOrWhiteSpace(release.Description);
        PART_UpNotesScroller.IsVisible = hasNotes;
        if (hasNotes) PART_TxtUpNotes.Text = release.Description;
        PART_BtnUpChangelog.IsVisible = !string.IsNullOrWhiteSpace(release.ChangelogUrl);
        PART_UpdateReleaseCard.IsVisible = true;

        PART_UpDownloadCard.IsVisible = false;
        PART_UpReadyCard.IsVisible = false;
        PART_UpFailedCard.IsVisible = false;

        // 右上角主操作按钮
        PART_BtnTriggerCheck.IsVisible = false;
        PART_BtnUpSecondary.IsVisible = false;
        PART_BtnUpPrimary.Content = "立即更新";
        PART_BtnUpPrimary.IsVisible = true;

        PART_UpdateBadgeDot.IsVisible = true;
    }

    private void SetUpdateDownloadingState(string fileName)
    {
        _updateFlowState = UpdateFlowState.Downloading;
        PART_UpdateBadgeBorder.Background = Brush.Parse("#1A0EA5E9");
        PART_UpdateBadgeBorder.BorderBrush = Brush.Parse("#330EA5E9");
        HideAllUpdateIcons();
        PART_UpdateIconDownloading.IsVisible = true;
        StopUpdateBadgeSpin();

        PART_TxtUpdateTitle.Text = "正在下载更新包...";
        PART_TxtUpdateSubtitle.Text = "正在从官方服务器下载最新安装包，请稍候";

        PART_UpdateVersionPill.IsVisible = false;
        PART_UpdateReleaseCard.IsVisible = false;
        PART_UpReadyCard.IsVisible = false;
        PART_UpFailedCard.IsVisible = false;

        PART_TxtUpPackageName.Text = fileName;
        PART_TxtUpPercent.Text = "0%";
        PART_UpProgressBar.Value = 0;
        PART_TxtUpDownloadSize.Text = "正在建立安全连接...";
        PART_TxtUpDownloadSpeed.Text = string.Empty;
        PART_UpDownloadCard.IsVisible = true;

        // 右上角操作
        PART_BtnTriggerCheck.IsVisible = false;
        PART_BtnUpPrimary.IsVisible = false;
        PART_BtnUpSecondary.Content = "取消下载";
        PART_BtnUpSecondary.IsVisible = true;
    }

    private void SetUpdateReadyState(string savedPath)
    {
        _updateFlowState = UpdateFlowState.ReadyToInstall;
        _downloadedFilePath = savedPath;
        PART_UpdateBadgeBorder.Background = Brush.Parse("#1AF59E0B");
        PART_UpdateBadgeBorder.BorderBrush = Brush.Parse("#33F59E0B");
        HideAllUpdateIcons();
        PART_UpdateIconReady.IsVisible = true;
        StopUpdateBadgeSpin();

        PART_TxtUpdateTitle.Text = "更新包已准备就绪";
        PART_TxtUpdateSubtitle.Text = "新版本已成功下载并校验完毕";

        PART_UpdateVersionPill.IsVisible = false;
        PART_UpdateReleaseCard.IsVisible = false;
        PART_UpDownloadCard.IsVisible = false;
        PART_UpFailedCard.IsVisible = false;

        PART_TxtUpSavedPath.Text = savedPath;
        PART_UpReadyCard.IsVisible = true;

        // 右上角主操作：立即重启升级
        PART_BtnTriggerCheck.IsVisible = false;
        PART_BtnUpSecondary.IsVisible = false;
        PART_BtnUpPrimary.Content = "立即重启升级";
        PART_BtnUpPrimary.IsVisible = true;
    }

    private void SetUpdateFailedState(string errorMessage)
    {
        _updateFlowState = UpdateFlowState.Failed;
        PART_UpdateBadgeBorder.Background = Brush.Parse("#1AEF4444");
        PART_UpdateBadgeBorder.BorderBrush = Brush.Parse("#33EF4444");
        HideAllUpdateIcons();
        PART_UpdateIconFailed.IsVisible = true;
        StopUpdateBadgeSpin();

        PART_TxtUpdateTitle.Text = "更新操作遇到问题";
        PART_TxtUpdateSubtitle.Text = "未能完成更新检查或安装包下载";

        PART_UpdateVersionPill.IsVisible = false;
        PART_UpdateReleaseCard.IsVisible = false;
        PART_UpDownloadCard.IsVisible = false;
        PART_UpReadyCard.IsVisible = false;

        PART_TxtUpFailedDetails.Text = errorMessage;
        PART_UpFailedCard.IsVisible = true;

        PART_BtnUpSecondary.IsVisible = false;
        PART_BtnUpPrimary.IsVisible = false;
        PART_BtnTriggerCheck.IsVisible = true;
        PART_BtnTriggerCheck.IsEnabled = true;
    }

    private async Task StartDownloadInPanelAsync()
    {
        if (_currentRelease is null || string.IsNullOrWhiteSpace(_currentRelease.UpdateUrl))
        {
            SetUpdateFailedState("未找到有效的更新下载链接。");
            return;
        }

        var updateUrl = _currentRelease.UpdateUrl;
        if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out var packageUri) ||
            packageUri.Scheme != Uri.UriSchemeHttps)
        {
            SetUpdateFailedState("更新包链接不符合安全标准（必须使用 HTTPS）。");
            return;
        }

        var ext = OperatingSystem.IsMacOS() ? ".zip" : OperatingSystem.IsWindows() ? ".msi" : ".zip";
        var fileName = Path.GetFileName(packageUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
        {
            fileName = $"ChuImage-{_currentRelease.Version}{ext}";
        }

        var downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloadsDir)) downloadsDir = Path.GetTempPath();

        var targetPath = Path.Combine(downloadsDir, fileName);
        var tempFile = targetPath + ".download-" + Guid.NewGuid().ToString("N");

        CancelDownload();
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;

        SetUpdateDownloadingState(fileName);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new IOException("下载请求被重定向到了非安全链接。");

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            if (totalBytes > 1024L * 1024 * 1024) throw new IOException("安装包大小超出合理限制（> 1GB）。");

            await using (var stream = await response.Content.ReadAsStreamAsync(token))
            await using (var fileStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                var stopwatch = Stopwatch.StartNew();
                long lastReadForSpeed = 0;
                var lastSpeedCheck = stopwatch.Elapsed;

                while ((bytesRead = await stream.ReadAsync(buffer, token)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > 1024L * 1024 * 1024) throw new IOException("下载数据大小超出安全限制。");

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);

                    var now = stopwatch.Elapsed;
                    var elapsedSec = (now - lastSpeedCheck).TotalSeconds;
                    var speedText = string.Empty;
                    if (elapsedSec >= 0.5)
                    {
                        var bytesDiff = totalRead - lastReadForSpeed;
                        var speedBps = bytesDiff / elapsedSec;
                        speedText = $"{FormatBytes(speedBps)}/s";
                        lastReadForSpeed = totalRead;
                        lastSpeedCheck = now;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (totalBytes > 0)
                        {
                            var pct = (int)Math.Clamp((totalRead * 100.0) / totalBytes, 0, 100);
                            PART_UpProgressBar.Value = pct;
                            PART_TxtUpPercent.Text = $"{pct}%";
                            PART_TxtUpDownloadSize.Text = $"{FormatBytes(totalRead)} / {FormatBytes(totalBytes)}";
                        }
                        else
                        {
                            PART_UpProgressBar.IsIndeterminate = true;
                            PART_TxtUpPercent.Text = string.Empty;
                            PART_TxtUpDownloadSize.Text = FormatBytes(totalRead);
                        }

                        if (!string.IsNullOrEmpty(speedText))
                        {
                            PART_TxtUpDownloadSpeed.Text = speedText;
                        }
                    }, DispatcherPriority.Background);
                }
            }

            File.Move(tempFile, targetPath, true);
            SetUpdateReadyState(targetPath);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
            if (_currentRelease is not null) SetUpdateAvailableState(_currentRelease);
            else SetUpdateNoUpdateState(null);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
            SetUpdateFailedState($"下载更新包失败：{ex.Message}");
        }
    }

    private async Task InstallAndRestartFromPanelAsync()
    {
        if (string.IsNullOrWhiteSpace(_downloadedFilePath) || !File.Exists(_downloadedFilePath) || _currentRelease is null)
        {
            SetUpdateFailedState("安装包文件不存在，请重新下载。");
            return;
        }

        try
        {
            if (App.MainWindow is MainWindow mainWindow)
            {
                _ = await mainWindow.CaptureAndSaveConfigAsync();
            }

            PART_TxtUpdateTitle.Text = "正在准备重启升级...";
            PART_TxtUpdateSubtitle.Text = "ChuImage 将在几秒内退出并完成安装升级，请稍候";
            PART_BtnUpPrimary.IsEnabled = false;

            var result = await Core.UpdateProvider.ApplyAndRestartAsync(_downloadedFilePath, _currentRelease, CancellationToken.None);
            if (!result.IsSuccess && !result.IsSkipped)
            {
                PART_BtnUpPrimary.IsEnabled = true;
                SetUpdateFailedState($"执行升级失败：{result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            PART_BtnUpPrimary.IsEnabled = true;
            SetUpdateFailedState($"执行升级异常：{ex.Message}");
        }
    }

    private void CancelDownload()
    {
        if (_downloadCts is not null)
        {
            try
            {
                _downloadCts.Cancel();
                _downloadCts.Dispose();
            }
            catch { }
            _downloadCts = null;
        }
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:F0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private void HideAllUpdateIcons()
    {
        PART_UpdateIconChecking.IsVisible = false;
        PART_UpdateIconNoUpdate.IsVisible = false;
        PART_UpdateIconAvailable.IsVisible = false;
        PART_UpdateIconDownloading.IsVisible = false;
        PART_UpdateIconReady.IsVisible = false;
        PART_UpdateIconFailed.IsVisible = false;
    }

    private void StartUpdateBadgeSpin()
    {
        StopUpdateBadgeSpin();
        _updateBadgeSpinAngle = 0;
        _updateBadgeSpinTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            _updateBadgeSpinAngle = (_updateBadgeSpinAngle + 6) % 360;
            if (PART_UpdateIconChecking.RenderTransform is not RotateTransform rt)
            {
                rt = new RotateTransform();
                PART_UpdateIconChecking.RenderTransform = rt;
            }
            rt.Angle = _updateBadgeSpinAngle;
        });
        _updateBadgeSpinTimer.Start();
    }

    private void StopUpdateBadgeSpin()
    {
        _updateBadgeSpinTimer?.Stop();
        _updateBadgeSpinTimer = null;
    }

    #endregion // Software Update In-Panel Flow


    #region UI Event Handlers

    private void Backdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_updateFlowState == UpdateFlowState.Downloading) return;
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

    private async void TriggerCheck_Click(object? sender, RoutedEventArgs e)
    {
        await CheckForUpdateInPanelAsync();
    }

    private async void UpPrimary_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateFlowState == UpdateFlowState.UpdateAvailable)
        {
            await StartDownloadInPanelAsync();
        }
        else if (_updateFlowState == UpdateFlowState.ReadyToInstall)
        {
            await InstallAndRestartFromPanelAsync();
        }
    }

    private void UpSecondary_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateFlowState == UpdateFlowState.Downloading)
        {
            CancelDownload();
        }
    }

    private async void UpChangelog_Click(object? sender, RoutedEventArgs e)
    {
        var url = _currentRelease?.ChangelogUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            await BHelper.OpenUrlAsync(TopLevel.GetTopLevel(this) as PhWindow, url, "from_about_update");
        }
    }

    private void UpOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_downloadedFilePath) || !File.Exists(_downloadedFilePath)) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{_downloadedFilePath}\"");
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start("explorer.exe", $"/select,\"{_downloadedFilePath}\"");
            }
            else
            {
                var dir = Path.GetDirectoryName(_downloadedFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Process.Start("xdg-open", $"\"{dir}\"");
                }
            }
        }
        catch { }
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
        // 点击关于界面各处的检查更新，直接切换至第一位 Tab 并在面板内运行检查
        SwitchTab("update");
        await CheckForUpdateInPanelAsync();
    }

    #endregion // UI Event Handlers
}
