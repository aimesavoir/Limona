using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Limona.Interop;
using Limona.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;

using MQ = Microsoft.UI.Dispatching;

namespace Limona.Menu;

/// <summary>
/// Interactive image editor: brightness/contrast/saturation, rotation, flip,
/// mouse-drag crop with resize handles, live preview and Save (PNG/JPG).
/// </summary>
internal sealed class ImageEditWindow : Window
{
    private const int PreviewMaxSide = 900;
    private const double CropHandleHitRadius = 14;
    private const double LayerHandleHitRadius = 12;
    private const int StickerGalleryColumns = 8;

    private static readonly InputCursor CropArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor CropCrossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private static readonly InputCursor CropMoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    private static readonly InputCursor CropResizeHorizontalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor CropResizeVerticalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    private static readonly InputCursor CropResizeDiagonalDownCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
    private static readonly InputCursor CropResizeDiagonalUpCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);
    private static readonly string[] CropHandleNames = { "NW", "N", "NE", "E", "SE", "S", "SW", "W" };
    private static readonly string[] ColorRangeNames = { "Reds", "Oranges", "Yellows", "Greens", "Cyans", "Blues", "Purples", "Magentas" };
    private static readonly double[] ColorRangeCenters = { 0, 30, 60, 120, 180, 240, 280, 320 };
    private static readonly string[] ColorRangeSwatches = { "#FFE84D4D", "#FFF28A3C", "#FFE7C83E", "#FF53A866", "#FF35AAA5", "#FF4E80D8", "#FF8D69C8", "#FFD2549B" };

    /// <summary>Fonts requested for the text tool, in display order. Each entry
    /// lists the real family names to probe (some fonts ship under a different
    /// internal name, e.g. "Agency" → "Agency FB").</summary>
    private static readonly (string Display, string[] Families)[] PreferredFonts =
    {
        // Latin
        ("Agency",                new[] { "Agency", "Agency FB" }),
        ("Ambawang signature",    new[] { "Ambawang Signature" }),
        ("Amerika Signature",     new[] { "Amerika Signature", "Amerika Signature Demo" }),
        ("AR Techni",             new[] { "AR Techni" }),
        ("Bahnschrift Condensed", new[] { "Bahnschrift Condensed" }),
        ("Bebas Neue",            new[] { "Bebas Neue" }),
        ("Book Signature",        new[] { "Book Signature" }),
        ("Khaled Font",           new[] { "Khaled Font" }),
        ("Cascadia",              new[] { "Cascadia Code", "Cascadia Mono", "Cascadia" }),
        ("DigifaceWide",          new[] { "DigifaceWide" }),
        ("Trade Gothic Inline",   new[] { "Trade Gothic Inline", "Trade Gothic Inline LH" }),
        // Arabic
        ("Cairo",                 new[] { "Cairo" }),
        ("Tajawal",               new[] { "Tajawal" }),
        ("Noto Sans Arabic",      new[] { "Noto Sans Arabic" }),
        ("(A) Arslan Wessam A",   new[] { "(A) Arslan Wessam A" }),
        ("Almarai",               new[] { "Almarai" }),
        ("Ammar Thin",            new[] { "Ammar Thin" }),
        ("Ara Alharbi Alhanoof",  new[] { "Ara Alharbi Alhanoof" }),
        ("Aref Ruqaa",            new[] { "Aref Ruqaa" }),
    };

    private static readonly string[] BaseFonts =
        { "Segoe UI", "Arial", "Georgia", "Times New Roman", "Verdana", "Consolas", "Cambria" };


    private static ImageEditWindow? s_active;

    private readonly string _sourcePath;
    private string _fileName;
    private bool _saving;
    private int _busyCount;

    private byte[]? _pixels;
    private uint _w, _h;
    private bool _loaded;

    private int _rotation;
    private bool _flipH, _flipV;

    private double _brightness = 0;
    private double _contrast = 0;
    private double _saturation = 0;
    private double _exposure;
    private double _highlights;
    private double _shadows;
    private int _hueRange;
    private double _hueShift;
    private double _hueSaturation;
    private double _hueLightness;
    private bool _updatingHslControls;

    private readonly record struct HueAdjustment(double HueShift, double Saturation, double Lightness);

    private sealed class ImageLayerItem : INotifyPropertyChanged
    {
        private string _name = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string LabelSource { get; set; } = "";
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }
        public TextOverlay? Text { get; init; }
        public ImageOverlay? Image { get; init; }
        public double AspectRatio { get; init; } = 1;
    }

    private readonly record struct LayerSnapshot(TextOverlay? Text, ImageOverlay? Image);

    private readonly record struct EditSettings(
        double Brightness, double Contrast, double Saturation,
        double Exposure, double Highlights, double Shadows,
        HueAdjustment[] HueAdjustments,
        int Rotation, bool FlipH, bool FlipV,
        bool HasCrop, int CropX, int CropY, int CropW, int CropH,
        LayerSnapshot[] Layers);

    private readonly ObservableCollection<ImageLayerItem> _layers = new();
    private readonly HueAdjustment[] _hueAdjustments = new HueAdjustment[ColorRangeNames.Length];
    private ImageLayerItem? _selectedLayer;
    private bool _updatingLayerControls;
    private bool _layerDragging;
    private bool _layerResizing;
    private int _layerResizeHandle = -1;
    private Point _layerDragStart;
    private Point _layerResizeStart;
    private double _layerStartX, _layerStartY;
    private double _layerStartSize;
    private Rect _layerResizeStartBounds;

    private int _cropX, _cropY, _cropW, _cropH;
    private bool _hasCrop;

    private bool _cropDragging;
    private Point _cropStart;
    private Point _cropEnd;
    private bool _cropMode;

    private bool _resizing;
    private string _resizeHandle = "";
    private Point _resizeStart;
    private int _resizeStartX, _resizeStartY, _resizeStartW, _resizeStartH;

    private bool _moving;
    private Point _moveStart;
    private int _moveStartX, _moveStartY;

    private TextBlock _titleText = null!;
    private TextBlock _dimInfo = null!;
    private Image _previewImage = null!;
    private Border _previewHost = null!;
    private Canvas _cropCanvas = null!;
    private Canvas _layerCanvas = null!;
    private Rectangle[] _cropShades = null!;
    private Rectangle _cropRectVisual = null!;
    private Rectangle[] _handles = null!;

    private Slider _brightnessSlider = null!;
    private Slider _contrastSlider = null!;
    private Slider _saturationSlider = null!;
    private Slider _exposureSlider = null!;
    private Slider _highlightsSlider = null!;
    private Slider _shadowsSlider = null!;
    private Slider _hueSlider = null!;
    private Slider _hueSaturationSlider = null!;
    private Slider _hueLightnessSlider = null!;
    private Grid _colorRangePicker = null!;
    private Ellipse[] _colorRangeRings = null!;
    private TextBox _layerTextInput = null!;
    private string _defaultLayerText = "";
    private bool _layerTextWasEdited;
    private ListView _layerList = null!;
    private Slider _layerSizeSlider = null!;
    private Slider _layerOpacitySlider = null!;
    private TextBlock _layerSizeValue = null!;
    private TextBlock _layerOpacityValue = null!;
    private ComboBox _layerFontCombo = null!;
    private ComboBox _layerWeightCombo = null!;
    private ColorPickerFlyout.SwatchButton _layerColorSwatch = null!;
    private ColorPickerFlyout.SwatchButton _layerStrokeSwatch = null!;
    private Slider _layerStrokeSlider = null!;
    private TextBlock _layerStrokeValue = null!;
    private CheckBox _layerItalicCheckBox = null!;
    private CheckBox _layerOutlineCheckBox = null!;
    private uint _textColorArgb = 0xFFFFFFFF;
    private uint _strokeColorArgb = 0xC0000000;
    private TextBox _stickerSearchBox = null!;
    private Grid _stickerGallery = null!;
    private Button _layerDeleteButton = null!;
    private Button _removeLayerButton = null!;
    private TextBlock _brightnessValue = null!;
    private TextBlock _contrastValue = null!;
    private TextBlock _saturationValue = null!;
    private TextBlock _exposureValue = null!;
    private TextBlock _highlightsValue = null!;
    private TextBlock _shadowsValue = null!;
    private TextBlock _hueValue = null!;
    private TextBlock _hueSaturationValue = null!;
    private TextBlock _hueLightnessValue = null!;
    private Button _rotCwButton = null!;
    private Button _rotCcwButton = null!;
    private Button _rot180Button = null!;
    private Button _flipHButton = null!;
    private Button _flipVButton = null!;
    private Button _cropModeButton = null!;
    private Button _applyCropButton = null!;
    private Button _resetButton = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;
    private TextBlock _status = null!;
    private Border _busyBadge = null!;

    private SoftwareBitmapSource? _bitmapSource;

    // Palette of the rendered image + eyedropper (color pick from any area).
    private StackPanel _paletteBar = null!;              // swatch circles
    private Button _dropperButton = null!;               // toggles pick mode
    private Button _paletteCodeButton = null!;           // shows "#RRGGBB  RGB(r, g, b)" — click copies
    private Canvas _pickCanvas = null!;                  // overlay while picking
    private Ellipse _pickRing = null!;                   // hovered-color circle
    private bool _pickMode;
    private InputCursor? _pickTargetCursor;
    private string _currentColorHex = "";
    private byte[]? _latestPixels;                       // rendered preview pixels (BGRA)
    private uint _latestW, _latestH;

    public static void ShowForFile(string sourcePath)
    {
        s_active = ToolWindowCoordinator.Open(() => new ImageEditWindow(sourcePath));
    }

    private ImageEditWindow(string sourcePath)
    {
        _sourcePath = sourcePath;
        _fileName = System.IO.Path.GetFileName(sourcePath);

        WindowFit.SetTitle(this, "Edit Image — " + _fileName);
        _titleText = MakeTitle();
        _dimInfo = MakeHintText();
        _previewImage = MakePreviewImage();
        _previewHost = MakePreviewHost();
        _cropCanvas = new Canvas { IsHitTestVisible = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00)) };
        _layerCanvas = new Canvas { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00)) };

        _pickCanvas = new Canvas
        {
            IsHitTestVisible = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00)),
            Visibility = Visibility.Collapsed,
        };
        _pickCanvas.PointerMoved += OnPickPointerMoved;
        _pickCanvas.PointerPressed += OnPickPointerPressed;
        _pickCanvas.PointerReleased += OnPickPointerReleased;
        _pickCanvas.PointerExited += (_, _) => _pickRing.Visibility = Visibility.Collapsed;
        _pickRing = new Ellipse
        {
            Width = 18,
            Height = 18,
            Stroke = Brush("#FFFFFFFF"),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _pickCanvas.Children.Add(_pickRing);

        _cropShades = new Rectangle[4];
        for (int i = 0; i < _cropShades.Length; i++)
        {
            _cropShades[i] = new Rectangle
            {
                Fill = Brush("#66000000"),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _cropCanvas.Children.Add(_cropShades[i]);
        }

        _cropRectVisual = new Rectangle
        {
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0xA6, 0xFF)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x15, 0x4D, 0xA6, 0xFF)),
            Visibility = Visibility.Collapsed,
            RadiusX = 2,
            RadiusY = 2,
        };
        _cropCanvas.Children.Add(_cropRectVisual);

        _handles = new Rectangle[8];
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0xA6, 0xFF)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                RadiusX = 6,
                RadiusY = 6,
                Visibility = Visibility.Collapsed,
            };
            _cropCanvas.Children.Add(_handles[i]);
        }

        _previewHost.Child = _previewImage;

        _brightnessSlider = MakeSlider(-100, 100, 0);
        _contrastSlider = MakeSlider(-100, 100, 0);
        _saturationSlider = MakeSlider(-100, 100, 0);
        _exposureSlider = MakeSlider(-2, 2, 0);
        _exposureSlider.StepFrequency = 0.1;
        _highlightsSlider = MakeSlider(-100, 100, 0);
        _shadowsSlider = MakeSlider(-100, 100, 0);
        _hueSlider = MakeSlider(-180, 180, 0);
        _hueSaturationSlider = MakeSlider(-100, 100, 0);
        _hueLightnessSlider = MakeSlider(-100, 100, 0);
        _colorRangePicker = MakeColorRangePicker();
        _defaultLayerText = Loc.T("Your text");
        _layerTextInput = new TextBox { Text = _defaultLayerText, PlaceholderText = "Text to add", MinHeight = 32 };
        _layerList = new ListView
        {
            Height = 84,
            ItemsSource = _layers,
            DisplayMemberPath = nameof(ImageLayerItem.Name),
            SelectionMode = ListViewSelectionMode.Single,
        };
        _layerFontCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        PopulateFontCombo();
        _layerWeightCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        foreach (var weight in new[] { ("Regular", 400), ("Medium", 500), ("SemiBold", 600), ("Bold", 700), ("ExtraBold", 800) })
            _layerWeightCombo.Items.Add(new ComboBoxItem { Content = weight.Item1, Tag = weight.Item2 });
        _layerColorSwatch = ColorPickerFlyout.MakeSwatch(_textColorArgb, color =>
        {
            _textColorArgb = color;
            if (_updatingLayerControls || _selectedLayer?.Text is not { } text) return;
            text.ColorArgb = color;
            UpdateLayerVisuals();
        }, "Text color");
        _layerStrokeSwatch = ColorPickerFlyout.MakeSwatch(_strokeColorArgb, color =>
        {
            _strokeColorArgb = color;
            if (_updatingLayerControls || _selectedLayer?.Text is not { } text) return;
            _layerOutlineCheckBox.IsChecked = true;
            text.OutlineArgb = color;
            UpdateLayerVisuals();
        }, "Stroke color");
        _layerItalicCheckBox = new CheckBox { Content = "Italic", IsEnabled = false };
        _layerOutlineCheckBox = new CheckBox { Content = "Stroke", IsEnabled = false, IsChecked = true };
        _stickerSearchBox = new TextBox { PlaceholderText = "Search stickers or enter an emoji", MinHeight = 32 };
        SelectFontInCombo("Segoe UI");
        _layerWeightCombo.SelectedIndex = 0;
        _layerSizeSlider = MakeSlider(3, 200, 8);
        _layerOpacitySlider = MakeSlider(0, 100, 100);
        _layerStrokeSlider = MakeSlider(1, 30, 7);
        _layerSizeValue = MakeValueText("8");
        _layerOpacityValue = MakeValueText("100%");
        _layerStrokeValue = MakeValueText("7%");
        _removeLayerButton = new Button { Content = "Remove Layer", MinHeight = 32, IsEnabled = false };
        _layerDeleteButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 },
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            Background = Brush(MenuTheme.AccentDeep),
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(_layerDeleteButton, "Delete selected layer");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_layerDeleteButton, "Delete selected layer");
        _brightnessValue = MakeValueText("0");
        _contrastValue = MakeValueText("0");
        _saturationValue = MakeValueText("0");
        _exposureValue = MakeValueText("0.0 EV");
        _highlightsValue = MakeValueText("0");
        _shadowsValue = MakeValueText("0");
        _hueValue = MakeValueText("0");
        _hueSaturationValue = MakeValueText("0");
        _hueLightnessValue = MakeValueText("0");

        _rotCwButton = MakeIconButton("rotate+90", "\u21BB", "Rotate 90 CW");
        _rotCcwButton = MakeIconButton("rotate-90", "\u21BA", "Rotate 90 CCW");
        _rot180Button = MakeIconButton("back", "\u21BB", "Rotate 180");
        _flipHButton = MakeFlipButton("flipH", "\u21C4", "Flip Horizontal", "Mirror left and right");
        _flipVButton = MakeFlipButton("flipV", "\u21C5", "Flip Vertical", "Mirror top and bottom");
        _cropModeButton = MakeIconButton("crop-tool", "\u2702", "Crop Mode");
        _applyCropButton = MakeIconButton("confirm", "\u2713", "Apply Crop");
        _applyCropButton.IsEnabled = false;
        _resetButton = MakeIconButton("back", "\u21BA", "Reset All");
        _saveButton = MakeAccentButton(Icons.Check(), "Save");
        _cancelButton = MakeCancelButton();
        _status = MakeStatusText();
        _busyBadge = MakeBusyBadge();

        _dropperButton = new Button
        {
            Content = EditIcons.Load("palette", 18, "\uE790"),
            Width = 32,
            Height = 30,
            MinWidth = 32,
            MinHeight = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("#40FFFFFF"),
        };
        ToolTipService.SetToolTip(_dropperButton, "Pick a color from the image (click a spot to copy its code)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_dropperButton, "Toggle color picker");
        _dropperButton.Click += (_, _) => SetPickMode(!_pickMode);

        _paletteCodeButton = new Button
        {
            Content = new TextBlock
            {
                Text = "Pick a color…",
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brush(MenuTheme.HintColor),
            },
            MinHeight = 30,
            Padding = new Thickness(10, 2, 10, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("#40FFFFFF"),
        };
        ToolTipService.SetToolTip(_paletteCodeButton, "Click to copy the HEX code");
        _paletteCodeButton.Click += (_, _) => CopyColorCode();

        _paletteBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };

        _brightnessSlider.ValueChanged += (_, _) => { _brightness = _brightnessSlider.Value; _brightnessValue.Text = ((int)_brightness).ToString(); UpdatePreview(); };
        _contrastSlider.ValueChanged += (_, _) => { _contrast = _contrastSlider.Value; _contrastValue.Text = ((int)_contrast).ToString(); UpdatePreview(); };
        _saturationSlider.ValueChanged += (_, _) => { _saturation = _saturationSlider.Value; _saturationValue.Text = ((int)_saturation).ToString(); UpdatePreview(); };
        _exposureSlider.ValueChanged += (_, _) => { _exposure = _exposureSlider.Value; _exposureValue.Text = $"{_exposure:0.0} EV"; UpdatePreview(); };
        _highlightsSlider.ValueChanged += (_, _) => { _highlights = _highlightsSlider.Value; _highlightsValue.Text = ((int)_highlights).ToString(); UpdatePreview(); };
        _shadowsSlider.ValueChanged += (_, _) => { _shadows = _shadowsSlider.Value; _shadowsValue.Text = ((int)_shadows).ToString(); UpdatePreview(); };
        _hueSlider.ValueChanged += (_, _) => { if (_updatingHslControls) return; _hueShift = _hueSlider.Value; _hueValue.Text = ((int)_hueShift).ToString(); SaveActiveHueAdjustment(); UpdatePreview(); };
        _hueSaturationSlider.ValueChanged += (_, _) => { if (_updatingHslControls) return; _hueSaturation = _hueSaturationSlider.Value; _hueSaturationValue.Text = ((int)_hueSaturation).ToString(); SaveActiveHueAdjustment(); UpdatePreview(); };
        _hueLightnessSlider.ValueChanged += (_, _) => { if (_updatingHslControls) return; _hueLightness = _hueLightnessSlider.Value; _hueLightnessValue.Text = ((int)_hueLightness).ToString(); SaveActiveHueAdjustment(); UpdatePreview(); };
        _layerList.SelectionChanged += (_, _) => SelectLayer(_layerList.SelectedItem as ImageLayerItem);
        _layerSizeSlider.ValueChanged += (_, _) => OnLayerSizeChanged();
        _layerOpacitySlider.ValueChanged += (_, _) => OnLayerOpacityChanged();
        _layerTextInput.TextChanged += (_, _) =>
        {
            if (!_updatingLayerControls &&
                !string.Equals(_layerTextInput.Text, _defaultLayerText, StringComparison.Ordinal))
                _layerTextWasEdited = true;
            OnLayerTextChanged();
        };
        _layerFontCombo.SelectionChanged += (_, _) => OnLayerTypographyChanged();
        _layerWeightCombo.SelectionChanged += (_, _) => OnLayerTypographyChanged();
        _layerStrokeSlider.ValueChanged += (_, _) => OnLayerStrokeWidthChanged();
        _layerItalicCheckBox.Checked += (_, _) => OnLayerTypographyChanged();
        _layerItalicCheckBox.Unchecked += (_, _) => OnLayerTypographyChanged();
        _layerOutlineCheckBox.Checked += (_, _) => OnLayerTypographyChanged();
        _layerOutlineCheckBox.Unchecked += (_, _) => OnLayerTypographyChanged();
        _stickerSearchBox.TextChanged += (_, _) => FilterStickerGallery();
        _removeLayerButton.Click += (_, _) => RemoveSelectedLayer();
        _layerDeleteButton.Click += (_, _) => RemoveSelectedLayer();

        _rotCwButton.Click += (_, _) => { _rotation = (_rotation + 90) % 360; UpdatePreview(); };
        _rotCcwButton.Click += (_, _) => { _rotation = (_rotation + 270) % 360; UpdatePreview(); };
        _rot180Button.Click += (_, _) => { _rotation = (_rotation + 180) % 360; UpdatePreview(); };
        _flipHButton.Click += (_, _) => { _flipH = !_flipH; UpdatePreview(); };
        _flipVButton.Click += (_, _) => { _flipV = !_flipV; UpdatePreview(); };
        _cropModeButton.Click += (_, _) => ToggleCropMode();
        _applyCropButton.Click += (_, _) => ApplyCrop();
        _resetButton.Click += (_, _) => ResetAll();
        _saveButton.Click += (_, _) => _ = OnSaveClicked();
        _cancelButton.Click += (_, _) => Close();

        _cropCanvas.PointerPressed += OnCropPointerPressed;
        _cropCanvas.PointerMoved += OnCropPointerMoved;
        _cropCanvas.PointerReleased += OnCropPointerReleased;
        _cropCanvas.PointerCanceled += (_, _) => EndDrag();
        _cropCanvas.PointerCaptureLost += (_, _) => EndDrag();
        _cropCanvas.PointerExited += (_, _) =>
        {
            if (!_cropDragging && !_resizing && !_moving)
                SetCropCursor(CropArrowCursor);
        };
        _layerCanvas.PointerPressed += OnLayerPointerPressed;
        _layerCanvas.PointerMoved += OnLayerPointerMoved;
        _layerCanvas.PointerReleased += OnLayerPointerReleased;
        _layerCanvas.PointerCanceled += (_, _) => EndLayerDrag();
        _layerCanvas.PointerCaptureLost += (_, _) => EndLayerDrag();
        _layerCanvas.PointerExited += (_, _) =>
        {
            if (!_layerDragging) CursorInterop.SetCursor(_layerCanvas, CropArrowCursor);
        };
        _layerCanvas.SizeChanged += (_, _) => UpdateLayerVisuals();

        Content = BuildLayout();
        WindowFit.ApplyFixed(this, (FrameworkElement)Content!, 1040, 620);
        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;

        _ = PrepareAsync();
    }

    private void OnLanguageChanged()
    {
        if (!_layerTextWasEdited)
        {
            var previousDefault = _defaultLayerText;
            _defaultLayerText = Loc.T("Your text");
            if (_selectedLayer?.Text is null &&
                string.Equals(_layerTextInput.Text, previousDefault, StringComparison.Ordinal))
            {
                _updatingLayerControls = true;
                _layerTextInput.Text = _defaultLayerText;
                _updatingLayerControls = false;
            }
        }

        foreach (var layer in _layers)
            layer.Name = GetLayerDisplayName(layer);
    }

    private static string GetLayerDisplayName(ImageLayerItem layer) =>
        Loc.F(layer.Text is null ? "Image: {0}" : "Text: {0}", ShortLayerName(layer.LabelSource));

    private async System.Threading.Tasks.Task PrepareAsync()
    {
        BeginBusy();
        try
        {
            var (pixels, w, h, _, _) = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.DecodeScaledPixelsAsync(_sourcePath, PreviewMaxSide));
            _pixels = pixels;
            _w = w;
            _h = h;
            _cropW = (int)w;
            _cropH = (int)h;
            LocalizationUi.SetFormattedText(_dimInfo, "Original: {0} x {1} px", w, h);
            _loaded = true;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            Dbg.Log("ImageEdit prepare THREW: " + ex);
            SetStatus("Failed: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
        }
        finally
        {
            EndBusy();
        }
    }

    private void UpdatePreview()
    {
        if (!_loaded || _pixels is null) return;
        var src = _pixels;
        var srcW = _w;
        var srcH = _h;
        var settings = CaptureRenderSettings();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var (outPixels, outW, outH) = RenderEdited(src, srcW, srcH, settings);
                var palette = ComputePalette(outPixels, outW, outH);
                var bmp = ImageConverter.CreateSoftwareBitmapFromPixels(outPixels, outW, outH);
                DispatcherQueue.TryEnqueue(() =>
                {
                    _latestPixels = outPixels;
                    _latestW = outW;
                    _latestH = outH;
                    if (_bitmapSource is null)
                    {
                        _bitmapSource = new SoftwareBitmapSource();
                        _ = _bitmapSource.SetBitmapAsync(bmp);
                        _previewImage.Source = _bitmapSource;
                    }
                    else
                    {
                        _ = _bitmapSource.SetBitmapAsync(bmp);
                    }
                    UpdateLayerVisuals();
                    UpdatePaletteBar(palette);
                });
            }
            catch (Exception ex)
            {
                Dbg.Log("ImageEdit UpdatePreview THREW: " + ex);
            }
        });
    }

    // ---------------------------------------------------------------------
    // Color palette + eyedropper
    // ---------------------------------------------------------------------

    /// <summary>
    /// Extracts the dominant colors of the rendered image: quantized-count,
    /// then greedy near-duplicate filtering. Runs on the render thread.
    /// </summary>
    private static List<Windows.UI.Color> ComputePalette(byte[] px, uint w, uint h)
    {
        var result = new List<Windows.UI.Color>();
        try
        {
            if (px is null || w == 0 || h == 0) return result;
            var counts = new Dictionary<int, int>(1 << 14);
            var exact = new Dictionary<int, (byte B, byte G, byte R)>();

            int stride = (int)w * 4;
            int step = w * h > 300_000 ? 2 : 1; // sample large images
            for (int y = 0; y < h; y += step)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int i = row + x * 4;
                    byte a = px[i + 3];
                    if (a < 32) continue; // ignore transparent
                    int r = px[i + 2], g = px[i + 1], b = px[i];
                    int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    counts.TryGetValue(key, out int c);
                    counts[key] = c + 1;
                    if (!exact.ContainsKey(key)) exact[key] = ((byte)b, (byte)g, (byte)r);
                }
            }

            var candidates = counts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => (Key: kv.Key, Exact: exact[kv.Key]))
                .Take(400);

            foreach (var (_, (b, g, r)) in candidates)
            {
                bool tooClose = false;
                foreach (var chosen in result)
                {
                    int dr = r - chosen.R, dg = g - chosen.G, db = b - chosen.B;
                    if (dr * dr + dg * dg + db * db < 48 * 48) { tooClose = true; break; }
                }
                if (tooClose) continue;
                result.Add(Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, (byte)b));
                if (result.Count >= 12) break;
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("ComputePalette THREW: " + ex.Message);
        }
        return result;
    }

    /// <summary>Rebuilds the swatch circles under the image.</summary>
    private void UpdatePaletteBar(List<Windows.UI.Color> colors)
    {
        try
        {
            _paletteBar.Children.Clear();
            foreach (var c in colors)
            {
                var swatch = new Button
                {
                    Width = 24,
                    Height = 24,
                    MinWidth = 24,
                    MinHeight = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(c),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush("#5FFFFFFF"),
                    Margin = new Thickness(0, 0, 2, 2),
                };
                LocalizationUi.SetToolTip(swatch, "#{0} — click to copy the codes", $"{c.R:X2}{c.G:X2}{c.B:X2}");
                swatch.Click += (_, _) => CopySwatchColor(c, swatch);
                _paletteBar.Children.Add(swatch);
            }
        }
        catch (Exception ex)
        {
            Dbg.Log("UpdatePaletteBar THREW: " + ex.Message);
        }
    }

    /// <summary>Shows the HEX + RGB codes of a color in the code button.</summary>
    private void ShowColorCode(Windows.UI.Color c)
    {
        _currentColorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var label = (TextBlock)_paletteCodeButton.Content;
        label.Text = $"{_currentColorHex}   RGB({c.R}, {c.G}, {c.B})";
        // Code field takes the chosen color; keep the text readable on it.
        _paletteCodeButton.Background = new SolidColorBrush(c);
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 * (c.A / 255.0);
        label.Foreground = Brush(lum > 0.55 ? "#FF121418" : "#FFFFFFFF");
        _paletteCodeButton.BorderBrush = Brush("#66FFFFFF");
    }

    /// <summary>Copies the currently displayed HEX code to the clipboard.</summary>
    private void CopyColorCode()
    {
        if (string.IsNullOrEmpty(_currentColorHex))
        {
            SetStatus("Pick a color first — click a circle or use the dropper", MenuTheme.HintColor);
            return;
        }
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_currentColorHex);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatus("Copied {0} to clipboard", MenuTheme.AccentColor, _currentColorHex);
        ShowCopiedToast(_paletteCodeButton);
    }

    /// <summary>Copies the code and shows the small "Copied" alert near a swatch.</summary>
    private void CopySwatchColor(Windows.UI.Color c, FrameworkElement anchor)
    {
        ShowColorCode(c);
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_currentColorHex);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatus("Copied {0} to clipboard", MenuTheme.AccentColor, _currentColorHex);
        ShowCopiedToast(anchor);
    }

    /// <summary>Small transient "Copied!" alert floating just above a control.</summary>
    private void ShowCopiedToast(FrameworkElement anchor)
    {
        try
        {
            var popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup();
            popup.Child = new Border
            {
                Background = Brush(MenuTheme.AccentColor),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 5),
                Child = new TextBlock
                {
                    Text = "Copied ✔",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Brush("#FF121418"),
                },
            };
            LocalizationUi.Watch((FrameworkElement)popup.Child);
            var topLeft = anchor.TransformToVisual(Content as FrameworkElement).TransformPoint(new Point(0, 0));
            popup.HorizontalOffset = Math.Max(0, topLeft.X + (anchor.ActualWidth - 74) / 2);
            var vy = topLeft.Y - 34;
            popup.VerticalOffset = vy < 0 ? topLeft.Y + anchor.ActualHeight + 6 : vy;
            popup.IsOpen = true;

            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1100);
            timer.Tick += (s, _) => { popup.IsOpen = false; s.Stop(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            Dbg.Log("ShowCopiedToast THREW: " + ex.Message);
        }
    }

    /// <summary>Enters/exits eyedropper mode over the preview image.</summary>
    private void SetPickMode(bool on)
    {
        _pickMode = on;
        _pickCanvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _pickRing.Visibility = Visibility.Collapsed;
        _dropperButton.Background = Brush(on ? MenuTheme.AccentColor : "#0D000000");
        if (on)
        {
            _pickTargetCursor ??= Limona.Interop.CursorInterop.CreateTargetCursor();
            Limona.Interop.CursorInterop.SetCursor(_pickCanvas, _pickTargetCursor);
            if (_cropMode) ToggleCropMode(); // can't crop and pick at the same time
            SetStatus("Color picker: move over the image, click to take the color", MenuTheme.AccentColor);
        }
        else
        {
            Limona.Interop.CursorInterop.SetCursor(_pickCanvas, null);
        }
    }

    /// <summary>Maps a point on the preview canvas to a pixel of the rendered image.</summary>
    private (Windows.UI.Color C, int X, int Y)? SampleDisplayPixel(Point pos)
    {
        if (_latestPixels is null || _latestW == 0 || _latestH == 0) return null;
        var display = GetLayerDisplayRect();
        if (display.Width <= 0 || display.Height <= 0) return null;
        double fx = (pos.X - display.X) / display.Width;
        double fy = (pos.Y - display.Y) / display.Height;
        if (fx < 0 || fx >= 1 || fy < 0 || fy >= 1) return null;
        int x = Math.Clamp((int)(fx * _latestW), 0, (int)_latestW - 1);
        int y = Math.Clamp((int)(fy * _latestH), 0, (int)_latestH - 1);
        int i = (int)(y * _latestW + x) * 4;
        byte b = _latestPixels[i], g = _latestPixels[i + 1], r = _latestPixels[i + 2], a = _latestPixels[i + 3];
        return (Windows.UI.Color.FromArgb(a, r, g, b), x, y);
    }

    private void OnPickPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pickMode) return;
        var pos = e.GetCurrentPoint(_pickCanvas).Position;
        var sample = SampleDisplayPixel(pos);
        if (sample is null) { _pickRing.Visibility = Visibility.Collapsed; return; }
        var (c, _, _) = sample.Value;
        _pickRing.Fill = new SolidColorBrush(c);
        Canvas.SetLeft(_pickRing, pos.X - 9);
        Canvas.SetTop(_pickRing, pos.Y - 9);
        _pickRing.Visibility = Visibility.Visible;
        ShowColorCode(c); // live codes while hovering
        e.Handled = true;
    }

    private void OnPickPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_pickMode) return;
        _pickCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPickPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pickMode) return;
        var pos = e.GetCurrentPoint(_pickCanvas).Position;
        var sample = SampleDisplayPixel(pos);
        try { _pickCanvas.ReleasePointerCapture(e.Pointer); } catch { }
        if (sample is null)
        {
            // Clicked outside the image: leave pick mode.
            SetPickMode(false);
            SetStatus("", MenuTheme.HintColor);
            return;
        }
        var (c, _, _) = sample.Value;
        ShowColorCode(c);
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_currentColorHex);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        SetStatus("Copied #{0} from the image", MenuTheme.AccentColor, $"{c.R:X2}{c.G:X2}{c.B:X2}");
        ShowCopiedToast(_pickRing);
        SetPickMode(false);
        e.Handled = true;
    }

    private EditSettings CaptureRenderSettings() => new(
        _brightness, _contrast, _saturation,
        _exposure, _highlights, _shadows,
        _hueAdjustments.ToArray(),
        _rotation, _flipH, _flipV,
        _hasCrop, _cropX, _cropY, _cropW, _cropH,
        _layers.Select(layer => new LayerSnapshot(layer.Text?.Clone(), layer.Image?.Clone())).ToArray());

    private static EditSettings ScaleCrop(EditSettings settings, uint sourceW, uint sourceH, uint targetW, uint targetH)
    {
        if (!settings.HasCrop) return settings;

        var scaleX = targetW / (double)sourceW;
        var scaleY = targetH / (double)sourceH;
        var x = Math.Clamp((int)Math.Floor(settings.CropX * scaleX), 0, (int)targetW - 1);
        var y = Math.Clamp((int)Math.Floor(settings.CropY * scaleY), 0, (int)targetH - 1);
        var right = Math.Clamp((int)Math.Ceiling((settings.CropX + settings.CropW) * scaleX), x + 1, (int)targetW);
        var bottom = Math.Clamp((int)Math.Ceiling((settings.CropY + settings.CropH) * scaleY), y + 1, (int)targetH);
        return settings with { CropX = x, CropY = y, CropW = right - x, CropH = bottom - y };
    }

    private (byte[] Pixels, uint W, uint H) RenderEdited(byte[] src, uint srcW, uint srcH, EditSettings settings)
    {
        uint x0 = 0, y0 = 0, cw = srcW, ch = srcH;
        if (settings.HasCrop)
        {
            x0 = (uint)Math.Clamp(settings.CropX, 0, (int)srcW - 1);
            y0 = (uint)Math.Clamp(settings.CropY, 0, (int)srcH - 1);
            cw = (uint)Math.Clamp(settings.CropW, 1, (int)(srcW - x0));
            ch = (uint)Math.Clamp(settings.CropH, 1, (int)(srcH - y0));
        }

        var cropped = new byte[cw * ch * 4];
        for (uint y = 0; y < ch; y++)
        {
            var srcRow = (int)((y0 + y) * srcW * 4 + x0 * 4);
            var dstRow = (int)(y * cw * 4);
            System.Buffer.BlockCopy(src, srcRow, cropped, dstRow, (int)(cw * 4));
        }

        uint outW, outH;
        if (settings.Rotation == 90 || settings.Rotation == 270) { outW = ch; outH = cw; }
        else { outW = cw; outH = ch; }

        var rotated = new byte[outW * outH * 4];
        for (uint dy = 0; dy < outH; dy++)
        for (uint dx = 0; dx < outW; dx++)
        {
            uint fx = settings.FlipH ? (outW - 1 - dx) : dx;
            uint fy = settings.FlipV ? (outH - 1 - dy) : dy;
            uint sx, sy;
            if (settings.Rotation == 0) { sx = fx; sy = fy; }
            else if (settings.Rotation == 90) { sx = outH - 1 - fy; sy = fx; }
            else if (settings.Rotation == 180) { sx = outW - 1 - fx; sy = outH - 1 - fy; }
            else { sx = fy; sy = outW - 1 - fx; }
            var si = (int)((sy * cw + sx) * 4);
            var di = (int)((dy * outW + dx) * 4);
            rotated[di] = cropped[si];
            rotated[di + 1] = cropped[si + 1];
            rotated[di + 2] = cropped[si + 2];
            rotated[di + 3] = cropped[si + 3];
        }

        var brightness = settings.Brightness / 100.0 * 255;
        var contrastFactor = (settings.Contrast + 100) / 100.0;
        var saturation = (settings.Saturation + 100) / 100.0;
        var exposureFactor = Math.Pow(2, settings.Exposure);

        var result = new byte[rotated.Length];
        for (int i = 0; i < rotated.Length; i += 4)
        {
            double r = rotated[i + 2];
            double g = rotated[i + 1];
            double b = rotated[i + 0];

            r += brightness; g += brightness; b += brightness;
            r = (r - 128) * contrastFactor + 128;
            g = (g - 128) * contrastFactor + 128;
            b = (b - 128) * contrastFactor + 128;
            var gray = 0.299 * r + 0.587 * g + 0.114 * b;
            r = gray + (r - gray) * saturation;
            g = gray + (g - gray) * saturation;
            b = gray + (b - gray) * saturation;

            r = Math.Clamp(r * exposureFactor, 0, 255);
            g = Math.Clamp(g * exposureFactor, 0, 255);
            b = Math.Clamp(b * exposureFactor, 0, 255);
            var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
            var shadowAmount = settings.Shadows / 100.0 * Math.Pow(1 - luminance, 2);
            var highlightAmount = settings.Highlights / 100.0 * Math.Pow(luminance, 2);
            var lightAmount = shadowAmount + highlightAmount;
            r = ApplyLightAdjustment(r, lightAmount);
            g = ApplyLightAdjustment(g, lightAmount);
            b = ApplyLightAdjustment(b, lightAmount);

            if (settings.HueAdjustments.Length > 0)
            {
                RgbToHsl(r, g, b, out var baseHue, out var hslSaturation, out var lightness);
                if (hslSaturation > 0.015)
                {
                    var hue = baseHue;
                    for (int range = 0; range < settings.HueAdjustments.Length; range++)
                    {
                        var adjustment = settings.HueAdjustments[range];
                        if (adjustment.HueShift == 0 && adjustment.Saturation == 0 && adjustment.Lightness == 0)
                            continue;
                        var weight = GetColorRangeWeight(baseHue, range);
                        if (weight <= 0) continue;

                        hue = (hue + adjustment.HueShift * weight + 360) % 360;
                        var saturationAdjustment = adjustment.Saturation / 100.0 * weight;
                        hslSaturation = saturationAdjustment >= 0
                            ? hslSaturation + (1 - hslSaturation) * saturationAdjustment
                            : hslSaturation * (1 + saturationAdjustment);
                        var lightnessAdjustment = adjustment.Lightness / 100.0 * weight;
                        lightness = lightnessAdjustment >= 0
                            ? lightness + (1 - lightness) * lightnessAdjustment
                            : lightness * (1 + lightnessAdjustment);
                    }
                    HslToRgb(hue, hslSaturation, lightness, out r, out g, out b);
                }
            }

            result[i + 0] = (byte)Math.Clamp(b, 0, 255);
            result[i + 1] = (byte)Math.Clamp(g, 0, 255);
            result[i + 2] = (byte)Math.Clamp(r, 0, 255);
            result[i + 3] = rotated[i + 3];
        }

        return (result, outW, outH);
    }

    private static double ApplyLightAdjustment(double value, double amount) =>
        amount >= 0 ? value + (255 - value) * amount : value * (1 + amount);

    private static double GetColorRangeWeight(double hue, int rangeIndex)
    {
        var delta = Math.Abs(hue - ColorRangeCenters[rangeIndex]);
        delta = Math.Min(delta, 360 - delta);
        var t = Math.Clamp(delta / 34, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    private static void RgbToHsl(double red, double green, double blue, out double hue, out double saturation, out double lightness)
    {
        red /= 255;
        green /= 255;
        blue /= 255;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        lightness = (max + min) / 2;
        if (delta <= 0.000001)
        {
            hue = 0;
            saturation = 0;
            return;
        }

        saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        if (max == red)
            hue = 60 * (((green - blue) / delta) % 6);
        else if (max == green)
            hue = 60 * ((blue - red) / delta + 2);
        else
            hue = 60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
    }

    private static void HslToRgb(double hue, double saturation, double lightness, out double red, out double green, out double blue)
    {
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var match = lightness - chroma / 2;
        double r, g, b;
        if (hue < 60) { r = chroma; g = x; b = 0; }
        else if (hue < 120) { r = x; g = chroma; b = 0; }
        else if (hue < 180) { r = 0; g = chroma; b = x; }
        else if (hue < 240) { r = 0; g = x; b = chroma; }
        else if (hue < 300) { r = x; g = 0; b = chroma; }
        else { r = chroma; g = 0; b = x; }
        red = (r + match) * 255;
        green = (g + match) * 255;
        blue = (b + match) * 255;
    }

    private void ToggleCropMode()
    {
        _cropMode = !_cropMode;
        if (_cropMode)
        {
            _cropModeButton.Background = Brush(MenuTheme.AccentColor);
            _cropModeButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18));
            _cropRectVisual.Visibility = Visibility.Visible;
            _applyCropButton.IsEnabled = true;
            _cropStart = new Point(0, 0);
            _cropEnd = new Point(_w, _h);
            _cropX = 0; _cropY = 0; _cropW = (int)_w; _cropH = (int)_h;
            _hasCrop = true;
            UpdateCropVisual();
            UpdateHandles();
            UpdateLayerVisuals();
            SetCropCursor(CropCrossCursor);
            SetStatus("Drag corners/edges to resize, drag center to move", MenuTheme.HintColor);
        }
        else
        {
            _cropModeButton.Background = Brush(MenuTheme.HubFill);
            _cropModeButton.Foreground = Brush(MenuTheme.LabelColor);
            _cropRectVisual.Visibility = Visibility.Collapsed;
            HideHandles();
            HideCropShades();
            UpdateLayerVisuals();
            _applyCropButton.IsEnabled = false;
            SetCropCursor(CropArrowCursor);
            SetStatus("", MenuTheme.HintColor);
        }
    }

    private void OnCropPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropMode || !_loaded) return;
        var pos = e.GetCurrentPoint(_cropCanvas).Position;
        var imgPos = ScreenToImage(pos);
        if (imgPos is null)
        {
            UpdateCropCursor(pos);
            return;
        }

        string handle = GetHandleAt(pos);
        if (!string.IsNullOrEmpty(handle))
        {
            _resizing = true;
            _resizeHandle = handle;
            _resizeStart = pos;
            _resizeStartX = _cropX; _resizeStartY = _cropY;
            _resizeStartW = _cropW; _resizeStartH = _cropH;
            SetCropCursor(GetResizeCursor(handle));
            _cropCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        var (rx, ry, rw, rh) = ImageToScreen(_cropX, _cropY, _cropW, _cropH);
        if (pos.X >= rx && pos.X <= rx + rw && pos.Y >= ry && pos.Y <= ry + rh)
        {
            _moving = true;
            _moveStart = pos;
            _moveStartX = _cropX; _moveStartY = _cropY;
            SetCropCursor(CropMoveCursor);
            _cropCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        _cropDragging = true;
        _cropStart = imgPos.Value;
        _cropEnd = imgPos.Value;
        SetCropCursor(CropCrossCursor);
        _cropCanvas.CapturePointer(e.Pointer);
        UpdateCropVisual();
        e.Handled = true;
    }

    private void OnCropPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropMode)
        {
            SetCropCursor(CropArrowCursor);
            return;
        }
        var pos = e.GetCurrentPoint(_cropCanvas).Position;

        if (_resizing)
        {
            SetCropCursor(GetResizeCursor(_resizeHandle));
            ResizeCrop(pos);
            UpdateCropVisual();
            UpdateHandles();
            e.Handled = true;
            return;
        }

        if (_moving)
        {
            SetCropCursor(CropMoveCursor);
            var bounds = GetImageDisplayRect();
            if (bounds.Width <= 0) return;
            int dx = (int)Math.Round((pos.X - _moveStart.X) / bounds.Width * _w);
            int dy = (int)Math.Round((pos.Y - _moveStart.Y) / bounds.Height * _h);
            _cropX = Math.Clamp(_moveStartX + dx, 0, (int)_w - _cropW);
            _cropY = Math.Clamp(_moveStartY + dy, 0, (int)_h - _cropH);
            UpdateCropVisual();
            UpdateHandles();
            e.Handled = true;
            return;
        }

        if (_cropDragging)
        {
            SetCropCursor(CropCrossCursor);
            _cropEnd = ScreenToImageClamped(pos);
            UpdateCropVisual();
            e.Handled = true;
            return;
        }

        UpdateCropCursor(pos);
    }

    private void OnCropPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_cropCanvas).Position;
        if (_cropDragging)
        {
            _cropDragging = false;
            FinalizeCropDrag();
        }
        if (_resizing) { _resizing = false; _resizeHandle = ""; }
        if (_moving) { _moving = false; }
        try { _cropCanvas.ReleasePointerCapture(e.Pointer); } catch { }
        UpdateCropCursor(pos);
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_cropDragging) { _cropDragging = false; FinalizeCropDrag(); }
        _resizing = false;
        _resizeHandle = "";
        _moving = false;
        SetCropCursor(CropArrowCursor);
    }

    private void FinalizeCropDrag()
    {
        var (x, y, w, h) = GetCropRect();
        if (w < 5 || h < 5)
        {
            _cropRectVisual.Visibility = Visibility.Collapsed;
            HideHandles();
            HideCropShades();
            _applyCropButton.IsEnabled = false;
            return;
        }
        _cropX = x; _cropY = y; _cropW = w; _cropH = h;
        _hasCrop = true;
        _applyCropButton.IsEnabled = true;
        UpdateCropVisual();
        UpdateHandles();
        SetStatus("Crop: {0} x {1} at ({2},{3}) - drag handles to adjust, Apply to commit",
            MenuTheme.HintColor, w, h, x, y);
    }

    private void ResizeCrop(Point pos)
    {
        var bounds = GetImageDisplayRect();
        if (bounds.Width <= 0) return;
        int dx = (int)Math.Round((pos.X - _resizeStart.X) / bounds.Width * _w);
        int dy = (int)Math.Round((pos.Y - _resizeStart.Y) / bounds.Height * _h);

        int newX = _resizeStartX, newY = _resizeStartY, newW = _resizeStartW, newH = _resizeStartH;

        switch (_resizeHandle)
        {
            case "NW": newX += dx; newY += dy; newW -= dx; newH -= dy; break;
            case "N": newY += dy; newH -= dy; break;
            case "NE": newW += dx; newY += dy; newH -= dy; break;
            case "E": newW += dx; break;
            case "SE": newW += dx; newH += dy; break;
            case "S": newH += dy; break;
            case "SW": newX += dx; newW -= dx; newH += dy; break;
            case "W": newX += dx; newW -= dx; break;
        }

        if (newW < 10) { if (_resizeHandle.Contains("W")) newX = _resizeStartX + _resizeStartW - 10; newW = 10; }
        if (newH < 10) { if (_resizeHandle.Contains("N")) newY = _resizeStartY + _resizeStartH - 10; newH = 10; }
        if (newX < 0) { newW += newX; newX = 0; }
        if (newY < 0) { newH += newY; newY = 0; }
        if (newX + newW > (int)_w) newW = (int)_w - newX;
        if (newY + newH > (int)_h) newH = (int)_h - newY;

        _cropX = newX; _cropY = newY; _cropW = newW; _cropH = newH;
    }

    private string GetHandleAt(Point pos)
    {
        var (rx, ry, rw, rh) = ImageToScreen(_cropX, _cropY, _cropW, _cropH);
        var nearestDistance = CropHandleHitRadius * CropHandleHitRadius;
        var nearestHandle = "";
        for (int i = 0; i < 8; i++)
        {
            var handle = GetCropHandlePoint(i, rx, ry, rw, rh);
            var dx = pos.X - handle.X;
            var dy = pos.Y - handle.Y;
            var distance = dx * dx + dy * dy;
            if (distance <= nearestDistance)
            {
                nearestDistance = distance;
                nearestHandle = CropHandleNames[i];
            }
        }
        return nearestHandle;
    }

    private void UpdateHandles()
    {
        if (!_cropMode) { HideHandles(); return; }
        var (rx, ry, rw, rh) = ImageToScreen(_cropX, _cropY, _cropW, _cropH);
        for (int i = 0; i < 8; i++)
        {
            var handle = GetCropHandlePoint(i, rx, ry, rw, rh);
            Canvas.SetLeft(_handles[i], handle.X - 6);
            Canvas.SetTop(_handles[i], handle.Y - 6);
            _handles[i].Visibility = Visibility.Visible;
        }
    }

    private static Point GetCropHandlePoint(int index, double x, double y, double width, double height) => index switch
    {
        0 => new Point(x, y),
        1 => new Point(x + width / 2, y),
        2 => new Point(x + width, y),
        3 => new Point(x + width, y + height / 2),
        4 => new Point(x + width, y + height),
        5 => new Point(x + width / 2, y + height),
        6 => new Point(x, y + height),
        _ => new Point(x, y + height / 2),
    };

    private static InputCursor GetResizeCursor(string handle) => handle switch
    {
        "NW" or "SE" => CropResizeDiagonalDownCursor,
        "NE" or "SW" => CropResizeDiagonalUpCursor,
        "N" or "S" => CropResizeVerticalCursor,
        _ => CropResizeHorizontalCursor,
    };

    private void UpdateCropCursor(Point pos)
    {
        if (!_cropMode || !_loaded)
        {
            SetCropCursor(CropArrowCursor);
            return;
        }

        var handle = GetHandleAt(pos);
        if (handle.Length > 0)
            SetCropCursor(GetResizeCursor(handle));
        else if (IsInsideCrop(pos))
            SetCropCursor(CropMoveCursor);
        else
            SetCropCursor(CropCrossCursor);
    }

    private bool IsInsideCrop(Point pos)
    {
        var (x, y, width, height) = ImageToScreen(_cropX, _cropY, _cropW, _cropH);
        return pos.X >= x && pos.X <= x + width && pos.Y >= y && pos.Y <= y + height;
    }

    private void SetCropCursor(InputCursor? cursor) => CursorInterop.SetCursor(_cropCanvas, cursor);

    private void HideHandles()
    {
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
    }

    private (int X, int Y, int W, int H) GetCropRect()
    {
        var x1 = (int)Math.Min(_cropStart.X, _cropEnd.X);
        var y1 = (int)Math.Min(_cropStart.Y, _cropEnd.Y);
        var x2 = (int)Math.Max(_cropStart.X, _cropEnd.X);
        var y2 = (int)Math.Max(_cropStart.Y, _cropEnd.Y);
        return (x1, y1, x2 - x1, y2 - y1);
    }

    private void UpdateCropVisual()
    {
        (int x, int y, int w, int h) rect;
        if (_cropDragging)
        {
            rect = GetCropRect();
        }
        else
        {
            rect = (_cropX, _cropY, _cropW, _cropH);
        }
        if (rect.w < 2 || rect.h < 2)
        {
            _cropRectVisual.Visibility = Visibility.Collapsed;
            HideCropShades();
            return;
        }
        var (rx, ry, rw, rh) = ImageToScreen(rect.x, rect.y, rect.w, rect.h);
        Canvas.SetLeft(_cropRectVisual, rx);
        Canvas.SetTop(_cropRectVisual, ry);
        _cropRectVisual.Width = rw;
        _cropRectVisual.Height = rh;
        _cropRectVisual.Visibility = Visibility.Visible;

        var canvasWidth = _cropCanvas.ActualWidth;
        var canvasHeight = _cropCanvas.ActualHeight;
        var right = Math.Clamp(rx + rw, 0, canvasWidth);
        var bottom = Math.Clamp(ry + rh, 0, canvasHeight);
        SetCropShade(0, 0, 0, canvasWidth, Math.Clamp(ry, 0, canvasHeight));
        SetCropShade(1, 0, bottom, canvasWidth, canvasHeight - bottom);
        SetCropShade(2, 0, Math.Clamp(ry, 0, canvasHeight), Math.Clamp(rx, 0, canvasWidth), Math.Max(0, bottom - Math.Clamp(ry, 0, canvasHeight)));
        SetCropShade(3, right, Math.Clamp(ry, 0, canvasHeight), canvasWidth - right, Math.Max(0, bottom - Math.Clamp(ry, 0, canvasHeight)));
    }

    private void SetCropShade(int index, double x, double y, double width, double height)
    {
        var shade = _cropShades[index];
        if (width <= 0 || height <= 0)
        {
            shade.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(shade, x);
        Canvas.SetTop(shade, y);
        shade.Width = width;
        shade.Height = height;
        shade.Visibility = Visibility.Visible;
    }

    private void HideCropShades()
    {
        foreach (var shade in _cropShades)
            shade.Visibility = Visibility.Collapsed;
    }

    private Point? ScreenToImage(Point screen)
    {
        var bounds = GetImageDisplayRect();
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var relX = (screen.X - bounds.X) / bounds.Width;
        var relY = (screen.Y - bounds.Y) / bounds.Height;
        if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return null;
        var ix = (int)(relX * _w);
        var iy = (int)(relY * _h);
        ix = Math.Clamp(ix, 0, (int)_w - 1);
        iy = Math.Clamp(iy, 0, (int)_h - 1);
        return new Point(ix, iy);
    }

    private Point ScreenToImageClamped(Point screen)
    {
        var bounds = GetImageDisplayRect();
        if (bounds.Width <= 0 || bounds.Height <= 0) return new Point(0, 0);
        var relX = Math.Clamp((screen.X - bounds.X) / bounds.Width, 0, 1);
        var relY = Math.Clamp((screen.Y - bounds.Y) / bounds.Height, 0, 1);
        var ix = Math.Clamp((int)Math.Round(relX * _w), 0, (int)_w - 1);
        var iy = Math.Clamp((int)Math.Round(relY * _h), 0, (int)_h - 1);
        return new Point(ix, iy);
    }

    private (double X, double Y, double W, double H) ImageToScreen(int ix, int iy, int iw, int ih)
    {
        var bounds = GetImageDisplayRect();
        if (bounds.Width <= 0 || bounds.Height <= 0) return (0, 0, 0, 0);
        var x = bounds.X + (double)ix / _w * bounds.Width;
        var y = bounds.Y + (double)iy / _h * bounds.Height;
        var w = (double)iw / _w * bounds.Width;
        var h = (double)ih / _h * bounds.Height;
        return (x, y, w, h);
    }

    private Rect GetImageDisplayRect()
    {
        if (_previewImage.ActualWidth <= 0 || _previewImage.ActualHeight <= 0) return new Rect(0, 0, 0, 0);
        var hostW = _previewHost.ActualWidth;
        var hostH = _previewHost.ActualHeight;
        var imgAspect = (double)_w / _h;
        var hostAspect = hostW / hostH;
        double dispW, dispH, offsetX, offsetY;
        if (imgAspect > hostAspect)
        {
            dispW = hostW;
            dispH = hostW / imgAspect;
            offsetX = 0;
            offsetY = (hostH - dispH) / 2;
        }
        else
        {
            dispH = hostH;
            dispW = hostH * imgAspect;
            offsetX = (hostW - dispW) / 2;
            offsetY = 0;
        }
        return new Rect(offsetX, offsetY, dispW, dispH);
    }

    private void ApplyCrop()
    {
        if (_cropW < 1 || _cropH < 1) return;
        _hasCrop = true;
        _cropRectVisual.Visibility = Visibility.Collapsed;
        HideHandles();
        HideCropShades();
        _cropMode = false;
        _cropModeButton.Background = Brush(MenuTheme.HubFill);
        _cropModeButton.Foreground = Brush(MenuTheme.LabelColor);
        _applyCropButton.IsEnabled = false;
        UpdatePreview();
        UpdateLayerVisuals();
        SetStatus("Cropped to {0} x {1}", "#FF9BE29B", _cropW, _cropH);
    }

    private void ResetAll()
    {
        _brightness = 0; _contrast = 0; _saturation = 0;
        _exposure = 0; _highlights = 0; _shadows = 0;
        _hueRange = 0; _hueShift = 0; _hueSaturation = 0; _hueLightness = 0;
        _rotation = 0; _flipH = false; _flipV = false;
        _hasCrop = false;
        _cropMode = false;
        _cropRectVisual.Visibility = Visibility.Collapsed;
        HideHandles();
        HideCropShades();
        _cropModeButton.Background = Brush(MenuTheme.HubFill);
        _cropModeButton.Foreground = Brush(MenuTheme.LabelColor);
        _applyCropButton.IsEnabled = false;
        _brightnessSlider.Value = 0; _contrastSlider.Value = 0; _saturationSlider.Value = 0;
        _exposureSlider.Value = 0; _highlightsSlider.Value = 0; _shadowsSlider.Value = 0;
        Array.Clear(_hueAdjustments);
        _hueSlider.Value = 0; _hueSaturationSlider.Value = 0; _hueLightnessSlider.Value = 0;
        _hueValue.Text = "0"; _hueSaturationValue.Text = "0"; _hueLightnessValue.Text = "0";
        UpdateColorRangeSelection();
        _layers.Clear();
        _layerList.SelectedItem = null;
        SelectLayer(null);
        UpdateLayerVisuals();
        UpdatePreview();
        SetStatus("Reset", MenuTheme.HintColor);
    }

    private void AddTextLayer()
    {
        var value = _layerTextInput.Text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            SetStatus("Enter text before adding a layer.", MenuTheme.AccentDeep);
            return;
        }

        var text = new TextOverlay
        {
            Text = value,
            FontSizeFraction = 0.08,
            WidthFraction = 0.9,
            XFraction = 0.5,
            YFraction = 0.5,
            ColorArgb = 0xFFFFFFFF,
            OutlineArgb = 0xC0000000,
            Opacity = 1,
        };
        var layer = new ImageLayerItem { LabelSource = value, Text = text };
        layer.Name = GetLayerDisplayName(layer);
        AddLayer(layer);
    }

    private async System.Threading.Tasks.Task PickLayerPictureAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                AddPictureLayer(file.Path, System.IO.Path.GetFileNameWithoutExtension(file.Name));
        }
        catch (Exception ex)
        {
            Dbg.Log("ImageEdit add picture THREW: " + ex);
            SetStatus("Could not add that picture: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
        }
    }

    private void AddPictureLayer(string path, string name)
    {
        try
        {
            using var image = System.Drawing.Image.FromFile(path);
            var overlay = new ImageOverlay
            {
                Path = path,
                XFraction = 0.5,
                YFraction = 0.5,
                WidthFraction = 0.3,
                Opacity = 1,
            };
            var layer = new ImageLayerItem
            {
                LabelSource = name,
                Image = overlay,
                AspectRatio = (double)image.Width / image.Height,
            };
            layer.Name = GetLayerDisplayName(layer);
            AddLayer(layer);
        }
        catch (Exception ex)
        {
            Dbg.Log("ImageEdit load layer THREW: " + ex);
            SetStatus("Could not load that picture: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
        }
    }

    private static string ShortLayerName(string name) => name.Length <= 22 ? name : name[..19] + "...";

    private void AddLayer(ImageLayerItem layer)
    {
        _layers.Add(layer);
        _layerList.SelectedItem = layer;
        UpdateLayerVisuals();
        SetStatus("Layer added. Drag it on the preview to position it.", "#FF9BE29B");
    }

    /// <summary>Fills the font combo with the requested families that actually
    /// exist on this machine (bundled pack + anything the user installed),
    /// preserving the Latin/Arabic order, then common system fallbacks.</summary>
    private void PopulateFontCombo()
    {
        var installed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var collection = new System.Drawing.Text.InstalledFontCollection();
            foreach (var family in collection.Families)
                installed.Add(family.Name);
        }
        catch (Exception ex)
        {
            Dbg.Log("Font enumeration failed: " + ex.Message);
        }

        var added = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (display, families) in PreferredFonts)
        {
            var family = families.FirstOrDefault(installed.Contains);
            if (family is null || !added.Add(family)) continue;
            _layerFontCombo.Items.Add(MakeFontItem(display, family));
        }

        foreach (var family in BaseFonts)
        {
            if (!installed.Contains(family) || !added.Add(family)) continue;
            _layerFontCombo.Items.Add(MakeFontItem(family, family));
        }
    }

    private static ComboBoxItem MakeFontItem(string display, string family) => new()
    {
        Content = display,
        Tag = family,
        FontFamily = new FontFamily(family),
        FontSize = 14,
    };

    private void SelectFontInCombo(string family)
    {
        foreach (var item in _layerFontCombo.Items)
        {
            if (item is ComboBoxItem { Tag: string candidate } &&
                string.Equals(candidate, family, StringComparison.OrdinalIgnoreCase))
            {
                _layerFontCombo.SelectedItem = item;
                return;
            }
        }

        // Legacy/hand-edited layer with a family outside the list: keep it usable.
        var extra = MakeFontItem(family, family);
        _layerFontCombo.Items.Add(extra);
        _layerFontCombo.SelectedItem = extra;
    }

    private void SelectLayer(ImageLayerItem? layer)
    {
        _selectedLayer = layer;
        _updatingLayerControls = true;
        var textLayer = layer?.Text;
        _layerFontCombo.IsEnabled = textLayer is not null;
        _layerWeightCombo.IsEnabled = textLayer is not null;
        _layerColorSwatch.Button.IsEnabled = textLayer is not null;
        _layerStrokeSwatch.Button.IsEnabled = textLayer is not null;
        _layerStrokeSlider.IsEnabled = textLayer is not null;
        _layerItalicCheckBox.IsEnabled = textLayer is not null;
        _layerOutlineCheckBox.IsEnabled = textLayer is not null;
        _layerSizeSlider.IsEnabled = layer is not null;
        _layerOpacitySlider.IsEnabled = layer is not null;
        _removeLayerButton.IsEnabled = layer is not null;
        if (layer is not null)
        {
            _layerSizeSlider.Value = layer.Text is not null
                ? layer.Text.FontSizeFraction * 100
                : layer.Image!.WidthFraction * 100;
            _layerOpacitySlider.Value = (layer.Text?.Opacity ?? layer.Image!.Opacity) * 100;
            _layerSizeValue.Text = ((int)_layerSizeSlider.Value).ToString();
            _layerOpacityValue.Text = $"{(int)_layerOpacitySlider.Value}%";
        }
        if (textLayer is not null)
        {
            _layerTextInput.Text = textLayer.Text;
            SelectFontInCombo(textLayer.FontFamily);
            _layerItalicCheckBox.IsChecked = textLayer.Italic;
            _layerOutlineCheckBox.IsChecked = (textLayer.OutlineArgb >> 24) > 0;
            for (int i = 0; i < _layerWeightCombo.Items.Count; i++)
            {
                if (_layerWeightCombo.Items[i] is ComboBoxItem item && Convert.ToInt32(item.Tag) == textLayer.FontWeight)
                {
                    _layerWeightCombo.SelectedIndex = i;
                    break;
                }
            }
            _textColorArgb = textLayer.ColorArgb;
            _layerColorSwatch.SetColor(textLayer.ColorArgb);
            _strokeColorArgb = (textLayer.OutlineArgb >> 24) > 0 ? textLayer.OutlineArgb : 0xC0000000;
            _layerStrokeSwatch.SetColor(_strokeColorArgb);
            _layerStrokeSlider.Value = Math.Clamp(textLayer.StrokeFraction * 100, 1, 30);
            _layerStrokeValue.Text = $"{Math.Round(textLayer.StrokeFraction * 100):0}%";
        }
        _updatingLayerControls = false;
        UpdateLayerVisuals();
    }

    private void OnLayerTextChanged()
    {
        if (_updatingLayerControls || _selectedLayer?.Text is not { } text) return;
        text.Text = _layerTextInput.Text;
        _selectedLayer.LabelSource = text.Text;
        _selectedLayer.Name = GetLayerDisplayName(_selectedLayer);
        UpdateLayerVisuals();
    }

    private void OnLayerTypographyChanged()
    {
        if (_updatingLayerControls || _selectedLayer?.Text is not { } text) return;
        if (_layerFontCombo.SelectedItem is ComboBoxItem { Tag: string family })
            text.FontFamily = family;
        if (_layerWeightCombo.SelectedItem is ComboBoxItem weight && weight.Tag is not null)
        {
            text.FontWeight = Convert.ToInt32(weight.Tag);
            text.Bold = text.FontWeight >= 600;
        }
        text.ColorArgb = _textColorArgb;
        text.Italic = _layerItalicCheckBox.IsChecked == true;
        text.OutlineArgb = _layerOutlineCheckBox.IsChecked == true ? _strokeColorArgb : 0;
        text.StrokeFraction = _layerStrokeSlider.Value / 100.0;
        UpdateLayerVisuals();
    }

    private void OnLayerSizeChanged()
    {
        if (_updatingLayerControls || _selectedLayer is null) return;
        if (_selectedLayer.Text is not null)
            _selectedLayer.Text.FontSizeFraction = _layerSizeSlider.Value / 100;
        else if (_selectedLayer.Image is not null)
            _selectedLayer.Image.WidthFraction = _layerSizeSlider.Value / 100;
        _layerSizeValue.Text = ((int)_layerSizeSlider.Value).ToString();
        UpdateLayerVisuals();
    }

    private void OnLayerStrokeWidthChanged()
    {
        _layerStrokeValue.Text = $"{Math.Round(_layerStrokeSlider.Value):0}%";
        if (_updatingLayerControls || _selectedLayer?.Text is not { } text) return;
        text.StrokeFraction = _layerStrokeSlider.Value / 100.0;
        UpdateLayerVisuals();
    }

    private void OnLayerOpacityChanged()
    {
        if (_updatingLayerControls || _selectedLayer is null) return;
        var opacity = _layerOpacitySlider.Value / 100;
        if (_selectedLayer.Text is not null) _selectedLayer.Text.Opacity = opacity;
        if (_selectedLayer.Image is not null) _selectedLayer.Image.Opacity = opacity;
        _layerOpacityValue.Text = $"{(int)_layerOpacitySlider.Value}%";
        UpdateLayerVisuals();
    }

    private void RemoveSelectedLayer()
    {
        if (_selectedLayer is null) return;
        _layers.Remove(_selectedLayer);
        _layerList.SelectedItem = null;
        SelectLayer(null);
        UpdateLayerVisuals();
    }

    private void OnLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_loaded || _cropMode) return;
        var position = e.GetCurrentPoint(_layerCanvas).Position;

        if (_selectedLayer is not null)
        {
            var handleIndex = GetLayerHandleAt(position);
            if (handleIndex >= 0)
            {
                _layerResizing = true;
                _layerResizeHandle = handleIndex;
                _layerResizeStart = position;
                _layerResizeStartBounds = GetLayerBounds(_selectedLayer, GetLayerDisplayRect());
                _layerStartSize = _selectedLayer.Text?.FontSizeFraction ?? _selectedLayer.Image!.WidthFraction;
                CursorInterop.SetCursor(_layerCanvas, GetResizeCursor(CropHandleNames[handleIndex]));
                _layerCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
        }

        var layer = HitTestLayer(position);
        if (layer is null)
        {
            _layerList.SelectedItem = null;
            SelectLayer(null);
            return;
        }
        _layerList.SelectedItem = layer;
        _layerDragStart = position;
        _layerStartX = layer.Text?.XFraction ?? layer.Image!.XFraction;
        _layerStartY = layer.Text?.YFraction ?? layer.Image!.YFraction;
        _layerDragging = true;
        CursorInterop.SetCursor(_layerCanvas, CropMoveCursor);
        _layerCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnLayerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(_layerCanvas).Position;
        if (!_layerDragging && !_layerResizing)
        {
            var handleIndex = GetLayerHandleAt(position);
            if (handleIndex >= 0)
                CursorInterop.SetCursor(_layerCanvas, GetResizeCursor(CropHandleNames[handleIndex]));
            else if (_selectedLayer is not null && IsInsideLayer(position, _selectedLayer))
                CursorInterop.SetCursor(_layerCanvas, CropMoveCursor);
            else
                CursorInterop.SetCursor(_layerCanvas, CropArrowCursor);
            return;
        }

        if (_selectedLayer is null) return;
        var imageRect = GetLayerDisplayRect();
        if (imageRect.Width <= 0 || imageRect.Height <= 0) return;

        if (_layerResizing)
        {
            var handle = GetLayerHandlePoint(_layerResizeHandle, _layerResizeStartBounds);
            var centerX = _layerResizeStartBounds.X + _layerResizeStartBounds.Width / 2;
            var centerY = _layerResizeStartBounds.Y + _layerResizeStartBounds.Height / 2;
            var directionX = Math.Sign(handle.X - centerX);
            var directionY = Math.Sign(handle.Y - centerY);
            var scaleX = directionX == 0 ? 1 : 1 + directionX * (position.X - _layerResizeStart.X) / Math.Max(1, _layerResizeStartBounds.Width);
            var scaleY = directionY == 0 ? 1 : 1 + directionY * (position.Y - _layerResizeStart.Y) / Math.Max(1, _layerResizeStartBounds.Height);
            var scale = directionX == 0 ? scaleY
                : directionY == 0 ? scaleX
                : (scaleX + scaleY) / 2;
            scale = Math.Clamp(scale, 0.1, 6);
            if (_selectedLayer.Text is not null)
            {
                _selectedLayer.Text.FontSizeFraction = Math.Clamp(_layerStartSize * scale, 0.03, 1.5);
                _updatingLayerControls = true;
                _layerSizeSlider.Value = _selectedLayer.Text.FontSizeFraction * 100;
                _layerSizeValue.Text = ((int)_layerSizeSlider.Value).ToString();
                _updatingLayerControls = false;
            }
            else if (_selectedLayer.Image is not null)
            {
                _selectedLayer.Image.WidthFraction = Math.Clamp(_layerStartSize * scale, 0.02, 2);
                _updatingLayerControls = true;
                _layerSizeSlider.Value = _selectedLayer.Image.WidthFraction * 100;
                _layerSizeValue.Text = ((int)_layerSizeSlider.Value).ToString();
                _updatingLayerControls = false;
            }
            UpdateLayerVisuals();
            e.Handled = true;
            return;
        }

        var bounds = GetLayerBounds(_selectedLayer, imageRect);
        // Allow the layer to be dragged partially outside the image (so the text
        // can reach/touch the edges and even hang off). Keep at least a sliver
        // inside so the layer stays visible and grabbable.
        var keepX = Math.Min(bounds.Width, Math.Max(24, bounds.Width * 0.15));
        var keepY = Math.Min(bounds.Height, Math.Max(24, bounds.Height * 0.15));
        var xMin = (keepX - bounds.Width / 2) / imageRect.Width;
        var xMax = 1 - xMin;
        var yMin = (keepY - bounds.Height / 2) / imageRect.Height;
        var yMax = 1 - yMin;
        var x = _layerStartX + (position.X - _layerDragStart.X) / imageRect.Width;
        var y = _layerStartY + (position.Y - _layerDragStart.Y) / imageRect.Height;
        SetLayerPosition(_selectedLayer, Math.Clamp(x, xMin, xMax), Math.Clamp(y, yMin, yMax));
        UpdateLayerVisuals();
        e.Handled = true;
    }

    private void OnLayerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndLayerDrag();
        try { _layerCanvas.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    private void EndLayerDrag()
    {
        _layerDragging = false;
        _layerResizing = false;
        _layerResizeHandle = -1;
        CursorInterop.SetCursor(_layerCanvas, CropArrowCursor);
    }

    private static void SetLayerPosition(ImageLayerItem layer, double x, double y)
    {
        if (layer.Text is not null) { layer.Text.XFraction = x; layer.Text.YFraction = y; }
        if (layer.Image is not null) { layer.Image.XFraction = x; layer.Image.YFraction = y; }
    }

    private ImageLayerItem? HitTestLayer(Point point)
    {
        var imageRect = GetLayerDisplayRect();
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            var bounds = GetLayerBounds(layer, imageRect);
            if (point.X >= bounds.X && point.X <= bounds.X + bounds.Width
                && point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.Height)
                return layer;
        }
        return null;
    }

    private bool IsInsideLayer(Point point, ImageLayerItem layer)
    {
        var bounds = GetLayerBounds(layer, GetLayerDisplayRect());
        return point.X >= bounds.X && point.X <= bounds.X + bounds.Width
            && point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.Height;
    }

    private int GetLayerHandleAt(Point point)
    {
        if (_selectedLayer is null) return -1;
        var bounds = GetLayerBounds(_selectedLayer, GetLayerDisplayRect());
        for (int i = 0; i < 8; i++)
        {
            var handle = GetLayerHandlePoint(i, bounds);
            var dx = point.X - handle.X;
            var dy = point.Y - handle.Y;
            if (dx * dx + dy * dy <= LayerHandleHitRadius * LayerHandleHitRadius)
                return i;
        }
        return -1;
    }

    private static Point GetLayerHandlePoint(int index, Rect bounds) => index switch
    {
        0 => new Point(bounds.X, bounds.Y),
        1 => new Point(bounds.X + bounds.Width / 2, bounds.Y),
        2 => new Point(bounds.X + bounds.Width, bounds.Y),
        3 => new Point(bounds.X + bounds.Width, bounds.Y + bounds.Height / 2),
        4 => new Point(bounds.X + bounds.Width, bounds.Y + bounds.Height),
        5 => new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height),
        6 => new Point(bounds.X, bounds.Y + bounds.Height),
        _ => new Point(bounds.X, bounds.Y + bounds.Height / 2),
    };

    /// <summary>Builds the on-canvas text layer visual, including the stroke
    /// colour (eight offset copies) so the preview matches the exported PNG.
    /// The text block uses its natural measured size, centred inside the
    /// slightly padded layer bounds — nothing is clipped.</summary>
    private Canvas BuildTextVisual(TextOverlay text, Rect imageRect, Rect bounds)
    {
        var size = Math.Clamp(text.FontSizeFraction * imageRect.Height, 8, 128);
        var strokePx = Math.Clamp(text.StrokeFraction * size, 0, 10);
        var pad = strokePx + 3;
        // Same constraint as GetLayerBounds, so the text does not re-wrap here.
        var measured = MeasureTextLayer(text, size, Math.Max(1, imageRect.Width));
        var textW = Math.Max(1, Math.Min(measured.Width, Math.Max(1, bounds.Width - pad * 2)));
        var textH = Math.Max(1, measured.Height);
        var offsetX = Math.Max(0, (bounds.Width - textW) / 2);
        var offsetY = Math.Max(0, (bounds.Height - textH) / 2);

        var host = new Canvas { Width = Math.Max(1, bounds.Width), Height = Math.Max(1, bounds.Height), Opacity = text.Opacity };
        var family = new FontFamily(text.FontFamily);
        var weight = LayerFontWeight(text.FontWeight);
        var style = text.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

        TextBlock Make(SolidColorBrush brush) => new()
        {
            Text = text.Text,
            Width = textW,
            Height = textH,
            FontFamily = family,
            FontSize = size,
            FontWeight = weight,
            FontStyle = style,
            Foreground = brush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
        };

        var strokeAlpha = (text.OutlineArgb >> 24) & 0xFF;
        if (strokeAlpha > 0 && strokePx >= 0.5)
        {
            var strokeBrush = BrushFromArgb(text.OutlineArgb);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4.0;
                var copy = Make(strokeBrush);
                Canvas.SetLeft(copy, offsetX + Math.Cos(angle) * strokePx);
                Canvas.SetTop(copy, offsetY + Math.Sin(angle) * strokePx);
                host.Children.Add(copy);
            }
        }

        var main = Make(BrushFromArgb(text.ColorArgb));
        Canvas.SetLeft(main, offsetX);
        Canvas.SetTop(main, offsetY);
        host.Children.Add(main);
        return host;
    }

    private void UpdateLayerVisuals()
    {
        if (_layerCanvas is null) return;
        _layerCanvas.Children.Clear();
        _layerCanvas.IsHitTestVisible = _loaded && _layers.Count > 0 && !_cropMode;
        if (!_loaded || _layers.Count == 0)
        {
            _layerDeleteButton.Visibility = Visibility.Collapsed;
            return;
        }

        var imageRect = GetLayerDisplayRect();
        if (imageRect.Width <= 0 || imageRect.Height <= 0) return;
        foreach (var layer in _layers)
        {
            var bounds = GetLayerBounds(layer, imageRect);
            if (layer.Text is { } textLayer && imageRect.Width > 0)
            {
                // Keep the export box in sync with the measured text so the PNG
                // does not re-wrap into a wide 90% frame. The extra 6% is a
                // safety margin: GDI+ measures slightly wider than DirectWrite.
                var fraction = Math.Clamp(bounds.Width / imageRect.Width + 0.06, 0.05, 1.0);
                if (Math.Abs(textLayer.WidthFraction - fraction) > 0.001)
                    textLayer.WidthFraction = fraction;
            }
            FrameworkElement visual;
            if (layer.Text is not null)
            {
                visual = BuildTextVisual(layer.Text, imageRect, bounds);
            }
            else
            {
                try
                {
                    visual = new Image
                    {
                        Source = new BitmapImage(new Uri(layer.Image!.Path)),
                        Width = bounds.Width,
                        Height = bounds.Height,
                        Stretch = Stretch.Uniform,
                        Opacity = layer.Image.Opacity,
                    };
                }
                catch (Exception ex)
                {
                    Dbg.Log("ImageEdit layer preview THREW: " + ex.Message);
                    continue;
                }
            }

            visual.IsHitTestVisible = false;
            Canvas.SetLeft(visual, bounds.X);
            Canvas.SetTop(visual, bounds.Y);
            _layerCanvas.Children.Add(visual);
        }

        if (_selectedLayer is null)
        {
            _layerDeleteButton.Visibility = Visibility.Collapsed;
            return;
        }

        var selectedBounds = GetLayerBounds(_selectedLayer, imageRect);
        var selection = new Border
        {
            Width = selectedBounds.Width,
            Height = selectedBounds.Height,
            BorderBrush = Brush(MenuTheme.AccentColor),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(selection, selectedBounds.X);
        Canvas.SetTop(selection, selectedBounds.Y);
        _layerCanvas.Children.Add(selection);

        for (int i = 0; i < 8; i++)
        {
            var handlePoint = GetLayerHandlePoint(i, selectedBounds);
            var handle = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brush("#FFFFFFFF"),
                Stroke = Brush(MenuTheme.AccentColor),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(handle, handlePoint.X - 6);
            Canvas.SetTop(handle, handlePoint.Y - 6);
            _layerCanvas.Children.Add(handle);
        }

        _layerDeleteButton.Visibility = Visibility.Visible;
        Canvas.SetLeft(_layerDeleteButton, Math.Clamp(selectedBounds.X + selectedBounds.Width - 15, 0, Math.Max(0, _layerCanvas.ActualWidth - 30)));
        Canvas.SetTop(_layerDeleteButton, Math.Clamp(selectedBounds.Y - 15, 0, Math.Max(0, _layerCanvas.ActualHeight - 30)));
        _layerCanvas.Children.Add(_layerDeleteButton);
    }

    private Rect GetLayerDisplayRect()
    {
        if (_previewHost.ActualWidth <= 0 || _previewHost.ActualHeight <= 0 || _w == 0 || _h == 0)
            return new Rect(0, 0, 0, 0);

        var imageW = (uint)Math.Clamp(_hasCrop ? _cropW : (int)_w, 1, (int)_w);
        var imageH = (uint)Math.Clamp(_hasCrop ? _cropH : (int)_h, 1, (int)_h);
        if (_rotation is 90 or 270) (imageW, imageH) = (imageH, imageW);
        var imageAspect = (double)imageW / imageH;
        var hostW = _previewHost.ActualWidth;
        var hostH = _previewHost.ActualHeight;
        if (imageAspect > hostW / hostH)
        {
            var width = hostW;
            var height = width / imageAspect;
            return new Rect(0, (hostH - height) / 2, width, height);
        }
        else
        {
            var height = hostH;
            var width = height * imageAspect;
            return new Rect((hostW - width) / 2, 0, width, height);
        }
    }

    private static Windows.UI.Text.FontWeight LayerFontWeight(int weight) =>
        weight >= 700 ? Microsoft.UI.Text.FontWeights.Bold
        : weight >= 600 ? Microsoft.UI.Text.FontWeights.SemiBold
        : weight >= 500 ? Microsoft.UI.Text.FontWeights.Medium
        : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Measures the text the same way <see cref="BuildTextVisual"/> draws
    /// it, so the layer frame hugs the glyphs instead of using a fixed 90% box.</summary>
    private static Size MeasureTextLayer(TextOverlay text, double fontSize, double maxWidth)
    {
        var block = new TextBlock
        {
            Text = text.Text ?? string.Empty,
            FontFamily = new FontFamily(text.FontFamily),
            FontSize = fontSize,
            FontWeight = LayerFontWeight(text.FontWeight),
            FontStyle = text.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
        };
        block.Measure(new Size(Math.Max(1, maxWidth), double.PositiveInfinity));
        return block.DesiredSize;
    }

    private Rect GetLayerBounds(ImageLayerItem layer, Rect imageRect)
    {
        double width, height;
        if (layer.Text is { } text)
        {
            var fontSize = Math.Clamp(text.FontSizeFraction * imageRect.Height, 8, 128);
            var strokePad = Math.Clamp(text.StrokeFraction * fontSize, 0, 10) + 3;
            // Always measure against the full image width. Measuring once against
            // the image and again against the (tighter) frame makes the block
            // wrap: the natural width becomes its own constraint.
            var measured = MeasureTextLayer(text, fontSize, Math.Max(1, imageRect.Width));
            width = Math.Min(imageRect.Width, Math.Max(8, measured.Width + strokePad * 2));
            height = Math.Min(imageRect.Height, Math.Max(8, measured.Height + strokePad * 2));
        }
        else
        {
            width = Math.Clamp(layer.Image!.WidthFraction, 0.02, 2) * imageRect.Width;
            height = width / Math.Max(0.05, layer.AspectRatio);
        }
        var centerX = imageRect.X + (layer.Text?.XFraction ?? layer.Image!.XFraction) * imageRect.Width;
        var centerY = imageRect.Y + (layer.Text?.YFraction ?? layer.Image!.YFraction) * imageRect.Height;
        return new Rect(centerX - width / 2, centerY - height / 2, width, height);
    }

    private static SolidColorBrush BrushFromArgb(uint argb) => new(Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private async System.Threading.Tasks.Task OnSaveClicked()
    {
        if (!_loaded || _saving) return;
        _saving = true;
        BeginBusy();
        _saveButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        SetStatus("Saving...", MenuTheme.AccentColor);

        try
        {
            var previewW = _w;
            var previewH = _h;
            var settings = CaptureRenderSettings();

            var dir = System.IO.Path.GetDirectoryName(_sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(_sourcePath);
            var destPath = ImageConverter.GetUniqueOutputPath(dir, baseName + "_edited", ".png");

            var (srcPixels, srcW, srcH, _, _) = await System.Threading.Tasks.Task.Run(
                () => ImageConverter.DecodeScaledPixelsAsync(_sourcePath, 0));
            settings = ScaleCrop(settings, previewW, previewH, srcW, srcH);
            var rendered = await System.Threading.Tasks.Task.Run(() =>
            {
                var (pixels, width, height) = RenderEdited(srcPixels, srcW, srcH, settings);
                var images = settings.Layers.Where(layer => layer.Image is not null).Select(layer => layer.Image!).ToArray();
                var texts = settings.Layers.Where(layer => layer.Text is not null).Select(layer => layer.Text!).ToArray();
                OverlayCompositor.CompositeBgra(pixels, (int)width, (int)height, images, texts);
                return (Pixels: pixels, Width: width, Height: height);
            });
            var (outPixels, outW, outH) = rendered;

            var png = await ImageConverter.EncodePngBytesAsync(outPixels, outW, outH);
            System.IO.File.WriteAllBytes(destPath, png);

            EndBusy();
            LocalizationUi.SetFormattedText(_titleText, "Saved: {0}", System.IO.Path.GetFileName(destPath));
            SetStatus("Saved next to original.", "#FF9BE29B");
        }
        catch (Exception ex)
        {
            Dbg.Log("ImageEdit save THREW: " + ex);
            EndBusy();
            SetStatus("Save failed: {0}", MenuTheme.AccentDeep, ex.Message.Split('\n')[0]);
            _saveButton.IsEnabled = true;
            _cancelButton.IsEnabled = false;
            _saving = false;
        }
    }

    private UIElement BuildLayout()
    {
        var page = new Border { Background = Brush(MenuTheme.HubFill), Padding = new Thickness(14) };
        var root = new Grid { ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        var left = new Grid { RowSpacing = 8 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(_dimInfo);
        var previewGrid = new Grid();
        previewGrid.Children.Add(_previewHost);
        previewGrid.Children.Add(_cropCanvas);
        previewGrid.Children.Add(_layerCanvas);
        previewGrid.Children.Add(_pickCanvas);
        Grid.SetRow(previewGrid, 1);
        left.Children.Add(previewGrid);

        var paletteRow = new StackPanel { Spacing = 6 };
        var paletteHeader = new Grid { ColumnSpacing = 8 };
        paletteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        paletteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_dropperButton, 0);
        Grid.SetColumn(_paletteCodeButton, 1);
        paletteHeader.Children.Add(_dropperButton);
        paletteHeader.Children.Add(_paletteCodeButton);
        paletteRow.Children.Add(paletteHeader);
        paletteRow.Children.Add(_paletteBar);
        Grid.SetRow(paletteRow, 2);
        left.Children.Add(paletteRow);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        var transformRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        transformRow.Children.Add(_rotCcwButton);
        transformRow.Children.Add(_rotCwButton);
        transformRow.Children.Add(_rot180Button);
        var flipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        flipRow.Children.Add(_flipHButton);
        flipRow.Children.Add(_flipVButton);

        var layerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var addTextButton = new Button { Content = "Add Text", MinHeight = 32 };
        addTextButton.Click += (_, _) => AddTextLayer();
        var addPictureButton = new Button { Content = "Add Picture", MinHeight = 32 };
        addPictureButton.Click += async (_, _) => await PickLayerPictureAsync();
        layerButtons.Children.Add(addTextButton);
        layerButtons.Children.Add(addPictureButton);

        var pageNames = new[] { "Transform", "Adjust", "Layers" };
        var pages = new UIElement[]
        {
            MakeControlPage(
                MakeSection("TRANSFORM"),
                transformRow,
                flipRow,
                MakeSection("CROP"),
                _cropModeButton,
                _applyCropButton,
                _resetButton),
            MakeControlPage(
                MakeSection("COLOR"),
                MakeSliderRow(_brightnessSlider, _brightnessValue, "Brightness"),
                MakeSliderRow(_contrastSlider, _contrastValue, "Contrast"),
                MakeSliderRow(_saturationSlider, _saturationValue, "Saturation"),
                MakeSection("HSL / COLOR MIX"),
                new TextBlock { Text = "Color range", FontSize = 12, Foreground = Brush(MenuTheme.LabelColor) },
                _colorRangePicker,
                MakeSliderRow(_hueSlider, _hueValue, "Hue"),
                MakeSliderRow(_hueSaturationSlider, _hueSaturationValue, "Saturation"),
                MakeSliderRow(_hueLightnessSlider, _hueLightnessValue, "Lightness"),
                MakeSection("LIGHT"),
                MakeSliderRow(_exposureSlider, _exposureValue, "Exposure"),
                MakeSliderRow(_highlightsSlider, _highlightsValue, "Highlights"),
                MakeSliderRow(_shadowsSlider, _shadowsValue, "Shadows")),
            new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = MakeControlPage(
                    MakeSection("ADD TEXT OR PICTURE"),
                    _layerTextInput,
                    layerButtons,
                    MakeSection("TYPEFACE"),
                    _layerFontCombo,
                    MakeSliderRow(_layerSizeSlider, _layerSizeValue, "Size"),
                    _layerWeightCombo,
                    MakeColorAndStyleRow(),
                    MakeSliderRow(_layerOpacitySlider, _layerOpacityValue, "Opacity"),
                    MakeSection("STICKERS"),
                    _stickerSearchBox,
                    MakeStickerGallery(),
                    MakeSection("LAYERS"),
                    _layerList,
                    _removeLayerButton),
            },
        };

            var tabBar = new Grid { ColumnSpacing = 10, MinHeight = 43 };
            tabBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            tabBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        var tabButtons = new Button[pageNames.Length];
            var tabLabels = new TextBlock[pageNames.Length];
            var tabIndicators = new Border[pageNames.Length];
        var pageHost = new Grid();
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            var activeUnderline = Brush(MenuTheme.AccentColor);
        var selectedPage = -1;

        void SelectPage(int selected)
        {
            selectedPage = selected;
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
                tabLabels[i].Foreground = i == selected
                    ? Brush(MenuTheme.LabelColor)
                    : Brush(MenuTheme.HintColor);
                tabLabels[i].FontWeight = i == selected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                tabIndicators[i].Background = i == selected ? activeUnderline : transparent;
            }
        }

        for (int i = 0; i < pageNames.Length; i++)
        {
            tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tabIndex = i;
            var tabContent = new Grid();
            tabContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tabContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
            var label = new TextBlock
            {
                Text = pageNames[i],
                FontSize = 13,
                Foreground = Brush(MenuTheme.HintColor),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            tabLabels[i] = label;
            tabContent.Children.Add(label);
            var indicator = new Border { Background = transparent };
            tabIndicators[i] = indicator;
            Grid.SetRow(indicator, 1);
            tabContent.Children.Add(indicator);
            var button = new Button
            {
                Content = tabContent,
                MinHeight = 42,
                Padding = new Thickness(4, 0, 4, 0),
                Background = transparent,
                BorderBrush = transparent,
                BorderThickness = new Thickness(0),
            };
            button.Click += (_, _) => SelectPage(tabIndex);
            button.PointerEntered += (_, _) =>
            {
                if (selectedPage != tabIndex)
                    tabLabels[tabIndex].Foreground = Brush(MenuTheme.LabelColor);
            };
            button.PointerExited += (_, _) =>
            {
                if (selectedPage != tabIndex)
                    tabLabels[tabIndex].Foreground = Brush(MenuTheme.HintColor);
            };
            tabButtons[i] = button;
            Grid.SetColumn(button, i);
            tabBar.Children.Add(button);

            pages[i].Visibility = Visibility.Collapsed;
            pageHost.Children.Add(pages[i]);
        }
        var tabRule = new Border { Background = Brush("#336B7280") };
        Grid.SetRow(tabRule, 1);
        Grid.SetColumnSpan(tabRule, pageNames.Length);
        tabBar.Children.Add(tabRule);
        SelectPage(0);

        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(tabBar);
        Grid.SetRow(pageHost, 1);
        panel.Children.Add(pageHost);
        Grid.SetRow(_status, 2);
        panel.Children.Add(_status);
        Grid.SetColumn(panel, 1);
        root.Children.Add(panel);

        var pageGrid = new Grid { RowSpacing = 8 };
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleRow = new TextBlock { Text = _fileName, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush(MenuTheme.LabelColor) };
        _titleText = titleRow;
        Grid.SetRow(titleRow, 0);
        pageGrid.Children.Add(titleRow);
        Grid.SetRow(root, 1);
        pageGrid.Children.Add(root);
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(_cancelButton);
        btns.Children.Add(_saveButton);
        Grid.SetColumn(btns, 1);
        footer.Children.Add(btns);
        Grid.SetRow(footer, 2);
        pageGrid.Children.Add(footer);

        page.Child = pageGrid;
        return page;
    }

    private ScrollViewer MakeStickerGallery()
    {
        var files = Directory.GetFiles(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Emoji"), "*.png")
            .OrderBy(file => System.IO.Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        _stickerGallery = grid;
        for (int column = 0; column < StickerGalleryColumns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < (files.Length + StickerGalleryColumns - 1) / StickerGalleryColumns; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        for (int i = 0; i < files.Length; i++)
        {
            var path = files[i];
            var fileKey = System.IO.Path.GetFileNameWithoutExtension(path);
            var emoji = StickerGlyphFromFileKey(fileKey);
            var button = new Button
            {
                Width = 36,
                Height = 32,
                MinWidth = 36,
                MinHeight = 32,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Content = new Image
                {
                    Source = new BitmapImage(new Uri(path)),
                    Width = 24,
                    Height = 24,
                    Stretch = Stretch.Uniform,
                },
                Tag = emoji + " " + fileKey,
            };
            button.Click += (_, _) => AddPictureLayer(path, emoji);
            LocalizationUi.SetToolTip(button, "Add sticker {0}", emoji);
            LocalizationUi.SetAutomationName(button, "Add sticker {0}", emoji);
            Grid.SetColumn(button, i % StickerGalleryColumns);
            Grid.SetRow(button, i / StickerGalleryColumns);
            grid.Children.Add(button);
        }
        return new ScrollViewer
        {
            Content = grid,
            Height = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private void FilterStickerGallery()
    {
        if (_stickerGallery is null) return;
        var query = _stickerSearchBox.Text.Trim();
        var visibleIndex = 0;
        foreach (var child in _stickerGallery.Children)
        {
            if (child is not FrameworkElement item) continue;
            var matches = string.IsNullOrEmpty(query)
                || item.Tag?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            item.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            if (!matches) continue;
            Grid.SetColumn(item, visibleIndex % StickerGalleryColumns);
            Grid.SetRow(item, visibleIndex / StickerGalleryColumns);
            visibleIndex++;
        }
        _stickerGallery.RowDefinitions.Clear();
        for (int row = 0; row < (visibleIndex + StickerGalleryColumns - 1) / StickerGalleryColumns; row++)
            _stickerGallery.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
    }

    private static string StickerGlyphFromFileKey(string fileKey)
    {
        var builder = new StringBuilder();
        foreach (var codePoint in fileKey.Split('-'))
        {
            if (int.TryParse(codePoint, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                && Rune.IsValid(value))
                builder.Append(char.ConvertFromUtf32(value));
        }
        return builder.Length == 0 ? fileKey : builder.ToString();
    }

    private static StackPanel MakeControlPage(params UIElement[] controls)
    {
        var page = new StackPanel { Spacing = 6, Margin = new Thickness(8, 6, 8, 6) };
        foreach (var control in controls)
            page.Children.Add(control);
        return page;
    }

    private static TextBlock MakeSection(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 60,
        Foreground = Brush(MenuTheme.HintColor),
        Margin = new Thickness(0, 6, 0, 0),
    };

    private Border MakePreviewHost()
    {
        var host = new Border { Background = Brush("#FF1B1E2A"), MinHeight = 300, MinWidth = 300 };
        return host;
    }

    private Image MakePreviewImage() => new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private Button MakeIconButton(string iconName, string fallbackGlyph, string tooltip)
    {
        var icon = EditIcons.Load(iconName, 20, fallbackGlyph);
        var btn = new Button { Content = icon, MinWidth = 36, MinHeight = 32, Padding = new Thickness(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00)) };
        ToolTipService.SetToolTip(btn, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, tooltip);
        return btn;
    }

    private Button MakeFlipButton(string iconName, string fallbackGlyph, string label, string tooltip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(EditIcons.Load(iconName, 16, fallbackGlyph));
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush(MenuTheme.LabelColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        var btn = new Button
        {
            Content = content,
            MinWidth = 136,
            MinHeight = 32,
            Padding = new Thickness(8, 4, 8, 4),
            Background = transparent,
            BorderBrush = transparent,
            BorderThickness = new Thickness(0),
        };
        btn.PointerEntered += (_, _) => btn.Background = Brush(MenuTheme.HubFill);
        btn.PointerExited += (_, _) => btn.Background = transparent;
        ToolTipService.SetToolTip(btn, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, tooltip);
        return btn;
    }

    private Grid MakeColorRangePicker()
    {
        var picker = new Grid { ColumnSpacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        _colorRangeRings = new Ellipse[ColorRangeNames.Length];
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        for (int i = 0; i < ColorRangeNames.Length; i++)
        {
            picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var content = new Grid { Width = 30, Height = 30 };
            content.Children.Add(new Ellipse
            {
                Width = 17,
                Height = 17,
                Fill = Brush(ColorRangeSwatches[i]),
            });
            var ring = new Ellipse
            {
                Width = 27,
                Height = 27,
                Stroke = Brush(MenuTheme.AccentColor),
                StrokeThickness = 0,
                Fill = transparent,
                IsHitTestVisible = false,
            };
            _colorRangeRings[i] = ring;
            content.Children.Add(ring);

            var button = new Button
            {
                Content = content,
                Width = 32,
                Height = 32,
                MinWidth = 32,
                MinHeight = 32,
                Padding = new Thickness(0),
                Background = transparent,
                BorderBrush = transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            button.Click += (_, _) => SelectHueRange(index);
            button.PointerEntered += (_, _) => button.Background = Brush("#22FFFFFF");
            button.PointerExited += (_, _) => button.Background = transparent;
            ToolTipService.SetToolTip(button, ColorRangeNames[i]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, ColorRangeNames[i]);
            Grid.SetColumn(button, i);
            picker.Children.Add(button);
        }

        UpdateColorRangeSelection();
        return picker;
    }

    private void UpdateColorRangeSelection()
    {
        if (_colorRangeRings is null) return;
        for (int i = 0; i < _colorRangeRings.Length; i++)
            _colorRangeRings[i].StrokeThickness = i == _hueRange ? 2 : 0;
    }

    private void SaveActiveHueAdjustment()
    {
        if (_hueRange < 0 || _hueRange >= _hueAdjustments.Length) return;
        _hueAdjustments[_hueRange] = new HueAdjustment(_hueShift, _hueSaturation, _hueLightness);
    }

    private void SelectHueRange(int range)
    {
        SaveActiveHueAdjustment();
        _hueRange = Math.Clamp(range, 0, _hueAdjustments.Length - 1);
        var adjustment = _hueAdjustments[_hueRange];
        _hueShift = adjustment.HueShift;
        _hueSaturation = adjustment.Saturation;
        _hueLightness = adjustment.Lightness;
        _updatingHslControls = true;
        _hueSlider.Value = _hueShift;
        _hueSaturationSlider.Value = _hueSaturation;
        _hueLightnessSlider.Value = _hueLightness;
        _hueValue.Text = ((int)_hueShift).ToString();
        _hueSaturationValue.Text = ((int)_hueSaturation).ToString();
        _hueLightnessValue.Text = ((int)_hueLightness).ToString();
        _updatingHslControls = false;
        UpdateColorRangeSelection();
        UpdatePreview();
    }

private Slider MakeSlider(double min, double max, double val)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = val, StepFrequency = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 24, MinHeight = 24 };
        return s;
    }

    private TextBlock MakeValueText(string t) => new() { Text = t, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush(MenuTheme.LabelColor), MinWidth = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

    private Grid MakeSliderRow(Slider slider, TextBlock value, string label)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush(MenuTheme.LabelColor), VerticalAlignment = VerticalAlignment.Center });
        slider.Height = 24; slider.MinHeight = 24;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    private StackPanel MakeColorAndStyleRow()
    {
        var box = new StackPanel { Spacing = 6 };

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        colorRow.Children.Add(new TextBlock { Text = "Text color", Width = 82, FontSize = 12, Foreground = Brush(MenuTheme.LabelColor), VerticalAlignment = VerticalAlignment.Center });
        colorRow.Children.Add(_layerColorSwatch.Button);
        colorRow.Children.Add(_layerItalicCheckBox);
        box.Children.Add(colorRow);

        var strokeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        strokeRow.Children.Add(new TextBlock { Text = "Stroke", Width = 82, FontSize = 12, Foreground = Brush(MenuTheme.LabelColor), VerticalAlignment = VerticalAlignment.Center });
        strokeRow.Children.Add(_layerStrokeSwatch.Button);
        strokeRow.Children.Add(_layerOutlineCheckBox);
        box.Children.Add(strokeRow);

        box.Children.Add(MakeSliderRow(_layerStrokeSlider, _layerStrokeValue, "Stroke width"));
        return box;
    }

    private Button MakeAccentButton(Geometry icon, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var path = new Microsoft.UI.Xaml.Shapes.Path { Data = icon, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)), Stretch = Stretch.Uniform, Width = 16, Height = 16 };
        sp.Children.Add(path);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x14, 0x18)), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, MinWidth = 96, Background = Brush(MenuTheme.AccentColor) };
        return b;
    }

    private Button MakeCancelButton()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var path = new Microsoft.UI.Xaml.Shapes.Path { Data = Icons.CloseIcon(), Fill = Brush(MenuTheme.LabelColor), Stretch = Stretch.Uniform, Width = 16, Height = 16 };
        sp.Children.Add(path);
        sp.Children.Add(new TextBlock { Text = "Cancel", FontSize = 12, Foreground = Brush(MenuTheme.LabelColor), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, MinWidth = 96 };
        b.Click += (_, _) => Close();
        return b;
    }

    private TextBlock MakeTitle() => new() { Text = "Edit Image", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush(MenuTheme.LabelColor) };

    private TextBlock MakeHintText() => new() { Text = "", FontSize = 11, Foreground = Brush(MenuTheme.HintColor), TextWrapping = TextWrapping.Wrap };

    private TextBlock MakeStatusText() => new() { Text = "", FontSize = 12, Foreground = Brush(MenuTheme.HintColor), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    private void SetStatus(string msg, string colorHex, params object?[] args)
    {
        if (args.Length == 0) LocalizationUi.SetText(_status, msg);
        else LocalizationUi.SetFormattedText(_status, msg, args);
        _status.Foreground = Brush(colorHex);
        _status.Visibility = msg.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BeginBusy() { _busyCount++; }
    private void EndBusy() { if (_busyCount > 0) _busyCount--; }

    private Border MakeBusyBadge() => new() { Visibility = Visibility.Collapsed };

    private static SolidColorBrush Brush(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        var a = (byte)((value >> 24) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var b = (byte)(value & 0xFF);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }
}
