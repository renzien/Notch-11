using System;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Media;

public class AppMediaState
{
    public bool HasMedia { get; set; }
    public bool IsPlaying { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public BitmapImage? Thumbnail { get; set; }
    public string SourceAppId { get; set; } = string.Empty;
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
}
