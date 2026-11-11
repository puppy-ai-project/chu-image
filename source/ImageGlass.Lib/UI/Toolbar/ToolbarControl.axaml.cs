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
using Avalonia.Interactivity;
using Avalonia.Svg.Skia;
using ImageGlass.Common;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace ImageGlass.UI;


public partial class ToolbarControl : PhControl
{
    private async void SettingsButton_Click(object? sender, RoutedEventArgs e) =>
        await Core.API.RunApiAsync(API.IG_OpenSettings);
    private void ImageInfo_Click(object? sender, RoutedEventArgs e) => App.MainWindow.ToggleImageInfoWindow();
    private void FitWindow_Click(object? sender, RoutedEventArgs e) => App.MainWindow.FitWindowToImage();
    private void UpdateFitWindowButton() => PART_BtnFitWindow.IsEnabled = App.MainWindow.CanFitWindowToImage;
    private void Viewer_RenderStateChanged(Viewer.ViewerControl sender, EventArgs e) => UpdateFitWindowButton();
    private void Viewer_DisplayRotationChanged(object? sender, EventArgs e) => UpdateFitWindowButton();
    private void MainWindow_FitSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateFitWindowButton();
    private void MainWindow_FitPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) UpdateFitWindowButton();
    }

    public ToolbarControlModel VM => (ToolbarControlModel)DataContext!;


    // events
    public event TEventHandler<object, ToolbarItemClickEventArgs>? ItemClicked;

    private readonly Dictionary<string, List<int>> _configBindingsMap = [];
    private readonly Dictionary<int, ToolbarItemMetadata> _metadataMap = [];
    public readonly List<ToolbarItemModel> _groupPrimaryItemModels = [];
    public readonly List<ToolbarItemModel> _groupSecondaryItemModels = [];
    public readonly List<ToolbarItemModel> _groupOverflowItemModels = [];
    private readonly List<Control> _itemElements = [];
    private double _lastOverflowWidth;
    private bool _overflowUpdateQueued;




    #region Public Properties

    /// <summary>
    /// Gets, sets items source of the toolbar.
    /// </summary>
    public ObservableCollection<ToolbarItemModel> ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
    public static readonly StyledProperty<ObservableCollection<ToolbarItemModel>> ItemsSourceProperty =
        AvaloniaProperty.Register<ToolbarControl, ObservableCollection<ToolbarItemModel>>(nameof(ItemsSource), []);


    /// <summary>
    /// Gets, sets tooltip placement of toolbar items.
    /// </summary>
    public PlacementMode ItemTooltipPlacement
    {
        get => GetValue(ItemTooltipPlacementProperty);
        set => SetValue(ItemTooltipPlacementProperty, value);
    }
    public static readonly StyledProperty<PlacementMode> ItemTooltipPlacementProperty =
        AvaloniaProperty.Register<ToolbarControl, PlacementMode>(nameof(ItemTooltipPlacement), PlacementMode.Bottom);

    #endregion // Public Properties



    public ToolbarControl()
    {
        InitializeComponent();
    }



    #region Control Events

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        Core.Config.PropertyChanged += Config_PropertyChanged;
        App.MainWindow.PART_MainView.PART_Viewer.RenderStateChanged += Viewer_RenderStateChanged;
        App.MainWindow.PART_MainView.PART_Viewer.DisplayRotationChanged += Viewer_DisplayRotationChanged;
        App.MainWindow.SizeChanged += MainWindow_FitSizeChanged;
        App.MainWindow.PropertyChanged += MainWindow_FitPropertyChanged;
        UpdateFitWindowButton();

        RefreshPendingUpdateState();
        ScheduleOverflowUpdate();
    }


    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Core.Config.PropertyChanged -= Config_PropertyChanged;
        App.MainWindow.PART_MainView.PART_Viewer.RenderStateChanged -= Viewer_RenderStateChanged;
        App.MainWindow.PART_MainView.PART_Viewer.DisplayRotationChanged -= Viewer_DisplayRotationChanged;
        App.MainWindow.SizeChanged -= MainWindow_FitSizeChanged;
        App.MainWindow.PropertyChanged -= MainWindow_FitPropertyChanged;
    }


    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // skip overflow recalculation when only the height changed
        if (Math.Abs(e.NewSize.Width - _lastOverflowWidth) < 0.5) return;
        _lastOverflowWidth = e.NewSize.Width;

        ScheduleOverflowUpdate();
    }


    /// <summary>
    /// Coalesces overflow recalculation into a single post-layout pass, so bursts of size changes
    /// (startup, DPI settle, full-screen enter/exit) run <see cref="HandleOverflow"/> once with the
    /// final, settled bounds instead of flickering through intermediate states.
    /// </summary>
    private void ScheduleOverflowUpdate()
    {
        if (_overflowUpdateQueued) return;
        _overflowUpdateQueued = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _overflowUpdateQueued = false;
            HandleOverflow();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }


    protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == ItemsSourceProperty)
        {
            LoadItems();
        }
        else if (e.Property == ItemTooltipPlacementProperty)
        {
            UpdateItemTooltipPlacement();
        }
    }


    protected override void OnIgLanguageChanged()
    {
        base.OnIgLanguageChanged();

        RefreshLanguage();
    }


    private void Config_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 1. Toolbar buttons
        // update toolbar spacing
        if (nameof(Core.Config.ToolbarIconHeight).Equals(e.PropertyName))
        {
            _ = VM.OnPropertyChanged(nameof(VM.IconSize));
            _ = VM.OnPropertyChanged(nameof(VM.ItemSpacing));
            _ = VM.OnPropertyChanged(nameof(VM.ItemPadding));
        }

        // update toolbar button check state
        else
        {
            UpdateButtonCheckState(e.PropertyName);
        }


        // the badge must appear the moment a download finishes, not on the next menu open
        if (nameof(Core.Config.UpdatePendingVersion).Equals(e.PropertyName))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPendingUpdateState);
        }


        // 2. Main menu items
        if (nameof(Core.Config.ZoomMode).Equals(e.PropertyName))
        {
            _ = VM.OnPropertyChanged(nameof(ToolbarItemModel.IsZoomModeAutoZoom));
        }
    }


    private void ToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToolbarButton btn) return;

        ItemClicked?.Invoke(btn, new ToolbarItemClickEventArgs(btn.VM));
    }


    private void ToolbarItem_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Control.BoundsProperty) return;
        if (e.NewValue is not Rect bounds) return;
        if (bounds.Width == 0 || bounds.Height == 0) return;
        if (sender is not IToolbarItem item) return;

        // save toolbar item width
        if (_metadataMap.TryGetValue(item.VM.SourceIndex, out var meta))
        {
            meta.RenderedWidth = bounds.Width;
        }
    }


    private void PART_BtnOverflowMenu_DropdownOpened(ContextMenu sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu mnu) return;
        mnu.Items.Clear();


        foreach (var item in _groupOverflowItemModels)
        {
            // 1. Separator
            if (item.IsSeparator)
            {
                mnu.Items.Add(new PhMenuItem() { Header = "-" });
                continue;
            }


            // 2. Button item
            // an inert button (no click action) has nothing to run: leave it out of the menu entirely
            if (!item.HasClickAction) continue;

            // get toolbar item metadata
            var mnuItem = new PhMenuItem
            {
                ToggleType = item.IsToggle ? MenuItemToggleType.CheckBox : MenuItemToggleType.None,
                IsChecked = item.IsChecked,
                DataContext = item, // save VM to data context
            };

            // get icon
            if (!string.IsNullOrEmpty(item.ImagePath))
            {
                try
                {
                    mnuItem.Icon = new Image
                    {
                        Source = new SvgImage
                        {
                            Source = SvgSource.Load(item.ImagePath),
                        },
                    };
                }
                catch { }
            }

            // no icon: the same hatch placeholder the toolbar button shows
            else if (item.IsPlaceholderIconVisible)
            {
                mnuItem.Icon = new PathIcon
                {
                    Opacity = 0.6,
                    Data = Resx.GetIcon(ResxIconId.IconPlaceholder),
                };
            }

            // get display text
            mnuItem.Header = item.DisplayText;
            mnuItem.HotkeyText = item.HotkeyText;

            // add click event
            mnuItem.Click += OverflowMenuItem_Click;

            mnu.Items.Add(mnuItem);
        }
    }


    private void OverflowMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mnu) return;
        if (mnu.DataContext is not ToolbarItemModel vm) return;

        ItemClicked?.Invoke(mnu, new ToolbarItemClickEventArgs(vm));
    }


    private void PART_BtnMainMenu_DropdownOpened(ContextMenu sender, RoutedEventArgs e)
    {
        RefreshMainMenuState();
    }


    private void MainMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not PhMenuItem mnu) return;

        var action = AppAPIProvider.GetMenuAction(mnu.LangKey);
        _ = Core.API.RunActionAsync(action, mnu.CommandParameter?.ToString());
    }


    public async void MainMenu_ViewChannelItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not PhMenuItem mnu) return;

        var channelType = mnu.CommandParameter?.ToString();
        if (string.IsNullOrEmpty(channelType)) return;

        if (Enum.TryParse<ColorChannels>(channelType, out var channels))
        {
            // toggle a channel
            if (mnu.ToggleType == MenuItemToggleType.CheckBox)
            {
                await SetColorChannelsAsync(channels, mnu.IsChecked);
            }

            // set channels
            else
            {
                await SetColorChannelsAsync(channels, null);
            }
        }
    }


    #endregion // Control Events



    #region Methods

    /// <summary>
    /// Clears all items and metadata.
    /// </summary>
    private void ClearItems()
    {
        // remove item events
        foreach (var item in _itemElements)
        {
            item.PropertyChanged -= ToolbarItem_PropertyChanged;

            if (item is ToolbarButton itemBtn)
            {
                itemBtn.Click -= ToolbarButton_Click;
            }
        }

        _itemElements.Clear();
        _metadataMap.Clear();
        _configBindingsMap.Clear();

        _groupPrimaryItemModels.Clear();
        _groupOverflowItemModels.Clear();
        _groupSecondaryItemModels.Clear();

        PART_PrimaryGroup.Children.Clear();
        PART_SecondaryGroup.Children.Clear();
    }


    /// <summary>
    /// Loads toolbar items.
    /// </summary>
    private void LoadItems()
    {
        ClearItems();

        var primaryList = new List<Control>();
        var secondaryList = new List<Control>();

        int srcIndex = -1;
        int primaryIndex = -1;
        int secondaryIndex = -1;

        var items = ItemsSource.ToList();
        if (!items.Any(item => item.Id == "Btn_Delete"))
        {
            var delete = Config.BuiltInToolbarItems.First(item => item.Id == "Btn_Delete");
            items.Add(delete);
        }
        foreach (var vm in items)
        {
            srcIndex++;
            vm.SourceIndex = srcIndex;

            // Keep these tools hidden even when they are present in a saved layout.
            if (vm.Id is "Btn_Crop" or "Btn_Slideshow" or "Btn_ColorPicker"
                or "Btn_ZoomIn" or "Btn_ZoomOut" or "Btn_ActualSize") continue;


            // create toolbar item element
            Control itemEl;
            if (vm.IsSeparator) itemEl = new ToolbarSeparator();
            else
            {
                var itemBtn = new ToolbarButton();
                itemBtn.IsChecked = ComputeCheckState(vm);

                // a button with no click action is inert: hit-testing off also kills hover/press/tooltip
                itemBtn.IsEnabled = itemBtn.IsHitTestVisible = vm.HasClickAction;

                itemBtn.Click += ToolbarButton_Click;
                itemEl = itemBtn;
            }

            itemEl.Focusable = false;
            itemEl.DataContext = vm;
            itemEl.PropertyChanged += ToolbarItem_PropertyChanged;


            // group: secondary
            if (vm.Alignment == ToolbarItemAlignment.Right)
            {
                secondaryIndex++;
                _groupSecondaryItemModels.Add(vm);
                secondaryList.Add(itemEl);
            }
            // group: primary
            else
            {
                primaryIndex++;
                _groupPrimaryItemModels.Add(vm);
                primaryList.Add(itemEl);
            }
            _itemElements.Add(itemEl);


            // save item metadata
            _metadataMap.TryAdd(srcIndex, new ToolbarItemMetadata()
            {
                SourceIndex = srcIndex,
                PrimaryItemIndex = primaryIndex,
                SecondaryItemIndex = secondaryIndex,
                RenderedWidth = 0,
            });


            // save binding map
            var bindingIndice = _configBindingsMap.GetValueOrDefault(vm.ConfigBinding) ?? [];
            if (!string.IsNullOrWhiteSpace(vm.ConfigBinding))
            {
                bindingIndice.Add(srcIndex);
                _configBindingsMap[vm.ConfigBinding] = bindingIndice;
            }


            // set item check state
            if (!string.IsNullOrWhiteSpace(vm.ConfigBinding))
            {
                vm.IsChecked = ComputeCheckState(vm);
            }
        }


        var cropIndex = ItemsSource.ToList().FindIndex(item => item.Id == "Btn_Crop");
        var afterCrop = cropIndex >= 0;
        foreach (var item in primaryList.OfType<ToolbarButton>())
        {
            if (afterCrop && item.VM.SourceIndex > cropIndex) item.SetIconSize(14);
        }
        if (afterCrop)
        {
            foreach (var item in secondaryList.OfType<ToolbarButton>()) item.SetIconSize(14);
            PART_BtnMainMenu.SetIconSize(14);
        }

        // A hidden final tool must not leave a trailing separator before the info button.
        while (primaryList.Count > 0 && primaryList[^1] is ToolbarSeparator)
            primaryList.RemoveAt(primaryList.Count - 1);

        // append to visual tree
        PART_PrimaryGroup.Children.AddRange(primaryList);
        PART_SecondaryGroup.Children.AddRange(secondaryList);

        // set tooltip placement
        UpdateItemTooltipPlacement();
    }


    /// <summary>
    /// Ensures all toolbar items remain visible on the title bar without collapsing into the overflow menu.
    /// </summary>
    private void HandleOverflow()
    {
        var primaryCount = _groupPrimaryItemModels.Count;
        if (primaryCount == 0) return;

        // Keep all items fully visible
        foreach (var item in _groupPrimaryItemModels)
        {
            item.IsNotOverflow = true;
        }
        _groupOverflowItemModels.Clear();
        PART_BtnOverflowMenu.IsVisible = false;

        PART_PrimaryIsland.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        PART_PrimaryGroup.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        PART_PrimaryIsland.IsVisible = true;
    }


    /// <summary>
    /// Updates check state of toolbar button according to config name.
    /// </summary>
    private void UpdateButtonCheckState(string? configName)
    {
        if (string.IsNullOrWhiteSpace(configName)) return;
        if (_configBindingsMap.GetValueOrDefault(configName) is not List<int> itemIndice) return;

        var items = ItemsSource;
        if (items is null || items.Count == 0) return;

        foreach (var srcIndex in itemIndice)
        {
            if ((uint)srcIndex < (uint)items.Count)
            {
                items[srcIndex].IsChecked = ComputeCheckState(items[srcIndex]);
            }
        }
    }


    /// <summary>
    /// Computes the check state of the input view model.
    /// </summary>
    private static bool ComputeCheckState(ToolbarItemModel vm)
    {
        var configValue = Core.Config.GetAsString(vm.ConfigBinding);
        var configBindingValue = vm.ConfigBindingValue;
        var configBindingValueEqual = true;

        if (vm.ConfigBindingValue.StartsWith('!'))
        {
            configBindingValue = vm.ConfigBindingValue.Substring(1);
            configBindingValueEqual = false;
        }

        var isChecked = configValue.Equals(configBindingValue, StringComparison.Ordinal);
        if (!configBindingValueEqual) isChecked = !isChecked;

        return isChecked;
    }


    /// <summary>
    /// Re-resolves every localized string owned by the toolbar after the app language changed:
    /// item texts/tooltips, the menu buttons, and the main-menu items. The menu items are localized
    /// explicitly because they self-localize only while loaded, and their popup may never have been
    /// opened (always the case on macOS, where the menu is shown through the native menu bar).
    /// </summary>
    private void RefreshLanguage()
    {
        foreach (var vm in ItemsSource)
        {
            vm.RefreshLanguage();
        }

        if (DataContext is ToolbarControlModel model) model.RefreshLanguage();

        LocalizeMenuText(PART_MainMenu.Items);

        // push the new text onto the macOS menu bar (no-op on other platforms)
        SyncNativeStates();
    }


    /// <summary>
    /// Re-localizes menu items and their submenus.
    /// </summary>
    private static void LocalizeMenuText(ItemCollection items)
    {
        foreach (var item in items)
        {
            if (item is not PhMenuItem mnuItem) continue;

            mnuItem.LocalizeText();

            if (mnuItem.Items.Count > 0)
            {
                LocalizeMenuText(mnuItem.Items);
            }
        }
    }


    /// <summary>
    /// Refreshes the hotkey text of every main-menu item from the current config. Runs on each menu
    /// open so edits made in the Keyboard settings page (or any hotkey change) show immediately.
    /// </summary>
    private void UpdateMenuTextIfNeeded()
    {
        if (PART_MainMenu.Items is not ItemCollection items) return;

        LoadMenuText(items);
    }


    /// <summary>
    /// Loads menu text.
    /// </summary>.
    private static void LoadMenuText(ItemCollection items)
    {
        foreach (var item in items)
        {
            if (item is not PhMenuItem mnuItem) continue;

            // load submenu items
            if (mnuItem.Items.Count > 0)
            {
                LoadMenuText(mnuItem.Items);
            }

            // update hotkey text for menu items
            mnuItem.HotkeyText = AppAPIProvider.GetMenuHotkeyText(mnuItem.LangKey);
        }
    }


    /// <summary>
    /// Updates the tooltip placement and vertical offset for the main menu button,
    /// overflow menu, and all item elements
    /// based on the current item tooltip placement setting.
    /// </summary>
    private void UpdateItemTooltipPlacement()
    {
        // calculate vertical offset
        var vOffset = 0;
        if (ItemTooltipPlacement is PlacementMode.Bottom
            or PlacementMode.BottomEdgeAlignedLeft
            or PlacementMode.BottomEdgeAlignedRight)
        {
            vOffset = 8;
        }

        // update placement and offset
        ToolTip.SetPlacement(PART_BtnMainMenu, ItemTooltipPlacement);
        ToolTip.SetVerticalOffset(PART_BtnMainMenu, vOffset);

        ToolTip.SetPlacement(PART_OverflowMenu, ItemTooltipPlacement);
        ToolTip.SetVerticalOffset(PART_OverflowMenu, vOffset);

        foreach (var itemEl in _itemElements)
        {
            ToolTip.SetPlacement(itemEl, ItemTooltipPlacement);
            ToolTip.SetVerticalOffset(itemEl, vOffset);
        }
    }


    private async Task SetColorChannelsAsync(ColorChannels channels, bool? isEnabled)
    {
        var newChannels = Core.ColorChannels;

        // toggle a channel
        if (isEnabled is not null)
        {
            if (IsEnabled)
            {
                newChannels ^= channels;
            }
            else
            {
                newChannels |= channels;
            }
        }

        // set channels
        else
        {
            newChannels = channels;
        }

        _ = await Core.API.RunApiAsync(API.IG_SetColorChannels, newChannels.ToString());
    }


    /// <summary>
    /// Rebuilds external tool entries in the Tools submenu dynamically.
    /// Items are inserted before <see cref="PART_MnuExternalToolsEndSeparator"/>.
    /// </summary>
    private void BuildExternalToolMenuItems()
    {
        var items = PART_MnuTools.Items;
        var sepEndIndex = items.IndexOf(PART_MnuExternalToolsEndSeparator);
        if (sepEndIndex < 0) return;

        // remove previously added external tool entries (tagged with our marker)
        var toRemove = items
            .OfType<PhMenuItem>()
            .Where(m => m.Tag is string tag && tag == "external_tool")
            .ToList();
        foreach (var m in toRemove) items.Remove(m);

        // re-query separator index after removal
        sepEndIndex = items.IndexOf(PART_MnuExternalToolsEndSeparator);

        var tools = Core.ToolRegistry.GetAllExternalToolManifests().ToList();
        if (tools.Count == 0) return;

        // show the separator that visually separates built-ins from external tools
        PART_MnuExternalToolsEndSeparator.IsVisible = true;

        // insert one menu item per external tool, before the separator
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];

            // build hotkey display text from tool's configured hotkeys
            var hotkeyText = tool.Hotkeys.Length > 0
                ? string.Join(", ", tool.Hotkeys.Select(hk => hk.KeyString))
                : string.Empty;

            var mnu = new PhMenuItem
            {
                Header = string.IsNullOrEmpty(tool.ToolName) ? tool.ToolId : tool.ToolName,
                HotkeyText = hotkeyText,
                Tag = "external_tool",
                CommandParameter = tool.ToolId,
            };
            mnu.Click += async (_, _) => await Core.API.RunApiAsync(API.IG_OpenTool, tool.ToolId);
            items.Insert(sepEndIndex + i, mnu);
        }
    }


    /// <summary>
    /// Refreshes the dynamic state of the main menu (localized text, editing app name,
    /// per-format enablement, and external tool entries) just before it is shown.
    /// Shared by the in-app dropdown menu and the macOS native menu.
    /// </summary>
    public void RefreshMainMenuState()
    {
        UpdateMenuTextIfNeeded();

        // 1. update editing app name
        EditingApp.UpdateAppNameForMenuEdit(PART_MnuEdit);

        // 2. update per-format enablement of menu items
        UpdateMenuItemEnableStates();

        // 3. rebuild external tool entries in the Tools submenu
        BuildExternalToolMenuItems();
    }


    /// <summary>
    /// Updates the enabled state of format-dependent menu items (animated and multi-frame).
    /// Separated out so the macOS native menu can reuse it without the structural
    /// external-tool rebuild (which must not run while AppKit is iterating the menu).
    /// </summary>
    private void UpdateMenuItemEnableStates()
    {
        // animated format
        var isAnimator = Core.Photos.Current?.Bitmap is AnimatorImpl;
        PART_MnuToggleImageAnimation.IsEnabled = isAnimator;
        PART_MnuViewChannels.IsEnabled
            = PART_MnuInvertColors.IsEnabled
            = PART_MnuRotateLeft.IsEnabled
            = PART_MnuRotateRight.IsEnabled
            = PART_MnuFlipHorizontal.IsEnabled
            = PART_MnuFlipVertical.IsEnabled
            = !isAnimator;

        // multi-frame format
        var hasMultiFrames = Core.Photos.CurrentMetadata?.FrameCount > 1;
        PART_MnuExportFrames.IsEnabled
            = PART_MnuViewNextFrame.IsEnabled
            = PART_MnuViewPreviousFrame.IsEnabled
            = PART_MnuViewFirstFrame.IsEnabled
            = PART_MnuViewLastFrame.IsEnabled
            = hasMultiFrames;

        // Pro licensing: one item per state; an expired license is managed, not pitched
        var isManagingLicense = Core.IsProEnabled || Core.ExpiredLicense is not null;
        PART_MnuUpgradeLicense.IsVisible = !isManagingLicense;
        PART_MnuManageLicense.IsVisible = isManagingLicense;

        RefreshPendingUpdateState();
    }


    /// <summary>
    /// Shows or hides the "Restart to update" item and the main-menu button badge.
    /// </summary>
    public void RefreshPendingUpdateState()
    {
        var hasUpdate = Core.UpdateProvider.CanApplyPendingUpdate;

        PART_MnuInstallUpdate.IsVisible = hasUpdate;
        PART_MnuInstallUpdateSeparator.IsVisible = hasUpdate;
        PART_UpdateBadge.IsVisible = hasUpdate;
    }


    /// <summary>
    /// Maps each visible leaf menu action to its localized path (e.g. <c>File / Open…</c>), plus the
    /// set of all menu keys (so a hidden item can be told from a non-menu one). For the Keyboard page.
    /// </summary>
    public (Dictionary<LangId, string> Paths, HashSet<LangId> AllKeys) GetMenuActionMap()
    {
        var paths = new Dictionary<LangId, string>();
        var allKeys = new HashSet<LangId>();
        CollectMenuActions(PART_MainMenu.Items, [], true, paths, allKeys);
        return (paths, allKeys);
    }


    private static void CollectMenuActions(ItemCollection items, List<string> parents, bool ancestorsVisible,
        Dictionary<LangId, string> paths, HashSet<LangId> allKeys)
    {
        foreach (var it in items)
        {
            if (it is not PhMenuItem item) continue;

            if (item.LangKey is LangId key) allKeys.Add(key);
            var visible = ancestorsVisible && item.IsVisible;

            // group: descend with its localized label appended to the path
            if (item.Items.Count > 0)
            {
                CollectMenuActions(item.Items, [.. parents, GetNativeHeader(item)], visible, paths, allKeys);
            }
            // visible leaf action
            else if (visible && item.LangKey is LangId leafKey)
            {
                paths[leafKey] = string.Join(" / ", parents.Append(GetNativeHeader(item)));
            }
        }
    }


    #endregion // Methods


}


public class ToolbarItemClickEventArgs(ToolbarItemModel vm) : RoutedEventArgs
{
    public ToolbarItemModel VM => vm;
}


public record ToolbarItemMetadata
{
    public int SourceIndex { get; set; } = -1;
    public int PrimaryItemIndex { get; set; } = -1;
    public int SecondaryItemIndex { get; set; } = -1;
    public double RenderedWidth { get; set; } = 0;
}


