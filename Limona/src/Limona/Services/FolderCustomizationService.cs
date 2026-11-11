using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Limona.Interop;

namespace Limona.Services;

#pragma warning disable CA1416 // Validate platform compatibility (Windows 10.0.19041+)

public enum FolderStyle
{
    Flat,
    Depth3D,
    Glossy,
    Minimalist
}

public enum GradientDirection
{
    Vertical,
    Horizontal,
    DiagonalDown,
    DiagonalUp
}

public enum CoverFrame
{
    Rounded,
    Circle,
    Square,
    Polaroid,
    Bordered,
    FullPocket
}

public enum BadgeType
{
    Star,
    Heart,
    Checkmark,
    Warning,
    Lock,
    Important,
    New,
    Priority,
    Clock,
    Question
}

public enum BadgePosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum BadgeSize
{
    Small,
    Medium,
    Large
}

public enum TypographyStyle
{
    Modern,
    Embossed,
    CircularCrest,
    BannerRibbon,
    BoxFrame
}

public enum DecorativeSymbol
{
    None,
    Star,
    Crown,
    Diamond,
    Shield,
    Laurel,
    Underline,
    Dot
}

public enum PatternType
{
    CarbonFiber,
    DiagonalStripes,
    PolkaDots,
    Honeycomb,
    Checkerboard,
    Chevron,
    CircuitBoard,
    DiamondMesh,
    Waves,
    Camouflage
}

public enum PatternScale
{
    Fine,
    Medium,
    Coarse
}

public enum PatternArea
{
    FullFolder,
    BodyOnly
}

public sealed record CoverSettings(
    string? ImagePath,
    float Zoom = 1.0f,
    float OffsetX = 0f,
    float OffsetY = 0f,
    CoverFrame Frame = CoverFrame.Rounded,
    string FolderColorHex = "#FFD4842F",
    string FrameBorderColorHex = "#FFFFFFFF",
    float FrameBorderThickness = 3f
);

public sealed record EmojiSettings(
    string EmojiText = "💼",
    FolderStyle Style = FolderStyle.Depth3D,
    bool UseGradient = false,
    string PrimaryColorHex = "#FFD4842F",
    string SecondaryColorHex = "#FFF09A3E",
    GradientDirection GradientDir = GradientDirection.Vertical,
    float Size = 64f,
    float OffsetX = 0f,
    float OffsetY = 0f
);

public sealed record BadgeSettings(
    BadgeType Badge = BadgeType.Star,
    BadgePosition Position = BadgePosition.BottomRight,
    BadgeSize Size = BadgeSize.Medium,
    bool IsTransparentBg = false,
    string BadgeBgColorHex = "#FFE5574E",
    string BadgeFgColorHex = "#FFFFFFFF"
);

public sealed record MonogramSettings(
    string Text = "M",
    string FontFamilyName = "Segoe UI",
    float FontSize = 58f,
    bool IsBold = true,
    bool IsItalic = false,
    TypographyStyle Typography = TypographyStyle.Modern,
    DecorativeSymbol Symbol = DecorativeSymbol.None,
    bool UseGradient = false,
    string PrimaryColorHex = "#FF4E9BFF",
    string SecondaryColorHex = "#FF10192B",
    string TextColorHex = "#FFFFFFFF",
    float OffsetX = 0f,
    float OffsetY = 0f
);

public sealed record PatternSettings(
    PatternType Pattern = PatternType.CarbonFiber,
    PatternScale Scale = PatternScale.Medium,
    float Opacity = 0.55f,
    PatternArea Area = PatternArea.BodyOnly,
    string BgColorHex = "#FF181A22",
    string FgColorHex = "#FFF5A623"
);

public sealed record EmojiPreset(
    string Name,
    string Emoji,
    FolderStyle Style,
    bool UseGradient,
    string Color1,
    string Color2,
    float Size,
    float OffsetY
);

public sealed record MonogramPreset(
    string Name,
    string Text,
    string Font,
    TypographyStyle Style,
    DecorativeSymbol Symbol,
    bool UseGradient,
    string Color1,
    string Color2,
    string TextColor
);

/// <summary>
/// Core rendering and composition engine for advanced folder customization features:
/// 1. Folder Cover (photo crop, zoom, frames, persistent storage)
/// 2. Emoji Folder Designer (categorized emojis, gradients, 3D/glossy/flat/minimalist styles, presets)
/// 3. Icon Badges (star, lock, warning, positions, transparent/colored, preserve original icon)
/// 4. Folder Monogram (initials, system fonts, crests, ribbons, embossed, symbols, presets)
/// 5. Full Background Pattern (carbon fiber, stripes, dots, honeycomb, mesh, opacity, color schemes)
/// </summary>
public static class FolderCustomizationService
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limona");

    private static readonly string CoversDir = Path.Combine(AppDataFolder, "CustomIcons", "Covers");
    private static readonly string BadgeBackupsDir = Path.Combine(AppDataFolder, "BadgeBackups");
    private static readonly string PresetsDir = Path.Combine(AppDataFolder, "Presets");

    static FolderCustomizationService()
    {
        try { Directory.CreateDirectory(CoversDir); } catch { }
        try { Directory.CreateDirectory(BadgeBackupsDir); } catch { }
        try { Directory.CreateDirectory(PresetsDir); } catch { }
    }

    // =========================================================================
    // FEATURE 1: FOLDER COVER
    // =========================================================================

    public static (Bitmap Preview, byte[] IcoBytes, string PersistentPath) GenerateCoverIcon(
        string folderPath, CoverSettings settings)
    {
        var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            SetupGraphics(g);

            var folderColor = FromArgbHex(settings.FolderColorHex);
            DrawBaseFolder(g, folderColor, folderColor, FolderStyle.Depth3D, false, 256);

            // Compute frame area on folder front pocket
            var frameRect = GetCoverFrameRect(settings.Frame);

            if (!string.IsNullOrEmpty(settings.ImagePath) && File.Exists(settings.ImagePath))
            {
                using var userImg = LoadImageSafely(settings.ImagePath);
                if (userImg != null)
                {
                    DrawCroppedImageInFrame(g, userImg, frameRect, settings);
                }
            }
            else
            {
                // Draw placeholder camera / photo frame icon
                DrawEmptyCoverPlaceholder(g, frameRect, settings);
            }
        }
        var icoBytes = FolderService.BuildMultiSizeIcoFromImage(bmp);

        // Persistent save
        var folderName = GetSafeFolderName(folderPath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var persistentPath = Path.Combine(CoversDir, $"cover_{folderName}_{timestamp}.ico");
        try { File.WriteAllBytes(persistentPath, icoBytes); } catch { }

        return (bmp, icoBytes, persistentPath);
    }

    private static RectangleF GetCoverFrameRect(CoverFrame frame)
    {
        return frame switch
        {
            CoverFrame.Circle => new RectangleF(66, 92, 124, 124),
            CoverFrame.Square => new RectangleF(66, 92, 124, 124),
            CoverFrame.Polaroid => new RectangleF(60, 86, 136, 136),
            CoverFrame.FullPocket => new RectangleF(26, 76, 204, 148),
            CoverFrame.Bordered => new RectangleF(58, 88, 140, 128),
            _ => new RectangleF(56, 88, 144, 126), // Rounded
        };
    }

    private static void DrawCroppedImageInFrame(
        Graphics g, Image img, RectangleF frameRect, CoverSettings settings)
    {
        GraphicsPath clipPath = new();
        float radius = 14f;

        switch (settings.Frame)
        {
            case CoverFrame.Circle:
                clipPath.AddEllipse(frameRect);
                break;
            case CoverFrame.Square:
                clipPath.AddRectangle(frameRect);
                break;
            case CoverFrame.Polaroid:
                // Inner photo rect inside polaroid border
                var innerPhoto = new RectangleF(frameRect.X + 8, frameRect.Y + 8, frameRect.Width - 16, frameRect.Height - 32);
                clipPath.AddRectangle(innerPhoto);
                break;
            case CoverFrame.FullPocket:
                clipPath = RoundedRect(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height, 18f);
                break;
            case CoverFrame.Bordered:
                clipPath = RoundedRect(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height, 12f);
                break;
            default: // Rounded
                clipPath = RoundedRect(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height, radius);
                break;
        }

        // Draw shadow under photo frame
        using (var shadowPath = (GraphicsPath)clipPath.Clone())
        {
            var mat = new Matrix();
            mat.Translate(0, 4);
            shadowPath.Transform(mat);
            using var shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
            g.FillPath(shadowBrush, shadowPath);
        }

        // Polaroid white card background
        if (settings.Frame == CoverFrame.Polaroid)
        {
            using var polaroidPath = RoundedRect(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height, 4f);
            using var cardBrush = new SolidBrush(Color.FromArgb(248, 248, 248));
            g.FillPath(cardBrush, polaroidPath);
            using var cardPen = new Pen(Color.FromArgb(180, 200, 200, 200), 1.2f);
            g.DrawPath(cardPen, polaroidPath);
        }

        var oldClip = g.Clip;
        g.SetClip(clipPath, CombineMode.Intersect);

        // Apply Zoom & Pan
        var zoom = Math.Max(0.2f, settings.Zoom);
        var baseW = frameRect.Width;
        var baseH = frameRect.Height;

        // Cover-fit aspect ratio
        float srcAspect = (float)img.Width / Math.Max(1, img.Height);
        float destAspect = baseW / baseH;
        float drawW, drawH;

        if (srcAspect > destAspect)
        {
            drawH = baseH * zoom;
            drawW = drawH * srcAspect;
        }
        else
        {
            drawW = baseW * zoom;
            drawH = drawW / srcAspect;
        }

        float drawX = frameRect.X + (baseW - drawW) / 2f + settings.OffsetX;
        float drawY = frameRect.Y + (baseH - drawH) / 2f + settings.OffsetY;

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, drawX, drawY, drawW, drawH);

        g.Clip = oldClip;

        // Outer border / frame outline
        if (settings.FrameBorderThickness > 0 && settings.Frame != CoverFrame.Polaroid)
        {
            var borderColor = FromArgbHex(settings.FrameBorderColorHex);
            using var borderPen = new Pen(borderColor, settings.FrameBorderThickness);
            borderPen.Alignment = PenAlignment.Center;
            g.DrawPath(borderPen, clipPath);
        }
    }

    private static void DrawEmptyCoverPlaceholder(Graphics g, RectangleF frameRect, CoverSettings settings)
    {
        using var clipPath = settings.Frame == CoverFrame.Circle
            ? new GraphicsPath()
            : RoundedRect(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height, 14f);

        if (settings.Frame == CoverFrame.Circle)
            clipPath.AddEllipse(frameRect);

        using (var bgBrush = new SolidBrush(Color.FromArgb(160, 255, 255, 255)))
        {
            g.FillPath(bgBrush, clipPath);
        }

        using (var borderPen = new Pen(Color.FromArgb(200, 255, 255, 255), 2f))
        {
            borderPen.DashStyle = DashStyle.Dash;
            g.DrawPath(borderPen, clipPath);
        }

        // Camera icon in center
        var cx = frameRect.X + frameRect.Width / 2f;
        var cy = frameRect.Y + frameRect.Height / 2f;
        using var iconPen = new Pen(Color.FromArgb(120, 0, 0, 0), 2.5f);
        g.DrawRectangle(iconPen, cx - 18, cy - 10, 36, 24);
        g.DrawEllipse(iconPen, cx - 7, cy - 1, 14, 14);
        g.DrawRectangle(iconPen, cx - 10, cy - 15, 10, 5);
    }

    // =========================================================================
    // FEATURE 2: EMOJI FOLDER DESIGNER
    // =========================================================================

    public static readonly Dictionary<string, string[]> EmojiCategories = new()
    {
        ["Faces & Smileys"] = ["😀", "😃", "😄", "😁", "😆", "😅", "😂", "🤣", "😊", "😇", "🙂", "😉", "😍", "🥰", "😘", "😋", "😜", "🤪", "😎", "🤩", "🥳", "😏", "🤔", "🤫", "😴", "🤯", "😱", "🥺", "😭", "😈", "🤠", "🤖"],
        ["Hearts & Emotions"] = ["❤️", "💖", "💕", "💗", "💓", "💘", "💝", "💞", "💟", "💙", "💚", "💛", "💜", "🧡", "🖤", "🤍", "🤎", "💔", "🔥", "✨", "⭐", "🌟", "💥", "💯", "👍", "👎", "👏", "🙌", "🎉", "🎊", "🏆", "🎯"],
        ["Business & Office"] = ["💼", "📈", "📊", "💰", "🏢", "📁", "📂", "👔", "🤝", "📅", "💳", "🏦", "🏷️", "📑", "🗄️", "🖋️"],
        ["People & Avatars"] = ["👤", "👥", "👨‍💻", "👩‍🎨", "🧑‍💼", "👶", "🧑‍🤝‍🧑", "🦸", "🧙", "👑", "🕵️", "🧑‍🏫", "👩‍⚕️", "🧑‍🚀", "👸", "👨‍🎨"],
        ["Documents & Media"] = ["📄", "📝", "📑", "📂", "📜", "📋", "✉️", "📕", "📰", "📚", "📦", "🗂️", "📖", "🏷️", "🔖", "🗃️"],
        ["Technology & Dev"] = ["💻", "🖥️", "📱", "⚙️", "🚀", "🤖", "🎮", "📷", "🔋", "💾", "🔒", "📡", "🕹️", "⚡", "🔧", "💿"],
        ["Education & Study"] = ["🎓", "📖", "✏️", "🔬", "📐", "🎒", "🏫", "🎨", "🧪", "💡", "🧠", "📚", "🖊️", "🔍", "🌍", "🧮"],
        ["Finance & Money"] = ["💵", "💸", "🪙", "🏦", "🏧", "💎", "🏷️", "🧾", "📊", "🛒", "💹", "💰", "💳", "📈", "📉", "🪙"],
        ["Travel & Places"] = ["✈️", "🚗", "🏖️", "🗺️", "🧳", "🚢", "🏔️", "🏨", "⛺", "🚆", "🌍", "🚀", "🏝️", "🏕️", "🛳️", "🚲"],
        ["Food & Drinks"] = ["☕", "🍕", "🍔", "🍎", "🍰", "🍷", "🍩", "🥑", "🍣", "🍦", "🍺", "🥪", "🥐", "🍓", "🍜", "🍹"],
        ["Nature & Animals"] = ["🌲", "🌿", "🌸", "☀️", "🌊", "🐾", "🐶", "🐱", "🔥", "🌈", "🍁", "🌻", "🍀", "🌴", "🌵", "🌙"],
        ["Entertainment & Art"] = ["🎬", "🎵", "🎧", "🎨", "🎭", "🎪", "🎯", "🎸", "🍿", "🏆", "🎤", "🎲", "🎹", "🎺", "🎫", "🎳"],
        ["General & Badges"] = ["⭐", "❤️", "⚡", "🔔", "🔑", "🔒", "🏷️", "📌", "💡", "🛡️", "⚙️", "🎯", "🔥", "✨", "✔️", "🚩"]
    };

    public static (Bitmap Preview, byte[] IcoBytes) GenerateEmojiIcon(
        EmojiSettings settings, Image? colorEmojiImage = null)
    {
        var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            SetupGraphics(g);

            var c1 = FromArgbHex(settings.PrimaryColorHex);
            var c2 = settings.UseGradient ? FromArgbHex(settings.SecondaryColorHex) : c1;

            DrawBaseFolder(g, c1, c2, settings.Style, settings.UseGradient, 256, settings.GradientDir);

            // Draw Emoji text centered on front pocket
            var pocketCenter = new PointF(128f + settings.OffsetX, 150f + settings.OffsetY);

            if (colorEmojiImage != null)
            {
                // Full color rendered emoji
                float drawW = Math.Max(32f, settings.Size * 1.55f);
                float drawH = Math.Max(32f, settings.Size * 1.55f);
                float drawX = pocketCenter.X - drawW / 2f;
                float drawY = pocketCenter.Y - drawH / 2f;

                // Soft shadow under emoji for depth
                using (var shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                {
                    g.FillEllipse(shadowBrush, drawX + drawW * 0.15f, drawY + drawH * 0.75f, drawW * 0.7f, drawH * 0.28f);
                }

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(colorEmojiImage, drawX, drawY, drawW, drawH);
            }
            else
            {
                DrawEmojiGlyphs(g, settings.EmojiText, settings.Size, pocketCenter);
            }
        }

        var icoBytes = FolderService.BuildMultiSizeIcoFromImage(bmp);
        return (bmp, icoBytes);
    }

    private static void DrawEmojiGlyphs(Graphics g, string emoji, float size, PointF center)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return;

        using var font = GetEmojiFont(size);

        // Soft drop shadow under emoji
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        {
            g.DrawString(emoji, font, shadowBrush, center.X + 2, center.Y + 3, sf);
        }

        // Emoji glyph
        using (var brush = new SolidBrush(Color.Black))
        {
            g.DrawString(emoji, font, brush, center.X, center.Y, sf);
        }
    }

    private static Font GetEmojiFont(float size)
    {
        try { return new Font("Segoe UI Emoji", size, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { }
        try { return new Font("Segoe UI Symbol", size, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { }
        return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public static void SaveEmojiPreset(string name, EmojiSettings settings)
    {
        try
        {
            var presets = LoadEmojiPresets();
            presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            presets.Add(new EmojiPreset(
                name,
                settings.EmojiText,
                settings.Style,
                settings.UseGradient,
                settings.PrimaryColorHex,
                settings.SecondaryColorHex,
                settings.Size,
                settings.OffsetY
            ));

            var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(PresetsDir, "emoji_presets.json"), json);
        }
        catch (Exception ex)
        {
            Dbg.Log("SaveEmojiPreset THREW: " + ex.Message);
        }
    }

    public static List<EmojiPreset> LoadEmojiPresets()
    {
        var file = Path.Combine(PresetsDir, "emoji_presets.json");
        if (File.Exists(file))
        {
            try
            {
                var json = File.ReadAllText(file);
                var list = JsonSerializer.Deserialize<List<EmojiPreset>>(json);
                if (list != null && list.Count > 0) return list;
            }
            catch { }
        }

        // Built-in presets
        return
        [
            new("Work & Office", "💼", FolderStyle.Depth3D, true, "#FF3E7BFA", "#FF1B3D8A", 64f, 0f),
            new("Invoices & Money", "💰", FolderStyle.Glossy, true, "#FF27AE60", "#FF145A32", 64f, 0f),
            new("Coding & Tech", "💻", FolderStyle.Flat, true, "#FF8E44AD", "#FF2C3E50", 64f, 0f),
            new("Photos & Media", "📷", FolderStyle.Depth3D, true, "#FFE67E22", "#FFD35400", 64f, 0f),
            new("Private & Secure", "🔒", FolderStyle.Depth3D, false, "#FF34495E", "#FF2C3E50", 64f, 0f),
            new("Travel & Trips", "✈️", FolderStyle.Glossy, true, "#FF00C0FF", "#FF0072FF", 64f, 0f),
            new("Education & Study", "🎓", FolderStyle.Depth3D, true, "#FF9B59B6", "#FF6C3483", 64f, 0f)
        ];
    }

    // =========================================================================
    // FEATURE 3: ICON BADGES
    // =========================================================================

    public static (Bitmap Preview, byte[] IcoBytes) GenerateBadgeIcon(
        string folderPath, BadgeSettings settings)
    {
        var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            SetupGraphics(g);

            // Step 1: Preserve the icon currently referenced by desktop.ini.
            var customIco = FolderService.GetCurrentFolderIconPath(folderPath);
            var backupIco = Path.Combine(BadgeBackupsDir, $"{GetFolderHash(folderPath)}.ico");

            bool drewExisting = false;
            if (customIco is not null)
            {
                try
                {
                    // Backup original if not backed up yet
                    if (!File.Exists(backupIco))
                    {
                        File.Copy(customIco, backupIco, overwrite: true);
                    }
                    using var existing = LoadHighestResFromIco(customIco);
                    if (existing != null)
                    {
                        g.DrawImage(existing, 0, 0, 256, 256);
                        drewExisting = true;
                    }
                }
                catch (Exception ex)
                {
                    Dbg.Log("Load existing icon for badge THREW: " + ex.Message);
                }
            }

            if (!drewExisting)
            {
                // Default clean folder base
                var defaultColor = FromArgbHex("#FFD4842F");
                DrawBaseFolder(g, defaultColor, defaultColor, FolderStyle.Depth3D, false, 256);
            }

            // Step 2: Overlay badge
            DrawBadge(g, settings);
        }

        var icoBytes = FolderService.BuildMultiSizeIcoFromImage(bmp);
        return (bmp, icoBytes);
    }

    public static async Task<bool> RestoreOriginalBadgeIconAsync(string folderPath)
    {
        var backupIco = Path.Combine(BadgeBackupsDir, $"{GetFolderHash(folderPath)}.ico");
        if (File.Exists(backupIco))
        {
            try
            {
                var icoBytes = File.ReadAllBytes(backupIco);
                await FolderService.ApplyGeneratedIconAsync(folderPath, icoBytes);
                return true;
            }
            catch (Exception ex)
            {
                Dbg.Log("Restore original icon THREW: " + ex.Message);
            }
        }
        else
        {
            // Reset to default
            FolderService.ResetFolderIcon(folderPath);
            return true;
        }
        return false;
    }

    private static void DrawBadge(Graphics g, BadgeSettings settings)
    {
        float badgeSize = settings.Size switch
        {
            BadgeSize.Small => 44f,
            BadgeSize.Large => 76f,
            _ => 58f // Medium
        };

        // Determine badge center coordinates
        PointF center = settings.Position switch
        {
            BadgePosition.TopLeft => new PointF(44f, 62f),
            BadgePosition.TopRight => new PointF(212f, 62f),
            BadgePosition.BottomLeft => new PointF(46f, 204f),
            _ => new PointF(210f, 204f) // BottomRight
        };

        var badgeRect = new RectangleF(
            center.X - badgeSize / 2f,
            center.Y - badgeSize / 2f,
            badgeSize,
            badgeSize
        );

        if (!settings.IsTransparentBg)
        {
            // Circular badge plate with drop shadow and glossy outline
            var shadowRect = new RectangleF(badgeRect.X, badgeRect.Y + 3, badgeRect.Width, badgeRect.Height);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            {
                g.FillEllipse(shadowBrush, shadowRect);
            }

            var bgColor = FromArgbHex(settings.BadgeBgColorHex);
            using (var bgBrush = new LinearGradientBrush(
                badgeRect,
                Lighten(bgColor, 0.2f),
                Darken(bgColor, 0.15f),
                LinearGradientMode.Vertical))
            {
                g.FillEllipse(bgBrush, badgeRect);
            }

            using (var borderPen = new Pen(Color.FromArgb(240, 255, 255, 255), Math.Max(2f, badgeSize * 0.05f)))
            {
                g.DrawEllipse(borderPen, badgeRect);
            }
        }

        // Draw Badge Symbol
        var fgColor = FromArgbHex(settings.BadgeFgColorHex);
        DrawBadgeSymbol(g, settings.Badge, center, badgeSize * 0.6f, fgColor);
    }

    private static void DrawBadgeSymbol(
        Graphics g, BadgeType badge, PointF center, float size, Color color)
    {
        using var pen = new Pen(color, Math.Max(2.5f, size * 0.14f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);

        float half = size / 2f;

        switch (badge)
        {
            case BadgeType.Star:
                DrawStarPolygon(g, brush, center.X, center.Y, half, half * 0.45f);
                break;

            case BadgeType.Heart:
                DrawHeartShape(g, brush, center.X, center.Y, half);
                break;

            case BadgeType.Checkmark:
                g.DrawLines(pen, new[]
                {
                    new PointF(center.X - half * 0.6f, center.Y),
                    new PointF(center.X - half * 0.1f, center.Y + half * 0.5f),
                    new PointF(center.X + half * 0.7f, center.Y - half * 0.5f)
                });
                break;

            case BadgeType.Warning:
                // Triangle + exclamation mark
                var triPath = new GraphicsPath();
                triPath.AddPolygon(new[]
                {
                    new PointF(center.X, center.Y - half * 0.8f),
                    new PointF(center.X + half * 0.85f, center.Y + half * 0.7f),
                    new PointF(center.X - half * 0.85f, center.Y + half * 0.7f)
                });
                g.DrawPath(pen, triPath);
                g.DrawLine(pen, center.X, center.Y - half * 0.35f, center.X, center.Y + half * 0.15f);
                g.FillEllipse(brush, center.X - pen.Width / 2f, center.Y + half * 0.38f, pen.Width, pen.Width);
                break;

            case BadgeType.Lock:
                // Shackle + body
                var bodyW = size * 0.65f;
                var bodyH = size * 0.5f;
                var bodyY = center.Y - bodyH / 4f;
                var bodyRect = RoundedRect(center.X - bodyW / 2f, bodyY, bodyW, bodyH, 4f);
                g.FillPath(brush, bodyRect);

                var shackleW = bodyW * 0.6f;
                var shackleH = size * 0.4f;
                g.DrawArc(pen, center.X - shackleW / 2f, bodyY - shackleH + 2, shackleW, shackleH * 1.5f, 180, 180);
                break;

            case BadgeType.Important:
                // Bold exclamation mark
                g.DrawLine(pen, center.X, center.Y - half * 0.7f, center.X, center.Y + half * 0.2f);
                g.FillEllipse(brush, center.X - pen.Width * 0.7f, center.Y + half * 0.45f, pen.Width * 1.4f, pen.Width * 1.4f);
                break;

            case BadgeType.New:
                using (var font = new Font("Arial", size * 0.38f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("NEW", font, brush, center.X, center.Y, sf);
                }
                break;

            case BadgeType.Priority:
                // Upward double chevron or red triangle
                var chevronPath = new GraphicsPath();
                chevronPath.AddLines(new[]
                {
                    new PointF(center.X - half * 0.6f, center.Y + half * 0.5f),
                    new PointF(center.X, center.Y - half * 0.3f),
                    new PointF(center.X + half * 0.6f, center.Y + half * 0.5f)
                });
                g.DrawPath(pen, chevronPath);

                var topPath = new GraphicsPath();
                topPath.AddLines(new[]
                {
                    new PointF(center.X - half * 0.6f, center.Y),
                    new PointF(center.X, center.Y - half * 0.8f),
                    new PointF(center.X + half * 0.6f, center.Y)
                });
                g.DrawPath(pen, topPath);
                break;

            case BadgeType.Clock:
                g.DrawEllipse(pen, center.X - half * 0.8f, center.Y - half * 0.8f, half * 1.6f, half * 1.6f);
                g.DrawLine(pen, center.X, center.Y, center.X, center.Y - half * 0.45f);
                g.DrawLine(pen, center.X, center.Y, center.X + half * 0.35f, center.Y);
                break;

            case BadgeType.Question:
                using (var font = new Font("Arial", size * 0.65f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("?", font, brush, center.X, center.Y, sf);
                }
                break;
        }
    }

    private static void DrawStarPolygon(Graphics g, Brush brush, float cx, float cy, float rOuter, float rInner)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            float r = (i % 2 == 0) ? rOuter : rInner;
            double angle = -Math.PI / 2.0 + i * Math.PI / 5.0;
            pts[i] = new PointF((float)(cx + r * Math.Cos(angle)), (float)(cy + r * Math.Sin(angle)));
        }
        g.FillPolygon(brush, pts);
    }

    private static void DrawHeartShape(Graphics g, Brush brush, float cx, float cy, float r)
    {
        var path = new GraphicsPath();
        float topY = cy - r * 0.5f;
        float bottomY = cy + r * 0.75f;
        float leftX = cx - r * 0.85f;
        float rightX = cx + r * 0.85f;

        path.AddBezier(cx, topY, cx - r * 0.5f, topY - r * 0.6f, leftX, topY + r * 0.2f, cx, bottomY);
        path.AddBezier(cx, bottomY, rightX, topY + r * 0.2f, cx + r * 0.5f, topY - r * 0.6f, cx, topY);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // =========================================================================
    // FEATURE 4: FOLDER MONOGRAM
    // =========================================================================

    public static string SuggestInitials(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "M";
        folderName = folderName.Trim();

        // Split by spaces, underscores, dashes
        var words = folderName.Split(new[] { ' ', '_', '-', '.', '&' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
        {
            var w = words[0];
            if (w.Length <= 3) return w.ToUpperInvariant();
            // Take first and last capital or first two chars
            var caps = w.Where(char.IsUpper).Take(2).ToArray();
            if (caps.Length >= 2) return new string(caps);
            return w.Substring(0, Math.Min(2, w.Length)).ToUpperInvariant();
        }

        var initials = new StringBuilder();
        foreach (var word in words.Take(3))
        {
            if (word.Length > 0 && (char.IsLetterOrDigit(word[0])))
                initials.Append(char.ToUpperInvariant(word[0]));
        }

        return initials.Length > 0 ? initials.ToString() : "M";
    }

    public static (Bitmap Preview, byte[] IcoBytes) GenerateMonogramIcon(MonogramSettings settings)
    {
        var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            SetupGraphics(g);

            var c1 = FromArgbHex(settings.PrimaryColorHex);
            var c2 = settings.UseGradient ? FromArgbHex(settings.SecondaryColorHex) : c1;

            DrawBaseFolder(g, c1, c2, FolderStyle.Depth3D, settings.UseGradient, 256);

            // Pocket center for monogram
            var center = new PointF(128f + settings.OffsetX, 150f + settings.OffsetY);

            DrawMonogramComposition(g, settings, center);
        }

        var icoBytes = FolderService.BuildMultiSizeIcoFromImage(bmp);
        return (bmp, icoBytes);
    }

    private static void DrawMonogramComposition(
        Graphics g, MonogramSettings settings, PointF center)
    {
        var text = string.IsNullOrWhiteSpace(settings.Text) ? "M" : settings.Text;
        var fontStyle = FontStyle.Regular;
        if (settings.IsBold) fontStyle |= FontStyle.Bold;
        if (settings.IsItalic) fontStyle |= FontStyle.Italic;

        using var font = CreateFontSafely(settings.FontFamilyName, settings.FontSize, fontStyle);
        var textColor = FromArgbHex(settings.TextColorHex);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        // Typography styles
        switch (settings.Typography)
        {
            case TypographyStyle.CircularCrest:
                DrawCircularCrest(g, text, font, textColor, center, settings.Symbol);
                break;

            case TypographyStyle.BannerRibbon:
                DrawBannerRibbon(g, text, font, textColor, center, settings.Symbol);
                break;

            case TypographyStyle.BoxFrame:
                DrawBoxFrame(g, text, font, textColor, center, settings.Symbol);
                break;

            case TypographyStyle.Embossed:
                DrawEmbossedText(g, text, font, textColor, center, sf);
                break;

            default: // Modern
                DrawModernText(g, text, font, textColor, center, sf, settings.Symbol);
                break;
        }
    }

    private static void DrawModernText(
        Graphics g, string text, Font font, Color textColor, PointF center, StringFormat sf, DecorativeSymbol symbol)
    {
        // Shadow
        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
        {
            g.DrawString(text, font, shadow, center.X + 2, center.Y + 3, sf);
        }
        using (var brush = new SolidBrush(textColor))
        {
            g.DrawString(text, font, brush, center.X, center.Y, sf);
        }

        DrawDecorativeSymbol(g, symbol, center, font.SizeInPoints * 0.45f, textColor);
    }

    private static void DrawEmbossedText(
        Graphics g, string text, Font font, Color textColor, PointF center, StringFormat sf)
    {
        // Highlight (top-left)
        using (var hi = new SolidBrush(Color.FromArgb(160, 255, 255, 255)))
        {
            g.DrawString(text, font, hi, center.X - 1.5f, center.Y - 1.5f, sf);
        }
        // Shadow (bottom-right)
        using (var sh = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
        {
            g.DrawString(text, font, sh, center.X + 2f, center.Y + 2f, sf);
        }
        // Body
        using (var brush = new SolidBrush(textColor))
        {
            g.DrawString(text, font, brush, center.X, center.Y, sf);
        }
    }

    private static void DrawCircularCrest(
        Graphics g, string text, Font font, Color textColor, PointF center, DecorativeSymbol symbol)
    {
        float crestR = Math.Max(50f, font.SizeInPoints * 0.95f);
        var crestRect = new RectangleF(center.X - crestR, center.Y - crestR, crestR * 2, crestR * 2);

        // Shadow
        using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        {
            g.FillEllipse(shadowBrush, crestRect.X + 2, crestRect.Y + 3, crestRect.Width, crestRect.Height);
        }

        // Inner seal plate
        using (var plateBrush = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
        {
            g.FillEllipse(plateBrush, crestRect);
        }

        // Concentric decorative rings
        using (var outerPen = new Pen(textColor, 3f))
        {
            g.DrawEllipse(outerPen, crestRect);
        }
        using (var innerPen = new Pen(Color.FromArgb(180, textColor), 1.2f) { DashStyle = DashStyle.Dash })
        {
            var inset = crestRect;
            inset.Inflate(-6, -6);
            g.DrawEllipse(innerPen, inset);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var brush = new SolidBrush(textColor))
        {
            g.DrawString(text, font, brush, center.X, center.Y, sf);
        }

        DrawDecorativeSymbol(g, symbol, new PointF(center.X, center.Y - crestR - 10), 16f, textColor);
    }

    private static void DrawBannerRibbon(
        Graphics g, string text, Font font, Color textColor, PointF center, DecorativeSymbol symbol)
    {
        float bannerH = Math.Max(38f, font.SizeInPoints * 0.9f);
        var bannerRect = new RectangleF(26, center.Y - bannerH / 2f, 204, bannerH);

        // Banner ribbon plate
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            g.FillRectangle(shadow, bannerRect.X, bannerRect.Y + 2, bannerRect.Width, bannerRect.Height);
        }
        using (var ribbonBrush = new SolidBrush(Color.FromArgb(230, 20, 20, 25)))
        {
            g.FillRectangle(ribbonBrush, bannerRect);
        }
        using (var linePen = new Pen(textColor, 2f))
        {
            g.DrawLine(linePen, bannerRect.Left, bannerRect.Top, bannerRect.Right, bannerRect.Top);
            g.DrawLine(linePen, bannerRect.Left, bannerRect.Bottom, bannerRect.Right, bannerRect.Bottom);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var brush = new SolidBrush(textColor))
        {
            g.DrawString(text, font, brush, center.X, center.Y, sf);
        }

        DrawDecorativeSymbol(g, symbol, new PointF(center.X, bannerRect.Top - 12), 16f, textColor);
    }

    private static void DrawBoxFrame(
        Graphics g, string text, Font font, Color textColor, PointF center, DecorativeSymbol symbol)
    {
        float boxW = Math.Max(70f, font.SizeInPoints * 1.5f);
        float boxH = Math.Max(50f, font.SizeInPoints * 1.1f);
        var boxRect = new RectangleF(center.X - boxW / 2f, center.Y - boxH / 2f, boxW, boxH);

        // Backdrop
        using (var plateBrush = new SolidBrush(Color.FromArgb(35, 255, 255, 255)))
        {
            g.FillRectangle(plateBrush, boxRect);
        }

        using (var pen = new Pen(textColor, 2.5f))
        {
            g.DrawRectangle(pen, boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height);
        }

        // Elegant corner marks
        float markLen = 8f;
        using (var markPen = new Pen(textColor, 4f))
        {
            g.DrawLine(markPen, boxRect.Left - 2, boxRect.Top, boxRect.Left + markLen, boxRect.Top);
            g.DrawLine(markPen, boxRect.Right + 2, boxRect.Top, boxRect.Right - markLen, boxRect.Top);
            g.DrawLine(markPen, boxRect.Left - 2, boxRect.Bottom, boxRect.Left + markLen, boxRect.Bottom);
            g.DrawLine(markPen, boxRect.Right + 2, boxRect.Bottom, boxRect.Right - markLen, boxRect.Bottom);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var brush = new SolidBrush(textColor))
        {
            g.DrawString(text, font, brush, center.X, center.Y, sf);
        }

        DrawDecorativeSymbol(g, symbol, new PointF(center.X, boxRect.Top - 12), 16f, textColor);
    }

    private static void DrawDecorativeSymbol(Graphics g, DecorativeSymbol symbol, PointF pos, float size, Color color)
    {
        if (symbol == DecorativeSymbol.None) return;

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 2f);

        switch (symbol)
        {
            case DecorativeSymbol.Crown:
                using (var crownFont = GetEmojiFont(size * 1.2f))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("👑", crownFont, brush, pos.X, pos.Y, sf);
                }
                break;
            case DecorativeSymbol.Star:
                DrawStarPolygon(g, brush, pos.X, pos.Y, size * 0.7f, size * 0.35f);
                break;
            case DecorativeSymbol.Diamond:
                var dPath = new GraphicsPath();
                dPath.AddPolygon(new[]
                {
                    new PointF(pos.X, pos.Y - size * 0.7f),
                    new PointF(pos.X + size * 0.7f, pos.Y),
                    new PointF(pos.X, pos.Y + size * 0.7f),
                    new PointF(pos.X - size * 0.7f, pos.Y)
                });
                g.FillPath(brush, dPath);
                break;
            case DecorativeSymbol.Underline:
                g.DrawLine(new Pen(color, 3f), pos.X - size * 1.6f, pos.Y + size, pos.X + size * 1.6f, pos.Y + size);
                break;
            case DecorativeSymbol.Dot:
                g.FillEllipse(brush, pos.X - 4f, pos.Y - 4f, 8f, 8f);
                break;
            default:
                break;
        }
    }

    public static List<string> GetAvailableSystemFonts()
    {
        var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Popular curated typography choices first
        string[] priority = ["Segoe UI", "Arial", "Georgia", "Consolas", "Trebuchet MS", "Impact", "Tahoma", "Verdana", "Times New Roman", "Palatino Linotype", "Century Gothic", "Franklin Gothic Medium"];
        foreach (var p in priority) fonts.Add(p);

        try
        {
            using var installed = new InstalledFontCollection();
            foreach (var fam in installed.Families)
            {
                fonts.Add(fam.Name);
            }
        }
        catch { }

        return fonts.OrderBy(f => Array.IndexOf(priority, f) >= 0 ? Array.IndexOf(priority, f) : 999).ThenBy(f => f).ToList();
    }

    public static void SaveMonogramPreset(string name, MonogramSettings settings)
    {
        try
        {
            var presets = LoadMonogramPresets();
            presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            presets.Add(new MonogramPreset(
                name,
                settings.Text,
                settings.FontFamilyName,
                settings.Typography,
                settings.Symbol,
                settings.UseGradient,
                settings.PrimaryColorHex,
                settings.SecondaryColorHex,
                settings.TextColorHex
            ));

            var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(PresetsDir, "monogram_presets.json"), json);
        }
        catch (Exception ex)
        {
            Dbg.Log("SaveMonogramPreset THREW: " + ex.Message);
        }
    }

    public static List<MonogramPreset> LoadMonogramPresets()
    {
        var file = Path.Combine(PresetsDir, "monogram_presets.json");
        if (File.Exists(file))
        {
            try
            {
                var json = File.ReadAllText(file);
                var list = JsonSerializer.Deserialize<List<MonogramPreset>>(json);
                if (list != null && list.Count > 0) return list;
            }
            catch { }
        }

        // Built-in presets
        return
        [
            new("Executive Crest", "M", "Georgia", TypographyStyle.CircularCrest, DecorativeSymbol.Crown, true, "#FF181A22", "#FF0A0B10", "#FFF5A623"),
            new("Modern Tech", "DEV", "Consolas", TypographyStyle.Modern, DecorativeSymbol.None, true, "#FF1A365D", "#FF2A4365", "#FF63B3ED"),
            new("Stamp Invoices", "INV", "Impact", TypographyStyle.BannerRibbon, DecorativeSymbol.Diamond, false, "#FF9B2C2C", "#FF742A2A", "#FFFFFFFF"),
            new("Classic Luxury", "CL", "Palatino Linotype", TypographyStyle.BoxFrame, DecorativeSymbol.Star, true, "#FF2D3748", "#FF1A202C", "#FFE2E8F0"),
            new("Embossed Clean", "2026", "Segoe UI", TypographyStyle.Embossed, DecorativeSymbol.None, false, "#FF319795", "#FF234E52", "#FFFFFFFF")
        ];
    }

    // =========================================================================
    // FEATURE 5: FULL BACKGROUND PATTERN
    // =========================================================================

    public static (Bitmap Preview, byte[] IcoBytes) GeneratePatternIcon(PatternSettings settings)
    {
        var bmp = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            SetupGraphics(g);

            var bgColor = FromArgbHex(settings.BgColorHex);
            var fgColor = FromArgbHex(settings.FgColorHex);

            // Draw base solid/gradient folder
            DrawBaseFolder(g, bgColor, bgColor, FolderStyle.Depth3D, false, 256);

            // Generate procedural texture brush
            using var patternBmp = CreatePatternTile(settings.Pattern, settings.Scale, fgColor, settings.Opacity);
            using var patternBrush = new TextureBrush(patternBmp, WrapMode.Tile);

            // Clip to full folder or front pocket only
            GraphicsPath patternArea;
            if (settings.Area == PatternArea.FullFolder)
            {
                patternArea = CreateFullFolderSilhouettePath(256);
            }
            else
            {
                patternArea = RoundedRect(24, 76, 208, 148, 18f); // Front pocket
            }

            g.FillPath(patternBrush, patternArea);

            // Re-apply front edge highlight & border
            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.8f))
            {
                g.DrawPath(pen, patternArea);
            }
        }

        var icoBytes = FolderService.BuildMultiSizeIcoFromImage(bmp);
        return (bmp, icoBytes);
    }

    private static Bitmap CreatePatternTile(
        PatternType type, PatternScale scale, Color fg, float opacity)
    {
        int tileSize = scale switch
        {
            PatternScale.Fine => 12,
            PatternScale.Coarse => 28,
            _ => 18 // Medium
        };

        var tile = new Bitmap(tileSize, tileSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(tile))
        {
            SetupGraphics(g);
            g.Clear(Color.Transparent);

            int alpha = Math.Clamp((int)(opacity * 255), 10, 255);
            var patColor = Color.FromArgb(alpha, fg.R, fg.G, fg.B);
            var penColor = Color.FromArgb(alpha, fg.R, fg.G, fg.B);
            using var brush = new SolidBrush(patColor);
            using var pen = new Pen(penColor, Math.Max(1.4f, tileSize * 0.12f));

            switch (type)
            {
                case PatternType.CarbonFiber:
                    // 2x2 weave blocks
                    int half = tileSize / 2;
                    using (var b1 = new SolidBrush(Color.FromArgb((int)(alpha * 0.85f), fg)))
                    using (var b2 = new SolidBrush(Color.FromArgb((int)(alpha * 0.45f), fg)))
                    {
                        g.FillRectangle(b1, 0, 0, half, half);
                        g.FillRectangle(b2, half, 0, half, half);
                        g.FillRectangle(b2, 0, half, half, half);
                        g.FillRectangle(b1, half, half, half, half);
                    }
                    g.DrawLine(pen, 0, 0, tileSize, tileSize);
                    break;

                case PatternType.DiagonalStripes:
                    g.DrawLine(pen, 0, tileSize, tileSize, 0);
                    g.DrawLine(pen, -tileSize / 2f, tileSize / 2f, tileSize / 2f, -tileSize / 2f);
                    g.DrawLine(pen, tileSize / 2f, tileSize * 1.5f, tileSize * 1.5f, tileSize / 2f);
                    break;

                case PatternType.PolkaDots:
                    float dotR = Math.Max(2f, tileSize * 0.22f);
                    g.FillEllipse(brush, 0, 0, dotR, dotR);
                    g.FillEllipse(brush, tileSize / 2f, tileSize / 2f, dotR, dotR);
                    break;

                case PatternType.Honeycomb:
                    // Hexagon outline
                    float r = tileSize * 0.48f;
                    float cx = tileSize / 2f;
                    float cy = tileSize / 2f;
                    var hexPts = new PointF[6];
                    for (int i = 0; i < 6; i++)
                    {
                        double ang = i * Math.PI / 3.0;
                        hexPts[i] = new PointF((float)(cx + r * Math.Cos(ang)), (float)(cy + r * Math.Sin(ang)));
                    }
                    g.DrawPolygon(pen, hexPts);
                    break;

                case PatternType.Checkerboard:
                    int q = tileSize / 2;
                    g.FillRectangle(brush, 0, 0, q, q);
                    g.FillRectangle(brush, q, q, q, q);
                    break;

                case PatternType.Chevron:
                    g.DrawLines(pen, new[]
                    {
                        new PointF(0, tileSize * 0.25f),
                        new PointF(tileSize / 2f, tileSize * 0.75f),
                        new PointF(tileSize, tileSize * 0.25f)
                    });
                    break;

                case PatternType.CircuitBoard:
                    g.DrawLine(pen, 0, tileSize / 2f, tileSize * 0.7f, tileSize / 2f);
                    g.DrawLine(pen, tileSize * 0.7f, tileSize / 2f, tileSize * 0.7f, tileSize);
                    g.FillEllipse(brush, tileSize * 0.6f, tileSize * 0.4f, tileSize * 0.25f, tileSize * 0.25f);
                    break;

                case PatternType.DiamondMesh:
                    g.DrawLine(pen, 0, 0, tileSize, tileSize);
                    g.DrawLine(pen, 0, tileSize, tileSize, 0);
                    break;

                case PatternType.Waves:
                    g.DrawBezier(pen,
                        new PointF(0, tileSize * 0.5f),
                        new PointF(tileSize * 0.33f, tileSize * 0.15f),
                        new PointF(tileSize * 0.66f, tileSize * 0.85f),
                        new PointF(tileSize, tileSize * 0.5f));
                    break;

                case PatternType.Camouflage:
                    g.FillEllipse(brush, 2, 2, tileSize * 0.6f, tileSize * 0.4f);
                    g.FillEllipse(brush, tileSize * 0.4f, tileSize * 0.5f, tileSize * 0.5f, tileSize * 0.4f);
                    break;
            }
        }

        return tile;
    }

    // =========================================================================
    // BASE FOLDER SILHOUETTE & RENDERING HELPERS
    // =========================================================================

    public static void DrawBaseFolder(
        Graphics g, Color c1, Color c2, FolderStyle style, bool useGradient, int size,
        GradientDirection gradDir = GradientDirection.Vertical)
    {
        float f = size / 256f;

        // 1. Tab path (back plate)
        using var tabPath = RoundedRect(24 * f, 46 * f, 90 * f, 36 * f, 12 * f);
        var tabColor = Darken(c1, 0.22f);
        using (var tabBrush = new SolidBrush(tabColor))
        {
            g.FillPath(tabBrush, tabPath);
        }

        // 2. Back plate
        using var backPlate = RoundedRect(24 * f, 56 * f, 208 * f, 130 * f, 16 * f);
        var backColor = Darken(c1, 0.12f);
        using (var backBrush = new SolidBrush(backColor))
        {
            g.FillPath(backBrush, backPlate);
        }

        // 3. Subtle white paper insert inside folder for depth
        if (style != FolderStyle.Flat && style != FolderStyle.Minimalist)
        {
            using var paper = RoundedRect(34 * f, 64 * f, 188 * f, 60 * f, 6 * f);
            using var paperBrush = new SolidBrush(Color.FromArgb(160, 245, 247, 250));
            g.FillPath(paperBrush, paper);
        }

        // 4. Front pocket / body
        var pocketRect = new RectangleF(24 * f, 76 * f, 208 * f, 148 * f);
        using var pocketPath = RoundedRect(pocketRect.X, pocketRect.Y, pocketRect.Width, pocketRect.Height, 18 * f);

        // Drop shadow for 3D depth
        if (style == FolderStyle.Depth3D)
        {
            using var shadowPath = (GraphicsPath)pocketPath.Clone();
            var mat = new Matrix();
            mat.Translate(0, 5 * f);
            shadowPath.Transform(mat);
            using var shadowBrush = new SolidBrush(Color.FromArgb(45, 0, 0, 0));
            g.FillPath(shadowBrush, shadowPath);
        }

        // Front pocket fill
        if (useGradient)
        {
            var mode = gradDir switch
            {
                GradientDirection.Horizontal => LinearGradientMode.Horizontal,
                GradientDirection.DiagonalDown => LinearGradientMode.ForwardDiagonal,
                GradientDirection.DiagonalUp => LinearGradientMode.BackwardDiagonal,
                _ => LinearGradientMode.Vertical
            };
            using var gradBrush = new LinearGradientBrush(pocketRect, c1, c2, mode);
            g.FillPath(gradBrush, pocketPath);
        }
        else
        {
            var bodyColor = style == FolderStyle.Depth3D ? c1 : c1;
            using var bodyBrush = new SolidBrush(bodyColor);
            g.FillPath(bodyBrush, pocketPath);
        }

        // 5. Lighting highlights & effects
        if (style == FolderStyle.Glossy)
        {
            // Curved glassy reflection arc
            using var glossPath = new GraphicsPath();
            glossPath.AddArc(pocketRect.X, pocketRect.Y, pocketRect.Width, pocketRect.Height * 0.9f, 180, 180);
            glossPath.CloseFigure();
            using var glossBrush = new LinearGradientBrush(
                new RectangleF(pocketRect.X, pocketRect.Y, pocketRect.Width, pocketRect.Height * 0.5f),
                Color.FromArgb(130, 255, 255, 255),
                Color.FromArgb(10, 255, 255, 255),
                LinearGradientMode.Vertical);
            g.FillPath(glossBrush, pocketPath);
        }
        else if (style == FolderStyle.Depth3D)
        {
            // Soft top lip highlight
            using var lipPath = RoundedRect(pocketRect.X, pocketRect.Y, pocketRect.Width, 22 * f, 14 * f);
            using var lipBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
            g.FillPath(lipBrush, lipPath);
        }

        // 6. Perimeter outline
        float penWidth = style == FolderStyle.Minimalist ? 3.5f * f : 1.8f * f;
        var outlineColor = style == FolderStyle.Minimalist
            ? c1
            : Darken(c1, 0.4f);
        using var pen = new Pen(outlineColor, penWidth);
        g.DrawPath(pen, pocketPath);
    }

    private static GraphicsPath CreateFullFolderSilhouettePath(int size)
    {
        float f = size / 256f;
        var path = new GraphicsPath();

        // Combined silhouette: tab + pocket
        using var tab = RoundedRect(24 * f, 46 * f, 90 * f, 36 * f, 12 * f);
        using var body = RoundedRect(24 * f, 76 * f, 208 * f, 148 * f, 18 * f);

        path.AddPath(tab, false);
        path.AddPath(body, false);
        return path;
    }

    // =========================================================================
    // UTILITIES
    // =========================================================================

    public static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void SetupGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static Color FromArgbHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Color.FromArgb(255, 212, 132, 47);
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6) clean = "FF" + clean;
        if (clean.Length != 8) return Color.FromArgb(255, 212, 132, 47);

        try
        {
            var value = Convert.ToUInt32(clean, 16);
            return Color.FromArgb(
                (int)((value >> 24) & 0xFF),
                (int)((value >> 16) & 0xFF),
                (int)((value >> 8) & 0xFF),
                (int)(value & 0xFF));
        }
        catch { return Color.FromArgb(255, 212, 132, 47); }
    }

    public static Color Lighten(Color c, float factor) =>
        Color.FromArgb(c.A,
            Math.Clamp((int)(c.R + (255 - c.R) * factor), 0, 255),
            Math.Clamp((int)(c.G + (255 - c.G) * factor), 0, 255),
            Math.Clamp((int)(c.B + (255 - c.B) * factor), 0, 255));

    public static Color Darken(Color c, float factor) =>
        Color.FromArgb(c.A,
            Math.Clamp((int)(c.R * (1f - factor)), 0, 255),
            Math.Clamp((int)(c.G * (1f - factor)), 0, 255),
            Math.Clamp((int)(c.B * (1f - factor)), 0, 255));

    private static Image? LoadImageSafely(string path)
    {
        try
        {
            // Read to memory stream so file is not locked on disk
            var bytes = File.ReadAllBytes(path);
            var ms = new MemoryStream(bytes);
            return Image.FromStream(ms);
        }
        catch (Exception ex)
        {
            Dbg.Log("LoadImageSafely failed for: " + path + " error: " + ex.Message);
            return null;
        }
    }

    private static Image? LoadHighestResFromIco(string icoPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(icoPath);
            if (bytes.Length < 6) return null;

            ushort count = BitConverter.ToUInt16(bytes, 4);
            int maxW = -1;
            int bestOffset = -1;
            int bestSize = -1;

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                int w = bytes[entry] == 0 ? 256 : bytes[entry];
                int dataSize = (int)BitConverter.ToUInt32(bytes, entry + 8);
                int dataOffset = (int)BitConverter.ToUInt32(bytes, entry + 12);

                if (w > maxW)
                {
                    maxW = w;
                    bestOffset = dataOffset;
                    bestSize = dataSize;
                }
            }

            if (bestOffset > 0 && bestOffset + bestSize <= bytes.Length)
            {
                var ms = new MemoryStream(bytes, bestOffset, bestSize);
                return Image.FromStream(ms);
            }
        }
        catch { }

        return null;
    }

    private static Font CreateFontSafely(string familyName, float size, FontStyle style)
    {
        try { return new Font(familyName, size, style, GraphicsUnit.Pixel); }
        catch { }
        try { return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Pixel); }
        catch { }
        return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static string GetSafeFolderName(string folderPath)
    {
        try
        {
            var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? "folder" : name;
        }
        catch { return "folder"; }
    }

    private static string GetFolderHash(string folderPath)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant()));
        return Convert.ToHexString(hash);
    }
}
