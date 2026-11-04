using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Limona.Interop;
using Limona.Services;

namespace Limona.Menu;

/// <summary>
/// Window opened by the "Change Icon" folder slice: pick an image or .ico file
/// (converted on the fly when needed) to become the folder's custom icon, or
/// reset back to the default one.
/// </summary>
internal sealed class FolderIconWindow : Window
{
    private static FolderIconWindow? s_active;

    private readonly string _folderPath;
    private readonly string _folderName;

    private TextBlock _statusText = null!;
    private Button _browseButton = null!;
    private Button _resetButton = null!;
    private Button _closeButton = null!;

    /// <summary>Opens the folder customization window (single instance at a time).</summary>
    public static void ShowForFile(string folderPath)
    {
        FolderCustomizationWindow.ShowForFile(folderPath, FolderCustomTab.Library);
    }

    private FolderIconWindow(string folderPath)
    {
        s_active = this;
        _folderPath = folderPath;
        _folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        WindowFit.SetTitle(this, "Folder icon");
        _statusText = MakeStatusText();
        _browseButton = MakeButton("Browse image…");
        _resetButton = MakeButton("Default icon");
        _closeButton = MakeButton("Close");
        _browseButton.Click += (_, _) => _ = OnBrowseAsync();
        _resetButton.Click += (_, _) => ResetIcon();
        _closeButton.Click += (_, _) => Close();

        Content = new Border
        {
            Background = Brush(MenuTheme.HubFill),
            Padding = new Thickness(20, 16, 20, 16),
            Child = BuildLayout(),
        };

        // Keep the icon picker above all other windows so the user can
        // click folder icons in Explorer and immediately see the result.
        if (this.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            p.IsAlwaysOnTop = true;

        WindowFit.Apply(this, (FrameworkElement)Content!);
        this.Activate();
        this.Closed += (_, _) => s_active = null;
    }

    private UIElement BuildLayout()
    {
        var stack = new StackPanel { Spacing = 12, MinWidth = 320 };

        stack.Children.Add(new TextBlock
        {
            Text = "Folder icon",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(MenuTheme.LabelColor),
        });

        stack.Children.Add(new TextBlock
        {
            Text = Limona.Services.Loc.F(
                "\"{0}\" — pick an image or .ico file; it is placed inside the folder and set as its icon.",
                _folderName),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(MenuTheme.HintColor),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(_browseButton);
        row.Children.Add(_resetButton);
        row.Children.Add(_closeButton);
        stack.Children.Add(row);

        // --------------------------------------------------------------
        // Gallery: user-provided icons from %LOCALAPPDATA%\Limona\Icons
        // --------------------------------------------------------------
        stack.Children.Add(new TextBlock
        {
            Text = "Icon pack:",
            FontSize = 12,
            Foreground = Brush(MenuTheme.HintColor),
        });
        stack.Children.Add(BuildGallerySection());

        stack.Children.Add(_statusText);
        return stack;
    }

    // ----------------------------------------------------------------------
    // Icon-pack gallery (loaded from Limona\Icons and %LOCALAPPDATA%\Limona\Icons)
    // ----------------------------------------------------------------------

    private static readonly string AppDataGalleryFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limona", "Icons");

    private static readonly string[] GalleryExtensions =
        [".ico", ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

    private readonly List<Button> _galleryButtons = new();
    private StackPanel _iconsGridPanel = null!;
    private ComboBox _categoryCombo = null!;
    private List<(string Path, string Pack)> _allIcons = new();

    private static IEnumerable<string> GetIconDirectories()
    {
        var dirs = new List<string>();

        // 1. App output / bundled Icons folder
        var baseDir = Path.Combine(AppContext.BaseDirectory, "Icons");
        if (Directory.Exists(baseDir)) dirs.Add(baseDir);

        // 2. Project root Icons folder (during development or portable run)
        try
        {
            var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\Icons"));
            if (Directory.Exists(projectDir) && !dirs.Contains(projectDir, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(projectDir);
            }
        }
        catch { }

        // 3. User %LOCALAPPDATA% folder
        if (Directory.Exists(AppDataGalleryFolder) && !dirs.Contains(AppDataGalleryFolder, StringComparer.OrdinalIgnoreCase))
        {
            dirs.Add(AppDataGalleryFolder);
        }

        return dirs;
    }

    private UIElement BuildGallerySection()
    {
        try { Directory.CreateDirectory(AppDataGalleryFolder); } catch { }

        var panel = new StackPanel { Spacing = 8 };

        // Scan all icon sources and group by subfolder/pack
        _allIcons = DiscoverIcons();

        if (_allIcons.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No icons found in Limona\\Icons.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(MenuTheme.HintColor),
            });
            return panel;
        }

        // Distinct category names (pack folders)
        var categories = _allIcons
            .Select(i => string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Category filter row
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        filterRow.Children.Add(new TextBlock
        {
            Text = "Category:",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(MenuTheme.LabelColor),
        });

        _categoryCombo = new ComboBox
        {
            MinWidth = 200,
            FontSize = 12,
        };

        _categoryCombo.Items.Add(Limona.Services.Loc.F("All Categories ({0})", _allIcons.Count));
        foreach (var cat in categories)
        {
            var count = _allIcons.Count(i => (string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack).Equals(cat, StringComparison.OrdinalIgnoreCase));
            _categoryCombo.Items.Add($"{cat} ({count})");
        }
        _categoryCombo.SelectedIndex = 0;
        _categoryCombo.SelectionChanged += (_, _) => PopulateIconsForCategory();
        filterRow.Children.Add(_categoryCombo);

        panel.Children.Add(filterRow);

        // Scrollable area for icons
        _iconsGridPanel = new StackPanel { Spacing = 8 };
        var scrollViewer = new ScrollViewer
        {
            Content = _iconsGridPanel,
            MaxHeight = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(2),
        };
        panel.Children.Add(scrollViewer);

        // Initial population
        PopulateIconsForCategory();

        return panel;
    }

    private List<(string Path, string Pack)> DiscoverIcons()
    {
        var list = new List<(string Path, string Pack)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootDir in GetIconDirectories())
        {
            try
            {
                var dirInfo = new DirectoryInfo(rootDir);
                if (!dirInfo.Exists) continue;

                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!GalleryExtensions.Contains(file.Extension.ToLowerInvariant())) continue;
                    if (!seenPaths.Add(file.FullName)) continue;

                    // Derive pack name: first directory relative to rootDir
                    string pack = "";
                    try
                    {
                        var rel = Path.GetRelativePath(rootDir, file.FullName);
                        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (parts.Length > 1)
                        {
                            pack = parts[0];
                        }
                    }
                    catch
                    {
                        pack = file.Directory?.Name ?? "";
                    }

                    list.Add((file.FullName, pack));
                }
            }
            catch (Exception ex)
            {
                Dbg.Log($"DiscoverIcons failed for '{rootDir}': {ex.Message}");
            }
        }

        return list;
    }

    private void PopulateIconsForCategory()
    {
        _iconsGridPanel.Children.Clear();
        _galleryButtons.Clear();

        if (_categoryCombo == null || _allIcons.Count == 0) return;

        var selectedIdx = _categoryCombo.SelectedIndex;
        List<(string Path, string Pack)> filtered;

        if (selectedIdx <= 0)
        {
            filtered = _allIcons;
        }
        else
        {
            var selectedText = _categoryCombo.SelectedItem as string ?? "";
            var catName = selectedText.Contains(" (") ? selectedText.Substring(0, selectedText.LastIndexOf(" (")) : selectedText;
            filtered = _allIcons
                .Where(i => (string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack).Equals(catName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Group by category/pack when viewing all or show direct grid
        var groups = filtered
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Pack) ? "General" : i.Pack, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        const int perRow = 6;

        foreach (var group in groups)
        {
            if (selectedIdx <= 0)
            {
                _iconsGridPanel.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Brush(MenuTheme.AccentColor),
                    Margin = new Thickness(0, 4, 0, 2),
                });
            }

            var items = group.ToList();
            for (var i = 0; i < items.Count; i += perRow)
            {
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var (path, _) in items.Skip(i).Take(perRow))
                {
                    var button = new Button
                    {
                        Content = new Microsoft.UI.Xaml.Controls.Image
                        {
                            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)),
                            Width = 36,
                            Height = 36,
                            Stretch = Stretch.Uniform,
                        },
                        Padding = new Thickness(4),
                        MinWidth = 48,
                        MinHeight = 48,
                    };
                    Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, Path.GetFileName(path));
                    var source = path;
                    button.Click += (_, _) => _ = OnGalleryPickedAsync(source);
                    _galleryButtons.Add(button);
                    rowPanel.Children.Add(button);
                }
                _iconsGridPanel.Children.Add(rowPanel);
            }
        }
    }

    private async System.Threading.Tasks.Task OnGalleryPickedAsync(string sourceFile)
    {
        SetBusy(true);
        SetStatus("Applying icon…", MenuTheme.AccentColor);
        try
        {
            await FolderService.ApplyGalleryIconAsync(_folderPath, sourceFile);
            SetStatus("Icon applied ✓ (Explorer refreshes in a moment.)", MenuTheme.AccentColor);
        }
        catch (Exception ex)
        {
            Dbg.Log("FolderIcon gallery apply THREW: " + ex);
            SetStatus("Failed: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async System.Threading.Tasks.Task OnBrowseAsync()
    {
        SetStatus("Choose an image…", MenuTheme.HintColor);
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".ico");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".gif");

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SetStatus("", MenuTheme.HintColor);
                return;
            }

            SetBusy(true);
            SetStatus("Preparing icon…", MenuTheme.AccentColor);
            try
            {
                await FolderService.SetFolderIconAsync(_folderPath, file.Path);
                SetStatus("Icon applied ✓ (Explorer refreshes in a moment.)", MenuTheme.AccentColor);
            }
            catch (Exception ex)
            {
                Dbg.Log("FolderIcon apply THREW: " + ex);
                SetStatus("Failed: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
            }
            finally
            {
                SetBusy(false);
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("FolderIcon picker THREW: " + ex);
            SetBusy(false);
            SetStatus("Picker failed: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
        }
    }

    private void ResetIcon()
    {
        try
        {
            FolderService.ResetFolderIcon(_folderPath);
            SetStatus("Back to the default icon ✓", MenuTheme.AccentColor);
        }
        catch (Exception ex)
        {
            Dbg.Log("FolderIcon reset THREW: " + ex);
            SetStatus("Failed: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
        }
    }

    private void SetBusy(bool busy)
    {
        _browseButton.IsEnabled = !busy;
        _resetButton.IsEnabled = !busy;
        _closeButton.IsEnabled = !busy;
        foreach (var button in _galleryButtons)
        {
            button.IsEnabled = !busy;
        }
    }

    private void SetStatus(string text, string color, params object?[] args) =>
        SetStatus(text, new SolidColorBrush(ParseColor(color)), args);

    private void SetStatus(string text, SolidColorBrush brush, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_statusText, text);
        else LocalizationUi.SetFormattedText(_statusText, text, args);
        _statusText.Foreground = brush;
        _statusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock MakeStatusText() => new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    private static Button MakeButton(string label) => new()
    {
        Content = label,
        Foreground = Brush(MenuTheme.IconHoverColor),
        Background = Brush(MenuTheme.AccentColor),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Padding = new Thickness(14, 7, 14, 7),
        CornerRadius = new CornerRadius(6),
    };

    private static SolidColorBrush Brush(string hex) => new(ParseColor(hex));

    private static Windows.UI.Color ParseColor(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }
}
