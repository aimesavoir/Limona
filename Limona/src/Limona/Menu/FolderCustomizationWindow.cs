using System.Drawing.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Limona.Interop;
using Limona.Services;
using Windows.Storage.Pickers;

namespace Limona.Menu;

#pragma warning disable CA1416 // Validate platform compatibility

public enum FolderCustomTab
{
    Library,
    Cover,
    Emoji,
    Badge,
    Monogram,
    Pattern
}

/// <summary>
/// Unified advanced folder customization window with 6 dedicated tabs:
///   1. Icon Packs (Gallery & Browse)
///   2. Folder Cover (Personal photo crop, zoom, frames, background)
///   3. Emoji Designer (Categorized emojis, gradients, 3D/glossy/flat, presets)
///   4. Icon Badges (Star, lock, warning, positions, colors, transparent, restore)
///   5. Folder Monogram (Initials suggestion, fonts, crests, ribbons, symbols)
///   6. Background Pattern (Carbon fiber, stripes, dots, honeycomb, mesh, opacity)
/// Features live high-DPI preview, always-on-top floating mode, and persistent icon saving.
/// </summary>
internal sealed class FolderCustomizationWindow : Window
{
    private static FolderCustomizationWindow? s_active;

    private readonly string _folderPath;
    private readonly string _folderName;

    // Navigation
    private FolderCustomTab _currentTab = FolderCustomTab.Library;
    private readonly Dictionary<FolderCustomTab, Button> _tabButtons = new();
    private readonly Dictionary<FolderCustomTab, FrameworkElement> _tabPanels = new();
    private Grid _contentHost = null!;

    // Right-pane preview & actions
    private Image _previewLarge = null!;
    private Image _previewSmall = null!;
    private TextBlock _statusText = null!;
    private Button _applyButton = null!;
    private Button _resetButton = null!;
    private Button _closeButton = null!;
    private byte[]? _pendingIcoBytes;

    // Feature 1: Cover State
    private string? _coverImagePath;
    private float _coverZoom = 1.0f;
    private float _coverOffsetX = 0f;
    private float _coverOffsetY = 0f;
    private CoverFrame _coverFrame = CoverFrame.Rounded;
    private string _coverFolderColor = "#FFD4842F";
    private TextBlock _coverFileLabel = null!;

    // Feature 2: Emoji State
    private string _emojiText = "💼";
    private FolderStyle _emojiStyle = FolderStyle.Depth3D;
    private bool _emojiUseGradient = true;
    private string _emojiColor1 = "#FF3E7BFA";
    private string _emojiColor2 = "#FF1B3D8A";
    private float _emojiSize = 64f;
    private float _emojiOffsetY = 0f;
    private TextBox _emojiInputBox = null!;
    private ComboBox _emojiPresetCombo = null!;

    // Feature 3: Badge State
    private BadgeType _badgeType = BadgeType.Star;
    private BadgePosition _badgePosition = BadgePosition.BottomRight;
    private BadgeSize _badgeSize = BadgeSize.Medium;
    private bool _badgeTransparent = false;
    private string _badgeBgColor = "#FFE5574E";
    private string _badgeFgColor = "#FFFFFFFF";

    // Feature 4: Monogram State
    private string _monogramText = "M";
    private string _monogramFont = "Segoe UI";
    private float _monogramFontSize = 58f;
    private bool _monogramBold = true;
    private TypographyStyle _monogramStyle = TypographyStyle.CircularCrest;
    private DecorativeSymbol _monogramSymbol = DecorativeSymbol.Crown;
    private bool _monogramUseGradient = true;
    private string _monogramColor1 = "#FF181A22";
    private string _monogramColor2 = "#FF0A0B10";
    private string _monogramTextColor = "#FFF5A623";
    private TextBox _monogramInputBox = null!;
    private ComboBox _monogramPresetCombo = null!;

    // Feature 5: Pattern State
    private PatternType _patternType = PatternType.CarbonFiber;
    private PatternScale _patternScale = PatternScale.Medium;
    private float _patternOpacity = 0.55f;
    private PatternArea _patternArea = PatternArea.BodyOnly;
    private string _patternBgColor = "#FF181A22";
    private string _patternFgColor = "#FFF5A623";

    // Feature: Icon Library State
    private ComboBox _libraryCatCombo = null!;
    private StackPanel _libraryGridPanel = null!;
    private List<(string Path, string Pack)> _allIcons = new();
    private string? _librarySelectedPath;
    private Button? _librarySelectedButton;
    private bool _isPreparingIcon;
    private bool _isApplyingIcon;
    private int _previewGeneration;

    public static void ShowForFile(string folderPath, FolderCustomTab tab = FolderCustomTab.Library)
    {
        s_active = ToolWindowCoordinator.Open(() => new FolderCustomizationWindow(folderPath, tab));
    }

    private FolderCustomizationWindow(string folderPath, FolderCustomTab initialTab)
    {
        _folderPath = folderPath;
        _folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _currentTab = initialTab;

        // Auto-seed initials for Monogram
        _monogramText = FolderCustomizationService.SuggestInitials(_folderName);

        WindowFit.SetTitle(this, "Folder Customization — " + _folderName);

        // Always on top as requested
        if (this.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
        }

        Content = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(18, 14, 18, 14),
            Child = BuildMainLayout(),
        };

        WindowFit.Apply(this, (FrameworkElement)Content);
        this.Closed += (_, _) => s_active = null;

        // Initial preview render
        SwitchTab(_currentTab);
    }

    private FrameworkElement BuildMainLayout()
    {
        var mainStack = new StackPanel { Spacing = 12, MinWidth = 740 };

        // Top bar: Window Title + Folder Name
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "Folder Customization",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Brush(MenuTheme.LabelColor),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"“{_folderName}”",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.AccentColor),
        });
        Grid.SetColumn(titleStack, 0);
        headerRow.Children.Add(titleStack);

        mainStack.Children.Add(headerRow);

        // Top Tab Navigation Bar
        mainStack.Children.Add(BuildTabBar());

        // Body: 2 Columns -> Left (Tab Options, Width=490) | Right (Preview & Apply, Width=240)
        var bodyGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(490) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // spacer
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

        // Left Content Host
        _contentHost = new Grid();
        Grid.SetColumn(_contentHost, 0);
        bodyGrid.Children.Add(_contentHost);

        // Right Preview & Actions
        var rightPane = BuildRightPane();
        Grid.SetColumn(rightPane, 2);
        bodyGrid.Children.Add(rightPane);

        mainStack.Children.Add(bodyGrid);
        return mainStack;
    }

    // -------------------------------------------------------------------------
    // TAB BAR NAVIGATION
    // -------------------------------------------------------------------------

    private FrameworkElement BuildTabBar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Background = Brush("#FF14161E"),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
        };

        var tabs = new (FolderCustomTab Tab, string Label)[]
        {
            (FolderCustomTab.Library, "📁 Icon Packs"),
            (FolderCustomTab.Cover, "🖼️ Cover"),
            (FolderCustomTab.Emoji, "😀 Emoji"),
            (FolderCustomTab.Badge, "🏷️ Badges"),
            (FolderCustomTab.Monogram, "🔤 Monogram"),
            (FolderCustomTab.Pattern, "🎨 Pattern")
        };

        foreach (var (tab, label) in tabs)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(6),
                Background = Brush("Transparent"),
                Foreground = Brush(MenuTheme.LabelColor),
            };
            var current = tab;
            btn.Click += (_, _) => SwitchTab(current);
            _tabButtons[tab] = btn;
            bar.Children.Add(btn);
        }

        return bar;
    }

    private void SwitchTab(FolderCustomTab tab)
    {
        _currentTab = tab;

        // Highlight tab buttons
        foreach (var (t, btn) in _tabButtons)
        {
            if (t == tab)
            {
                btn.Background = Brush(MenuTheme.AccentColor);
                btn.Foreground = Brush("#FF000000");
            }
            else
            {
                btn.Background = Brush("Transparent");
                btn.Foreground = Brush(MenuTheme.LabelColor);
            }
        }

        // Lazy build tab panels
        if (!_tabPanels.TryGetValue(tab, out var panel))
        {
            panel = tab switch
            {
                FolderCustomTab.Cover => BuildCoverPanel(),
                FolderCustomTab.Emoji => BuildEmojiPanel(),
                FolderCustomTab.Badge => BuildBadgePanel(),
                FolderCustomTab.Monogram => BuildMonogramPanel(),
                FolderCustomTab.Pattern => BuildPatternPanel(),
                _ => BuildLibraryPanel()
            };
            _tabPanels[tab] = panel;
        }

        _contentHost.Children.Clear();
        _contentHost.Children.Add(panel);
        LocalizationUi.ApplyTo(panel);

        _statusText.Text = "";
        UpdatePreviewAsync();
    }

    // -------------------------------------------------------------------------
    // RIGHT PANE: LIVE PREVIEW & APPLY ACTIONS
    // -------------------------------------------------------------------------

    private FrameworkElement BuildRightPane()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Background = Brush("#FF14161E"),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            BorderBrush = Brush("#FF252836"),
            BorderThickness = new Thickness(1),
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Live Preview",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.HintColor),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // Large 128x128 Preview Box
        var previewBox = new Border
        {
            Width = 140,
            Height = 140,
            Background = Brush("#FF0B0C10"),
            CornerRadius = new CornerRadius(8),
            BorderBrush = Brush("#FF303444"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _previewLarge = new Image
        {
            Width = 120,
            Height = 120,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        previewBox.Child = _previewLarge;
        panel.Children.Add(previewBox);

        // Small 32x32 Explorer Preview
        var smallRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _previewSmall = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform };
        smallRow.Children.Add(_previewSmall);
        smallRow.Children.Add(new TextBlock
        {
            Text = "Explorer view (32px)",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.HintColor),
        });
        panel.Children.Add(smallRow);

        // Status Text
        _statusText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.AccentColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MinHeight = 28,
        };
        panel.Children.Add(_statusText);

        // Primary Action: Apply to Folder
        _applyButton = new Button
        {
            Content = "",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Brush("#FF000000"),
            Background = Brush(MenuTheme.AccentColor),
            Padding = new Thickness(14, 9, 14, 9),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        LocalizationUi.SetContent(_applyButton, "✓ {0}", LocalizationUi.Localized("Apply to Folder"));
        _applyButton.Click += async (_, _) => await ApplyCurrentIconAsync();
        panel.Children.Add(_applyButton);

        // Reset & Close buttons row
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };

        _resetButton = new Button
        {
            Content = "Default icon",
            FontSize = 11,
            Foreground = Brush(MenuTheme.LabelColor),
            Background = Brush("#FF222530"),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(5),
        };
        _resetButton.Click += (_, _) =>
        {
            FolderService.ResetFolderIcon(_folderPath);
            LocalizationUi.SetText(_statusText, "Restored to default icon ✔");
            UpdatePreviewAsync();
        };

        _closeButton = new Button
        {
            Content = "Close",
            FontSize = 11,
            Foreground = Brush(MenuTheme.LabelColor),
            Background = Brush("#FF222530"),
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(5),
        };
        _closeButton.Click += (_, _) => this.Close();

        actionRow.Children.Add(_resetButton);
        actionRow.Children.Add(_closeButton);
        panel.Children.Add(actionRow);

        return panel;
    }

    private async Task ApplyCurrentIconAsync()
    {
        if (_pendingIcoBytes is null || _pendingIcoBytes.Length == 0)
        {
            LocalizationUi.SetText(_statusText, "No icon generated yet.");
            return;
        }

        _isApplyingIcon = true;
        UpdateApplyButtonState();
        LocalizationUi.SetText(_statusText, "Applying icon to folder…");

        try
        {
            await FolderService.ApplyGeneratedIconAsync(_folderPath, _pendingIcoBytes);
            LocalizationUi.SetText(_statusText, "Icon applied ✓ (Explorer refreshed)");
        }
        catch (Exception ex)
        {
            Dbg.Log("ApplyCurrentIconAsync THREW: " + ex);
            LocalizationUi.SetFormattedText(_statusText, "Failed: {0}", ex.Message.Split('\n')[0]);
        }
        finally
        {
            _isApplyingIcon = false;
            UpdateApplyButtonState();
        }
    }

    private void UpdateApplyButtonState() =>
        _applyButton.IsEnabled = !_isPreparingIcon && !_isApplyingIcon
            && _pendingIcoBytes is { Length: > 0 };

    private void UpdatePreviewAsync()
    {
        var generation = Interlocked.Increment(ref _previewGeneration);
        _pendingIcoBytes = null;
        _isPreparingIcon = true;
        UpdateApplyButtonState();
        try
        {
            System.Drawing.Bitmap bmp;
            byte[] icoBytes;

            switch (_currentTab)
            {
                case FolderCustomTab.Cover:
                    var coverRes = FolderCustomizationService.GenerateCoverIcon(_folderPath, new CoverSettings(
                        _coverImagePath, _coverZoom, _coverOffsetX, _coverOffsetY, _coverFrame, _coverFolderColor));
                    bmp = coverRes.Preview;
                    icoBytes = coverRes.IcoBytes;
                    break;

                case FolderCustomTab.Emoji:
                {
                    // GDI+ renders the emoji font monochrome, so feed the
                    // generator a real color bitmap when one is available.
                    using var colorEmoji = EmojiImages.LoadColor(_emojiText);
                    var emojiRes = FolderCustomizationService.GenerateEmojiIcon(new EmojiSettings(
                        _emojiText, _emojiStyle, _emojiUseGradient, _emojiColor1, _emojiColor2,
                        GradientDirection.Vertical, _emojiSize, 0f, _emojiOffsetY), colorEmoji);
                    bmp = emojiRes.Preview;
                    icoBytes = emojiRes.IcoBytes;

                    // Custom emoji not in the bundled set: fetch it once, then re-render.
                    if (colorEmoji is null && !string.IsNullOrWhiteSpace(_emojiText))
                    {
                        var requested = _emojiText;
                        _ = Task.Run(async () =>
                        {
                            var path = await EmojiImages.EnsureAsync(requested);
                            if (path is not null)
                            {
                                this.DispatcherQueue.TryEnqueue(() => UpdatePreviewAsync());
                            }
                        });
                    }
                    break;
                }

                case FolderCustomTab.Badge:
                    var badgeRes = FolderCustomizationService.GenerateBadgeIcon(_folderPath, new BadgeSettings(
                        _badgeType, _badgePosition, _badgeSize, _badgeTransparent, _badgeBgColor, _badgeFgColor));
                    bmp = badgeRes.Preview;
                    icoBytes = badgeRes.IcoBytes;
                    break;

                case FolderCustomTab.Monogram:
                    var monoRes = FolderCustomizationService.GenerateMonogramIcon(new MonogramSettings(
                        _monogramText, _monogramFont, _monogramFontSize, _monogramBold, false,
                        _monogramStyle, _monogramSymbol, _monogramUseGradient,
                        _monogramColor1, _monogramColor2, _monogramTextColor));
                    bmp = monoRes.Preview;
                    icoBytes = monoRes.IcoBytes;
                    break;

                case FolderCustomTab.Pattern:
                    var patRes = FolderCustomizationService.GeneratePatternIcon(new PatternSettings(
                        _patternType, _patternScale, _patternOpacity, _patternArea, _patternBgColor, _patternFgColor));
                    bmp = patRes.Preview;
                    icoBytes = patRes.IcoBytes;
                    break;

                default: // Library tab
                    if (!string.IsNullOrEmpty(_librarySelectedPath) && File.Exists(_librarySelectedPath))
                    {
                        // Live preview of the icon selected from a pack.
                        bmp = LoadPreviewBitmap(_librarySelectedPath);
                        icoBytes = _pendingIcoBytes
                            ?? FolderService.BuildIconBytesForSource(_librarySelectedPath);
                    }
                    else
                    {
                        // Default folder silhouette for preview
                        bmp = new System.Drawing.Bitmap(256, 256, PixelFormat.Format32bppArgb);
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            FolderCustomizationService.SetupGraphics(g);
                            FolderService.DrawFolderSilhouette(g, FolderCustomizationService.FromArgbHex("#FFD4842F"), 256);
                        }
                        icoBytes = FolderService.BuildMultiSizeIcoFromImage(bmp);
                    }
                    break;
            }

            _pendingIcoBytes = icoBytes;
            ShowPreviewBitmap(bmp, generation);
        }
        catch (Exception ex)
        {
            Dbg.Log("UpdatePreviewAsync THREW: " + ex);
            LocalizationUi.SetFormattedText(_statusText, "Preview failed: {0}", ex.Message.Split('\n')[0]);
        }
        finally
        {
            _isPreparingIcon = false;
            UpdateApplyButtonState();
        }
    }

    /// <summary>Encodes a preview bitmap as PNG and pushes it into both preview images.</summary>
    private void ShowPreviewBitmap(System.Drawing.Bitmap bmp, int generation)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            bytes = ms.ToArray();
        }
        bmp.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                var stream = await ImageConverter.BytesToWinRtStreamAsync(bytes);
                this.DispatcherQueue.TryEnqueue(async () =>
                {
                    if (generation != Volatile.Read(ref _previewGeneration))
                        return;

                    var img = new BitmapImage();
                    await img.SetSourceAsync(stream);
                    if (generation != Volatile.Read(ref _previewGeneration))
                        return;

                    _previewLarge.Source = img;
                    _previewSmall.Source = img;
                });
            }
            catch (Exception ex)
            {
                Dbg.Log("ShowPreviewBitmap THREW: " + ex);
            }
        });
    }

    /// <summary>Loads an icon/image file as a 256 px preview bitmap (largest ico frame scaled).</summary>
    private static System.Drawing.Bitmap LoadPreviewBitmap(string path)
    {
        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            using var icon = new System.Drawing.Icon(path, 256, 256);
            using var frame = icon.ToBitmap();
            var bmp = new System.Drawing.Bitmap(256, 256, PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                FolderCustomizationService.SetupGraphics(g);
                g.DrawImage(frame, 0, 0, 256, 256);
            }
            return bmp;
        }
        return new System.Drawing.Bitmap(path);
    }

    // -------------------------------------------------------------------------
    // TAB 1: ICON PACKS (LIBRARY)
    // -------------------------------------------------------------------------

    private FrameworkElement BuildLibraryPanel()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = "Select an icon from the built-in icon packs or browse an image/ico file:",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
            TextWrapping = TextWrapping.Wrap,
        });

        // Browse Image button
        var browseBtn = MakeButton("", "#FF262A38");
        LocalizationUi.SetContent(browseBtn, "📂 {0}…", LocalizationUi.Localized("Browse Image / ICO"));
        browseBtn.Click += async (_, _) => await OnBrowseFileAsync();
        panel.Children.Add(browseBtn);

        // Discover icons
        _allIcons = DiscoverIcons();

        if (_allIcons.Count > 0)
        {
            var categories = _allIcons
                .Select(i => string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var catRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            catRow.Children.Add(new TextBlock { Text = "Category:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            _libraryCatCombo = new ComboBox { MinWidth = 220, FontSize = 12 };
            _libraryCatCombo.Items.Add($"All Categories ({_allIcons.Count})");
            LocalizationUi.SetComboItem(_libraryCatCombo, 0, "All Categories ({0})", _allIcons.Count);
            foreach (var cat in categories)
            {
                var cnt = _allIcons.Count(i => (string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack).Equals(cat, StringComparison.OrdinalIgnoreCase));
                _libraryCatCombo.Items.Add($"{cat} ({cnt})");
            }
            _libraryCatCombo.SelectedIndex = 0;
            _libraryCatCombo.SelectionChanged += (_, _) => PopulateLibraryGrid();
            catRow.Children.Add(_libraryCatCombo);
            panel.Children.Add(catRow);

            _libraryGridPanel = new StackPanel { Spacing = 6 };
            var scroll = new ScrollViewer
            {
                Content = _libraryGridPanel,
                MaxHeight = 270,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            panel.Children.Add(scroll);
            PopulateLibraryGrid();
        }

        return panel;
    }

    private void PopulateLibraryGrid()
    {
        _libraryGridPanel.Children.Clear();
        if (_allIcons.Count == 0) return;

        var selectedIdx = _libraryCatCombo.SelectedIndex;
        List<(string Path, string Pack)> filtered;
        if (selectedIdx <= 0)
        {
            filtered = _allIcons;
        }
        else
        {
            var selectedText = _libraryCatCombo.SelectedItem as string ?? "";
            var catName = selectedText.Contains(" (") ? selectedText.Substring(0, selectedText.LastIndexOf(" (")) : selectedText;
            filtered = _allIcons.Where(i => (string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack).Equals(catName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        const int perRow = 7;
        for (int i = 0; i < filtered.Count; i += perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (path, _) in filtered.Skip(i).Take(perRow))
            {
                var iconBtn = new Button
                {
                    Content = new Image
                    {
                        Source = new BitmapImage(new Uri(path)),
                        Width = 36,
                        Height = 36,
                        Stretch = Stretch.Uniform,
                    },
                    Padding = new Thickness(3),
                    MinWidth = 46,
                    MinHeight = 46,
                    Background = Brush("#FF1E212D"),
                    BorderBrush = Brush("#FF2F3446"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                };
                ToolTipService.SetToolTip(iconBtn, Path.GetFileName(path));
                var targetFile = path;

                // Keep the selected pack icon highlighted across grid rebuilds.
                if (string.Equals(targetFile, _librarySelectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    iconBtn.BorderBrush = Brush(MenuTheme.AccentColor);
                    iconBtn.BorderThickness = new Thickness(2);
                    _librarySelectedButton = iconBtn;
                }

                iconBtn.Click += async (_, _) => await SelectLibraryIconAsync(targetFile, iconBtn);
                row.Children.Add(iconBtn);
            }
            _libraryGridPanel.Children.Add(row);
        }
    }

    /// <summary>Selects a pack icon: highlights it, renders the live preview, and stages the icon for Apply.</summary>
    private async Task SelectLibraryIconAsync(string path, Button button)
    {
        var generation = Interlocked.Increment(ref _previewGeneration);
        _pendingIcoBytes = null;
        _isPreparingIcon = true;
        UpdateApplyButtonState();
        try
        {
            if (_librarySelectedButton is not null)
            {
                _librarySelectedButton.BorderBrush = Brush("#FF2F3446");
                _librarySelectedButton.BorderThickness = new Thickness(1);
            }
            _librarySelectedButton = button;
            button.BorderBrush = Brush(MenuTheme.AccentColor);
            button.BorderThickness = new Thickness(2);

            _librarySelectedPath = path;
            LocalizationUi.SetFormattedText(_statusText, "Selected: {0}", Path.GetFileName(path));

            var bytes = await Task.Run(() => FolderService.BuildIconBytesForSource(path));
            if (generation != Volatile.Read(ref _previewGeneration) ||
                _currentTab != FolderCustomTab.Library ||
                !string.Equals(path, _librarySelectedPath, StringComparison.OrdinalIgnoreCase))
                return;

            _pendingIcoBytes = bytes;
            ShowPreviewBitmap(LoadPreviewBitmap(path), generation);
        }
        catch (Exception ex)
        {
            Dbg.Log("SelectLibraryIconAsync THREW: " + ex);
            LocalizationUi.SetFormattedText(_statusText, "Preview failed: {0}", ex.Message.Split('\n')[0]);
        }
        finally
        {
            if (generation == Volatile.Read(ref _previewGeneration))
            {
                _isPreparingIcon = false;
                UpdateApplyButtonState();
            }
        }
    }

    private async Task OnBrowseFileAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".ico");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                LocalizationUi.SetText(_statusText, "Applying custom image…");
                await FolderService.SetFolderIconAsync(_folderPath, file.Path);
                LocalizationUi.SetText(_statusText, "Icon applied ✓");
            }
        }
        catch (Exception ex)
        {
            LocalizationUi.SetFormattedText(_statusText, "Picker error: {0}", ex.Message.Split('\n')[0]);
        }
    }

    private static List<(string Path, string Pack)> DiscoverIcons()
    {
        var list = new List<(string Path, string Pack)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dirs = new List<string>();
        var binIcons = Path.Combine(AppContext.BaseDirectory, "Icons");
        if (Directory.Exists(binIcons)) dirs.Add(binIcons);

        // User-drop location: icons added here appear without a rebuild.
        try
        {
            var userIcons = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Limona", "Icons");
            if (Directory.Exists(userIcons) && !dirs.Contains(userIcons, StringComparer.OrdinalIgnoreCase))
                dirs.Add(userIcons);
        }
        catch { }

        try
        {
            var devIcons = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\Icons"));
            if (Directory.Exists(devIcons) && !dirs.Contains(devIcons, StringComparer.OrdinalIgnoreCase))
                dirs.Add(devIcons);
        }
        catch { }

        string[] exts = [".ico", ".png", ".jpg", ".jpeg", ".bmp"];
        foreach (var dir in dirs)
        {
            try
            {
                var dInfo = new DirectoryInfo(dir);
                if (!dInfo.Exists) continue;
                foreach (var file in dInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!exts.Contains(file.Extension.ToLowerInvariant())) continue;
                    if (!seen.Add(file.FullName)) continue;

                    string pack = "";
                    try
                    {
                        var rel = Path.GetRelativePath(dir, file.FullName);
                        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (parts.Length > 1) pack = parts[0];
                    }
                    catch { pack = file.Directory?.Name ?? ""; }

                    list.Add((file.FullName, pack));
                }
            }
            catch { }
        }
        return list;
    }

    // -------------------------------------------------------------------------
    // TAB 2: FOLDER COVER
    // -------------------------------------------------------------------------

    private FrameworkElement BuildCoverPanel()
    {
        var panel = new StackPanel { Spacing = 10 };

        // Select Photo button + label
        var selectRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var pickBtn = MakeButton("", "#FF262A38");
        LocalizationUi.SetContent(pickBtn, "📷 {0}…", LocalizationUi.Localized("Select Cover Image"));
        pickBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _coverImagePath = file.Path;
                _coverFileLabel.Text = file.Name;
                UpdatePreviewAsync();
            }
        };
        selectRow.Children.Add(pickBtn);

        _coverFileLabel = new TextBlock
        {
            Text = "No image chosen",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.HintColor),
        };
        selectRow.Children.Add(_coverFileLabel);
        panel.Children.Add(selectRow);

        // Frame style selector
        var frameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        frameRow.Children.Add(new TextBlock { Text = "Frame Shape:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

        var frameCombo = new ComboBox { MinWidth = 160, FontSize = 12 };
        frameCombo.Items.Add("Rounded Frame");
        frameCombo.Items.Add("Circular Portrait");
        frameCombo.Items.Add("Classic Square");
        frameCombo.Items.Add("Polaroid Photo");
        frameCombo.Items.Add("Bordered Accent");
        frameCombo.Items.Add("Full Pocket Cover");
        frameCombo.SelectedIndex = 0;
        frameCombo.SelectionChanged += (_, _) =>
        {
            _coverFrame = frameCombo.SelectedIndex switch
            {
                1 => CoverFrame.Circle,
                2 => CoverFrame.Square,
                3 => CoverFrame.Polaroid,
                4 => CoverFrame.Bordered,
                5 => CoverFrame.FullPocket,
                _ => CoverFrame.Rounded,
            };
            UpdatePreviewAsync();
        };
        frameRow.Children.Add(frameCombo);
        panel.Children.Add(frameRow);

        // Zoom Slider (0.5x to 3.0x)
        var zoomRow = new StackPanel { Spacing = 2 };
        zoomRow.Children.Add(new TextBlock { Text = "Zoom / Scale:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var zoomSlider = new Slider { Minimum = 50, Maximum = 300, Value = 100, StepFrequency = 5 };
        zoomSlider.ValueChanged += (_, e) =>
        {
            _coverZoom = (float)(e.NewValue / 100.0);
            UpdatePreviewAsync();
        };
        zoomRow.Children.Add(zoomSlider);
        panel.Children.Add(zoomRow);

        // Pan X & Pan Y
        var panRow = new Grid();
        panRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        panRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var panXStack = new StackPanel { Spacing = 2 };
        panXStack.Children.Add(new TextBlock { Text = "Pan X (Horizontal):", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var panXSlider = new Slider { Minimum = -80, Maximum = 80, Value = 0 };
        panXSlider.ValueChanged += (_, e) => { _coverOffsetX = (float)e.NewValue; UpdatePreviewAsync(); };
        panXStack.Children.Add(panXSlider);
        Grid.SetColumn(panXStack, 0);
        panRow.Children.Add(panXStack);

        var panYStack = new StackPanel { Spacing = 2 };
        panYStack.Children.Add(new TextBlock { Text = "Pan Y (Vertical):", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var panYSlider = new Slider { Minimum = -80, Maximum = 80, Value = 0 };
        panYSlider.ValueChanged += (_, e) => { _coverOffsetY = (float)e.NewValue; UpdatePreviewAsync(); };
        panYStack.Children.Add(panYSlider);
        Grid.SetColumn(panYStack, 2);
        panRow.Children.Add(panYStack);

        panel.Children.Add(panRow);

        // Folder Background Color
        panel.Children.Add(new TextBlock { Text = "Folder Base Color:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        panel.Children.Add(BuildColorPaletteRow(hex => { _coverFolderColor = hex; UpdatePreviewAsync(); }));

        return panel;
    }

    // -------------------------------------------------------------------------
    // TAB 3: EMOJI DESIGNER
    // -------------------------------------------------------------------------

    private FrameworkElement BuildEmojiPanel()
    {
        var panel = new StackPanel { Spacing = 10 };

        // Input & Preset Row
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Emoji Text Box
        var inputStack = new StackPanel { Spacing = 2 };
        inputStack.Children.Add(new TextBlock { Text = "Emoji (type or click below):", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        _emojiInputBox = new TextBox { Text = _emojiText, FontSize = 16 };
        _emojiInputBox.TextChanged += (_, _) =>
        {
            _emojiText = _emojiInputBox.Text;
            UpdatePreviewAsync();
        };
        inputStack.Children.Add(_emojiInputBox);
        Grid.SetColumn(inputStack, 0);
        topRow.Children.Add(inputStack);

        // Preset selector + Save
        var presetStack = new StackPanel { Spacing = 2 };
        presetStack.Children.Add(new TextBlock { Text = "Saved Presets:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _emojiPresetCombo = new ComboBox { MinWidth = 140, FontSize = 11 };
        RefreshEmojiPresets();
        _emojiPresetCombo.SelectionChanged += (_, _) =>
        {
            if (_emojiPresetCombo.SelectedItem is string name)
            {
                var p = FolderCustomizationService.LoadEmojiPresets().FirstOrDefault(x => x.Name == name);
                if (p != null)
                {
                    _emojiText = p.Emoji;
                    _emojiInputBox.Text = p.Emoji;
                    _emojiStyle = p.Style;
                    _emojiUseGradient = p.UseGradient;
                    _emojiColor1 = p.Color1;
                    _emojiColor2 = p.Color2;
                    _emojiSize = p.Size;
                    _emojiOffsetY = p.OffsetY;
                    UpdatePreviewAsync();
                }
            }
        };
        presetRow.Children.Add(_emojiPresetCombo);

        var savePresetBtn = MakeButton("", "#FF222530");
        LocalizationUi.SetContent(savePresetBtn, "💾 {0}", LocalizationUi.Localized("Save"));
        savePresetBtn.Click += (_, _) =>
        {
            var pName = $"{_emojiText} Design";
            FolderCustomizationService.SaveEmojiPreset(pName, new EmojiSettings(
                _emojiText, _emojiStyle, _emojiUseGradient, _emojiColor1, _emojiColor2,
                GradientDirection.Vertical, _emojiSize, 0f, _emojiOffsetY));
            RefreshEmojiPresets();
            LocalizationUi.SetFormattedText(_statusText, "Preset '{0}' saved!", pName);
        };
        presetRow.Children.Add(savePresetBtn);
        presetStack.Children.Add(presetRow);

        Grid.SetColumn(presetStack, 2);
        topRow.Children.Add(presetStack);
        panel.Children.Add(topRow);

        // Category Picker & Emojis Scroll
        var catRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        catRow.Children.Add(new TextBlock { Text = "Category:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var catCombo = new ComboBox { MinWidth = 160, FontSize = 12 };
        foreach (var cat in FolderCustomizationService.EmojiCategories.Keys)
            catCombo.Items.Add(cat);
        catCombo.SelectedIndex = 0;

        var emojiGrid = new StackPanel { Spacing = 6 };

        void PopulateCategoryEmojis(string catName)
        {
            emojiGrid.Children.Clear();
            if (FolderCustomizationService.EmojiCategories.TryGetValue(catName, out var emojis))
            {
                const int perRow = 8;
                for (int i = 0; i < emojis.Length; i += perRow)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    foreach (var em in emojis.Skip(i).Take(perRow))
                    {
                        var emBtn = new Button
                        {
                            Content = em,
                            FontSize = 18,
                            Padding = new Thickness(0),
                            MinWidth = 42,
                            MinHeight = 38,
                            Background = Brush("#FF1E212D"),
                            BorderBrush = Brush("#FF2F3446"),
                            CornerRadius = new CornerRadius(5),
                        };
                        var captured = em;
                        emBtn.Click += (_, _) =>
                        {
                            _emojiText = captured;
                            _emojiInputBox.Text = captured;
                            UpdatePreviewAsync();
                        };
                        row.Children.Add(emBtn);
                    }
                    emojiGrid.Children.Add(row);
                }
            }
        }

        catCombo.SelectionChanged += (_, _) =>
        {
            if (catCombo.SelectedItem is string cat) PopulateCategoryEmojis(cat);
        };
        PopulateCategoryEmojis("Faces & Smileys");

        catRow.Children.Add(catCombo);
        panel.Children.Add(catRow);
        // Keep the window compact: the emoji grid scrolls instead of stretching it.
        panel.Children.Add(new ScrollViewer
        {
            Content = emojiGrid,
            MaxHeight = 150,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // Style & Gradient Row
        var styleRow = new Grid();
        styleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        styleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        styleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var styleStack = new StackPanel { Spacing = 2 };
        styleStack.Children.Add(new TextBlock { Text = "Folder Style:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var styleCombo = new ComboBox { MinWidth = 140, FontSize = 12 };
        styleCombo.Items.Add("3D-Inspired");
        styleCombo.Items.Add("Glossy");
        styleCombo.Items.Add("Flat");
        styleCombo.Items.Add("Minimalist");
        styleCombo.SelectedIndex = 0;
        styleCombo.SelectionChanged += (_, _) =>
        {
            _emojiStyle = styleCombo.SelectedIndex switch
            {
                1 => FolderStyle.Glossy,
                2 => FolderStyle.Flat,
                3 => FolderStyle.Minimalist,
                _ => FolderStyle.Depth3D
            };
            UpdatePreviewAsync();
        };
        styleStack.Children.Add(styleCombo);
        Grid.SetColumn(styleStack, 0);
        styleRow.Children.Add(styleStack);

        var gradStack = new StackPanel { Spacing = 4 };
        gradStack.Children.Add(new TextBlock { Text = "Gradient Mode:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var gradCheck = new CheckBox { Content = "Enable Gradient", IsChecked = _emojiUseGradient };
        gradCheck.Checked += (_, _) => { _emojiUseGradient = true; UpdatePreviewAsync(); };
        gradCheck.Unchecked += (_, _) => { _emojiUseGradient = false; UpdatePreviewAsync(); };
        gradStack.Children.Add(gradCheck);
        Grid.SetColumn(gradStack, 2);
        styleRow.Children.Add(gradStack);

        panel.Children.Add(styleRow);

        // Colors
        panel.Children.Add(new TextBlock { Text = "Folder Colors (Primary / Accent):", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        panel.Children.Add(BuildColorPaletteRow(hex =>
        {
            _emojiColor1 = hex;
            _emojiColor2 = FolderCustomizationService.Lighten(FolderCustomizationService.FromArgbHex(hex), 0.3f).ToArgb().ToString("X8");
            UpdatePreviewAsync();
        }));

        // Emoji Size & Offset Y
        var adjRow = new Grid();
        adjRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        adjRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        adjRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sizeStack = new StackPanel { Spacing = 2 };
        sizeStack.Children.Add(new TextBlock { Text = "Emoji Size:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var sizeSlider = new Slider { Minimum = 28, Maximum = 110, Value = _emojiSize };
        sizeSlider.ValueChanged += (_, e) => { _emojiSize = (float)e.NewValue; UpdatePreviewAsync(); };
        sizeStack.Children.Add(sizeSlider);
        Grid.SetColumn(sizeStack, 0);
        adjRow.Children.Add(sizeStack);

        var posStack = new StackPanel { Spacing = 2 };
        posStack.Children.Add(new TextBlock { Text = "Vertical Offset:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var posSlider = new Slider { Minimum = -40, Maximum = 40, Value = _emojiOffsetY };
        posSlider.ValueChanged += (_, e) => { _emojiOffsetY = (float)e.NewValue; UpdatePreviewAsync(); };
        posStack.Children.Add(posSlider);
        Grid.SetColumn(posStack, 2);
        adjRow.Children.Add(posStack);

        panel.Children.Add(adjRow);

        return panel;
    }

    private void RefreshEmojiPresets()
    {
        _emojiPresetCombo.Items.Clear();
        foreach (var p in FolderCustomizationService.LoadEmojiPresets())
            _emojiPresetCombo.Items.Add(p.Name);
        if (_emojiPresetCombo.Items.Count > 0) _emojiPresetCombo.SelectedIndex = 0;
    }

    // -------------------------------------------------------------------------
    // TAB 4: ICON BADGES
    // -------------------------------------------------------------------------

    private FrameworkElement BuildBadgePanel()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = "Select a badge to stamp onto this folder's current design (or default icon):",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });

        // Badges Grid Selector (2 clean rows, no scrollbar obscuring buttons)
        var badgeContainer = new StackPanel { Spacing = 6 };
        var badges = new (BadgeType Type, string Label)[]
        {
            (BadgeType.Star, "⭐ Star"),
            (BadgeType.Heart, "❤️ Heart"),
            (BadgeType.Checkmark, "✔️ Done"),
            (BadgeType.Warning, "⚠️ Alert"),
            (BadgeType.Lock, "🔒 Lock"),
            (BadgeType.Important, "❗ Priority"),
            (BadgeType.New, "🆕 New"),
            (BadgeType.Clock, "⏰ Clock"),
            (BadgeType.Question, "❓ Info"),
        };

        const int badgesPerRow = 5;
        for (int i = 0; i < badges.Length; i += badgesPerRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (bType, bLabel) in badges.Skip(i).Take(badgesPerRow))
            {
                var btn = new Button
                {
                    Content = bLabel,
                    FontSize = 11,
                    Padding = new Thickness(10, 6, 10, 6),
                    CornerRadius = new CornerRadius(5),
                    Background = _badgeType == bType ? Brush(MenuTheme.AccentColor) : Brush("#FF1E212D"),
                    Foreground = _badgeType == bType ? Brush("#FF000000") : Brush(MenuTheme.LabelColor),
                    BorderBrush = Brush("#FF2F3446"),
                };
                var capture = bType;
                btn.Click += (_, _) =>
                {
                    _badgeType = capture;
                    SwitchTab(FolderCustomTab.Badge); // refresh buttons
                };
                row.Children.Add(btn);
            }
            badgeContainer.Children.Add(row);
        }
        panel.Children.Add(badgeContainer);

        // Position & Size Row
        var posRow = new Grid();
        posRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        posRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        posRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var posStack = new StackPanel { Spacing = 2 };
        posStack.Children.Add(new TextBlock { Text = "Badge Position:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var posCombo = new ComboBox { MinWidth = 140, FontSize = 12 };
        posCombo.Items.Add("Bottom-Right");
        posCombo.Items.Add("Bottom-Left");
        posCombo.Items.Add("Top-Right");
        posCombo.Items.Add("Top-Left");
        posCombo.SelectedIndex = 0;
        posCombo.SelectionChanged += (_, _) =>
        {
            _badgePosition = posCombo.SelectedIndex switch
            {
                1 => BadgePosition.BottomLeft,
                2 => BadgePosition.TopRight,
                3 => BadgePosition.TopLeft,
                _ => BadgePosition.BottomRight,
            };
            UpdatePreviewAsync();
        };
        posStack.Children.Add(posCombo);
        Grid.SetColumn(posStack, 0);
        posRow.Children.Add(posStack);

        var sizeStack = new StackPanel { Spacing = 2 };
        sizeStack.Children.Add(new TextBlock { Text = "Badge Size:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var sizeCombo = new ComboBox { MinWidth = 140, FontSize = 12 };
        sizeCombo.Items.Add("Medium (Standard)");
        sizeCombo.Items.Add("Small (Subtle)");
        sizeCombo.Items.Add("Large (Prominent)");
        sizeCombo.SelectedIndex = 0;
        sizeCombo.SelectionChanged += (_, _) =>
        {
            _badgeSize = sizeCombo.SelectedIndex switch
            {
                1 => BadgeSize.Small,
                2 => BadgeSize.Large,
                _ => BadgeSize.Medium,
            };
            UpdatePreviewAsync();
        };
        sizeStack.Children.Add(sizeCombo);
        Grid.SetColumn(sizeStack, 2);
        posRow.Children.Add(sizeStack);

        panel.Children.Add(posRow);

        // Transparent background toggle
        var transCheck = new CheckBox { Content = "Transparent badge background (icon only)", IsChecked = _badgeTransparent };
        transCheck.Checked += (_, _) => { _badgeTransparent = true; UpdatePreviewAsync(); };
        transCheck.Unchecked += (_, _) => { _badgeTransparent = false; UpdatePreviewAsync(); };
        panel.Children.Add(transCheck);

        // Badge Background Color
        panel.Children.Add(new TextBlock { Text = "Badge Plate Color:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        panel.Children.Add(BuildColorPaletteRow(hex => { _badgeBgColor = hex; UpdatePreviewAsync(); }));

        // Restore original icon button
        var restoreBtn = MakeButton("", "#FF222530");
        LocalizationUi.SetContent(restoreBtn, "↩️ {0}",
            LocalizationUi.Localized("Remove Badge & Restore Original Icon"));
        restoreBtn.Click += async (_, _) =>
        {
            var ok = await FolderCustomizationService.RestoreOriginalBadgeIconAsync(_folderPath);
            LocalizationUi.SetText(_statusText, ok
                ? "Badge removed, original icon restored ✓"
                : "Restored to default icon ✓");
            UpdatePreviewAsync();
        };
        panel.Children.Add(restoreBtn);

        return panel;
    }

    // -------------------------------------------------------------------------
    // TAB 4: FOLDER MONOGRAM
    // -------------------------------------------------------------------------

    private FrameworkElement BuildMonogramPanel()
    {
        var panel = new StackPanel { Spacing = 10 };

        // Monogram Input & Suggest Initials
        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var inStack = new StackPanel { Spacing = 2 };
        inStack.Children.Add(new TextBlock { Text = "Monogram Text:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        _monogramInputBox = new TextBox { Text = _monogramText, FontSize = 16, MaxLength = 5 };
        _monogramInputBox.TextChanged += (_, _) =>
        {
            _monogramText = _monogramInputBox.Text;
            UpdatePreviewAsync();
        };
        inStack.Children.Add(_monogramInputBox);
        Grid.SetColumn(inStack, 0);
        inputRow.Children.Add(inStack);

        var suggestBtn = MakeButton("", "#FF25293A");
        LocalizationUi.SetContent(suggestBtn, "💡 {0}", LocalizationUi.Localized("Suggest"));
        suggestBtn.VerticalAlignment = VerticalAlignment.Bottom;
        suggestBtn.Click += (_, _) =>
        {
            _monogramText = FolderCustomizationService.SuggestInitials(_folderName);
            _monogramInputBox.Text = _monogramText;
            UpdatePreviewAsync();
        };
        Grid.SetColumn(suggestBtn, 2);
        inputRow.Children.Add(suggestBtn);

        // Presets selector
        var presetStack = new StackPanel { Spacing = 2 };
        presetStack.Children.Add(new TextBlock { Text = "Presets:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        _monogramPresetCombo = new ComboBox { MinWidth = 140, FontSize = 11 };
        RefreshMonogramPresets();
        _monogramPresetCombo.SelectionChanged += (_, _) =>
        {
            if (_monogramPresetCombo.SelectedItem is string name)
            {
                var p = FolderCustomizationService.LoadMonogramPresets().FirstOrDefault(x => x.Name == name);
                if (p != null)
                {
                    _monogramFont = p.Font;
                    _monogramStyle = p.Style;
                    _monogramSymbol = p.Symbol;
                    _monogramUseGradient = p.UseGradient;
                    _monogramColor1 = p.Color1;
                    _monogramColor2 = p.Color2;
                    _monogramTextColor = p.TextColor;
                    UpdatePreviewAsync();
                }
            }
        };
        presetStack.Children.Add(_monogramPresetCombo);
        Grid.SetColumn(presetStack, 4);
        inputRow.Children.Add(presetStack);

        panel.Children.Add(inputRow);

        // Font Selector & Typography Style
        var fontRow = new Grid();
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fStack = new StackPanel { Spacing = 2 };
        fStack.Children.Add(new TextBlock { Text = "System Font:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var fontCombo = new ComboBox { MinWidth = 160, FontSize = 12 };
        foreach (var font in FolderCustomizationService.GetAvailableSystemFonts().Take(18))
            fontCombo.Items.Add(font);
        fontCombo.SelectedIndex = 0;
        fontCombo.SelectionChanged += (_, _) =>
        {
            if (fontCombo.SelectedItem is string fName)
            {
                _monogramFont = fName;
                UpdatePreviewAsync();
            }
        };
        fStack.Children.Add(fontCombo);
        Grid.SetColumn(fStack, 0);
        fontRow.Children.Add(fStack);

        var typoStack = new StackPanel { Spacing = 2 };
        typoStack.Children.Add(new TextBlock { Text = "Typography Style:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var typoCombo = new ComboBox { MinWidth = 160, FontSize = 12 };
        typoCombo.Items.Add("Circular Crest");
        typoCombo.Items.Add("Modern Clean");
        typoCombo.Items.Add("Embossed 3D");
        typoCombo.Items.Add("Banner Ribbon");
        typoCombo.Items.Add("Geometric Box");
        typoCombo.SelectedIndex = 0;
        typoCombo.SelectionChanged += (_, _) =>
        {
            _monogramStyle = typoCombo.SelectedIndex switch
            {
                1 => TypographyStyle.Modern,
                2 => TypographyStyle.Embossed,
                3 => TypographyStyle.BannerRibbon,
                4 => TypographyStyle.BoxFrame,
                _ => TypographyStyle.CircularCrest
            };
            UpdatePreviewAsync();
        };
        typoStack.Children.Add(typoCombo);
        Grid.SetColumn(typoStack, 2);
        fontRow.Children.Add(typoStack);

        panel.Children.Add(fontRow);

        // Decorative Symbol
        var symRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        symRow.Children.Add(new TextBlock { Text = "Emblem Symbol:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var symCombo = new ComboBox { MinWidth = 160, FontSize = 12 };
        symCombo.Items.Add("Crown 👑");
        symCombo.Items.Add("Star ★");
        symCombo.Items.Add("Diamond ◆");
        symCombo.Items.Add("Shield 🛡");
        symCombo.Items.Add("Underline ━");
        symCombo.Items.Add("None");
        symCombo.SelectedIndex = 0;
        symCombo.SelectionChanged += (_, _) =>
        {
            _monogramSymbol = symCombo.SelectedIndex switch
            {
                0 => DecorativeSymbol.Crown,
                1 => DecorativeSymbol.Star,
                2 => DecorativeSymbol.Diamond,
                3 => DecorativeSymbol.Shield,
                4 => DecorativeSymbol.Underline,
                _ => DecorativeSymbol.None
            };
            UpdatePreviewAsync();
        };
        symRow.Children.Add(symCombo);
        panel.Children.Add(symRow);

        // Font Size Slider
        var fsStack = new StackPanel { Spacing = 2 };
        fsStack.Children.Add(new TextBlock { Text = "Font Size:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var fsSlider = new Slider { Minimum = 28, Maximum = 100, Value = _monogramFontSize };
        fsSlider.ValueChanged += (_, e) => { _monogramFontSize = (float)e.NewValue; UpdatePreviewAsync(); };
        fsStack.Children.Add(fsSlider);
        panel.Children.Add(fsStack);

        // Monogram Folder Colors
        panel.Children.Add(new TextBlock { Text = "Folder Background Color:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        panel.Children.Add(BuildColorPaletteRow(hex =>
        {
            _monogramColor1 = hex;
            _monogramColor2 = FolderCustomizationService.Darken(FolderCustomizationService.FromArgbHex(hex), 0.35f).ToArgb().ToString("X8");
            UpdatePreviewAsync();
        }));

        return panel;
    }

    private void RefreshMonogramPresets()
    {
        _monogramPresetCombo.Items.Clear();
        foreach (var p in FolderCustomizationService.LoadMonogramPresets())
            _monogramPresetCombo.Items.Add(p.Name);
        if (_monogramPresetCombo.Items.Count > 0) _monogramPresetCombo.SelectedIndex = 0;
    }

    // -------------------------------------------------------------------------
    // TAB 5: BACKGROUND PATTERN
    // -------------------------------------------------------------------------

    private FrameworkElement BuildPatternPanel()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = "Apply seamless repeating texture patterns across the folder icon:",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });

        // Pattern Type selector
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        typeRow.Children.Add(new TextBlock { Text = "Pattern Style:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var typeCombo = new ComboBox { MinWidth = 180, FontSize = 12 };
        string[] patterns = ["Carbon Fiber", "Diagonal Stripes", "Polka Dots", "Honeycomb", "Checkerboard", "Chevron", "Circuit Board", "Diamond Mesh", "Waves", "Camouflage"];
        foreach (var p in patterns) typeCombo.Items.Add(p);
        typeCombo.SelectedIndex = 0;
        typeCombo.SelectionChanged += (_, _) =>
        {
            _patternType = typeCombo.SelectedIndex switch
            {
                1 => PatternType.DiagonalStripes,
                2 => PatternType.PolkaDots,
                3 => PatternType.Honeycomb,
                4 => PatternType.Checkerboard,
                5 => PatternType.Chevron,
                6 => PatternType.CircuitBoard,
                7 => PatternType.DiamondMesh,
                8 => PatternType.Waves,
                9 => PatternType.Camouflage,
                _ => PatternType.CarbonFiber
            };
            UpdatePreviewAsync();
        };
        typeRow.Children.Add(typeCombo);
        panel.Children.Add(typeRow);

        // Pattern Area & Scale
        var optRow = new Grid();
        optRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        optRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var areaStack = new StackPanel { Spacing = 2 };
        areaStack.Children.Add(new TextBlock { Text = "Pattern Coverage:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var areaCombo = new ComboBox { MinWidth = 140, FontSize = 12 };
        areaCombo.Items.Add("Front Pocket Only");
        areaCombo.Items.Add("Full Folder Silhouette");
        areaCombo.SelectedIndex = 0;
        areaCombo.SelectionChanged += (_, _) =>
        {
            _patternArea = areaCombo.SelectedIndex == 1 ? PatternArea.FullFolder : PatternArea.BodyOnly;
            UpdatePreviewAsync();
        };
        areaStack.Children.Add(areaCombo);
        Grid.SetColumn(areaStack, 0);
        optRow.Children.Add(areaStack);

        var scaleStack = new StackPanel { Spacing = 2 };
        scaleStack.Children.Add(new TextBlock { Text = "Texture Scale:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var scaleCombo = new ComboBox { MinWidth = 140, FontSize = 12 };
        scaleCombo.Items.Add("Medium");
        scaleCombo.Items.Add("Fine (Detailed)");
        scaleCombo.Items.Add("Coarse (Bold)");
        scaleCombo.SelectedIndex = 0;
        scaleCombo.SelectionChanged += (_, _) =>
        {
            _patternScale = scaleCombo.SelectedIndex switch
            {
                1 => PatternScale.Fine,
                2 => PatternScale.Coarse,
                _ => PatternScale.Medium
            };
            UpdatePreviewAsync();
        };
        scaleStack.Children.Add(scaleCombo);
        Grid.SetColumn(scaleStack, 2);
        optRow.Children.Add(scaleStack);

        panel.Children.Add(optRow);

        // Opacity Slider
        var opStack = new StackPanel { Spacing = 2 };
        opStack.Children.Add(new TextBlock { Text = "Pattern Contrast / Opacity:", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var opSlider = new Slider { Minimum = 15, Maximum = 100, Value = 55 };
        opSlider.ValueChanged += (_, e) =>
        {
            _patternOpacity = (float)(e.NewValue / 100.0);
            UpdatePreviewAsync();
        };
        opStack.Children.Add(opSlider);
        panel.Children.Add(opStack);

        // Pattern Color Themes
        panel.Children.Add(new TextBlock { Text = "Theme Palettes (Background / Pattern Accent):", FontSize = 11, Foreground = Brush(MenuTheme.HintColor) });
        var themesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var themes = new (string Name, string Bg, string Fg)[]
        {
            ("Carbon Gold", "#FF181A22", "#FFF5A623"),
            ("Cyber Neon", "#FF0F172A", "#FF00E5FF"),
            ("Crimson Steel", "#FF2B1113", "#FFE53E3E"),
            ("Emerald Tech", "#FF0B221A", "#FF48BB78"),
            ("Royal Violet", "#FF1A1028", "#FF9F7AEA"),
            ("Luxury White", "#FF2D3748", "#FFFFFFFF")
        };
        foreach (var (tName, bg, fg) in themes)
        {
            var tBtn = new Button
            {
                Content = tName,
                FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                Background = Brush("#FF1E212D"),
                Foreground = Brush(fg),
                BorderBrush = Brush(bg),
                CornerRadius = new CornerRadius(5),
            };
            var curBg = bg; var curFg = fg;
            tBtn.Click += (_, _) =>
            {
                _patternBgColor = curBg;
                _patternFgColor = curFg;
                UpdatePreviewAsync();
            };
            themesRow.Children.Add(tBtn);
        }
        panel.Children.Add(themesRow);

        return panel;
    }

    // -------------------------------------------------------------------------
    // REUSABLE COLOR PALETTE COMPONENT
    // -------------------------------------------------------------------------

    private static FrameworkElement BuildColorPaletteRow(Action<string> onColorSelected)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string[] palette =
        [
            "#FFD4842F", // Classic folder yellow
            "#FF181A22", // Obsidian black
            "#FF4E9BFF", // Azure blue
            "#FF43C59E", // Mint green
            "#FFE5574E", // Coral red
            "#FF9F6FE8", // Lavender purple
            "#FFF09A3E", // Amber orange
            "#FF241312", // Wine dark
            "#FF4A5568", // Slate gray
            "#FFFFFFFF", // Pure white
        ];

        foreach (var hex in palette)
        {
            var swatch = new Button
            {
                Content = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Fill = Brush(hex),
                    Stroke = Brush("#FF3A3F50"),
                    StrokeThickness = 1,
                },
                Padding = new Thickness(0),
                MinWidth = 28,
                MinHeight = 28,
                Background = Brush("Transparent"),
                CornerRadius = new CornerRadius(14),
            };
            var colorHex = hex;
            swatch.Click += (_, _) => onColorSelected(colorHex);
            row.Children.Add(swatch);
        }

        return row;
    }

    private static Button MakeButton(string label, string bgHex) => new()
    {
        Content = label,
        Foreground = Brush(MenuTheme.LabelColor),
        Background = Brush(bgHex),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Padding = new Thickness(12, 6, 12, 6),
        CornerRadius = new CornerRadius(6),
    };

    private static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    private static Windows.UI.Color ParseColor(string hex)
    {
        if (hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            return Windows.UI.Color.FromArgb(0, 0, 0, 0);

        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }
}
