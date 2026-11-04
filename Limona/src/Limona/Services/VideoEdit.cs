namespace Limona.Services;

/// <summary>Text burned into the exported frame. Times are absolute ticks of
/// the source timeline so the preview and the export always agree.</summary>
public sealed class TextOverlay
{
    public string Text = "Text";
    public string FontFamily = "Segoe UI";
    public double FontSizeFraction = 0.08;   // of the output height
    public int FontWeight = 400;
    public bool Bold;
    public bool Italic;
    public uint ColorArgb = 0xFFFFFFFF;
    public uint OutlineArgb = 0xB0000000;    // stroke color (alpha 0 = none)
    public double StrokeFraction = 0.07;     // stroke width, of the font size
    public double Opacity = 1.0;             // multiplies every text alpha
    public double XFraction = 0.5;           // centre, of the output frame
    public double YFraction = 0.85;
    public double WidthFraction = 0.9;
    public long StartTicks;
    public long EndTicks;
    public long DurationTicks => Math.Max(0, EndTicks - StartTicks);

    public TextOverlay Clone() => (TextOverlay)MemberwiseClone();
}

/// <summary>Picture/sticker burned into the exported frame.</summary>
public sealed class ImageOverlay
{
    public string Path = "";
    public double XFraction = 0.5;
    public double YFraction = 0.5;
    public double WidthFraction = 0.3;
    public double Opacity = 1.0;
    public long StartTicks;
    public long EndTicks;
    public long DurationTicks => Math.Max(0, EndTicks - StartTicks);

    public ImageOverlay Clone() => (ImageOverlay)MemberwiseClone();
}

/// <summary>Extra audio laid on the timeline: trimmed from its own file and
/// mixed with the video's audio at its own volume.</summary>
public sealed class AudioClip
{
    public string Path = "";
    public string Name = "";
    public long StartTicks;       // timeline position
    public long EndTicks;         // timeline end
    public long SourceStartTicks; // trim inside the audio file
    public double Volume = 1.0;
    public long DurationTicks => Math.Max(0, EndTicks - StartTicks);

    public AudioClip Clone() => (AudioClip)MemberwiseClone();
}

/// <summary>Everything that is applied on top of the cropped/trimmed video.</summary>
public sealed class EditSpec
{
    public List<TextOverlay> Texts { get; } = new();
    public List<ImageOverlay> Images { get; } = new();
    public List<AudioClip> Audios { get; } = new();

    /// <summary>Volume applied to the video's own audio (0..2).</summary>
    public double VideoVolume { get; set; } = 1.0;

    public bool HasOverlays => Texts.Count > 0 || Images.Count > 0;
    public bool HasAddedAudio => Audios.Count > 0;
}
