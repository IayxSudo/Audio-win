using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AudioWin
{
    /// <summary>
    /// Small bag of attached properties used by the control templates.
    /// Replaces the old PlaceholderBehavior, which painted a VisualBrush onto
    /// the TextBox background and then reset it to hard-coded black - that broke
    /// the light theme and fought with the control template.
    /// </summary>
    public static class Ui
    {
        public static readonly DependencyProperty WatermarkProperty =
            DependencyProperty.RegisterAttached(
                "Watermark", typeof(string), typeof(Ui),
                new FrameworkPropertyMetadata(string.Empty));

        public static string GetWatermark(DependencyObject o) => (string)o.GetValue(WatermarkProperty);
        public static void SetWatermark(DependencyObject o, string v) => o.SetValue(WatermarkProperty, v);
    }

    /// <summary>true -&gt; Visible, false -&gt; Collapsed. Pass "invert" to flip.</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool bo && bo;
            if (parameter as string == "invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    /// <summary>Non-empty string / non-null -&gt; Visible. Pass "invert" to flip.</summary>
    public class HasValueToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool has = value != null && !(value is string s && string.IsNullOrWhiteSpace(s));
            if (parameter as string == "invert") has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Maps a TrackSource to the small coloured badge shown on link tracks.</summary>
    public class SourceToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var src = value is TrackSource ts ? ts : TrackSource.Local;
            return src switch
            {
                TrackSource.YouTube => new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)),
                TrackSource.Spotify => new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0x7A)),
                TrackSource.SoundCloud => new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x00)),
                TrackSource.Web => new SolidColorBrush(Color.FromRgb(0x5A, 0x9B, 0xF5)),
                _ => Brushes.Transparent
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Subtracts the converter parameter from a double. Used for layout maths in XAML.</summary>
    public class SubtractConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double d = value is double v ? v : 0;
            double p = 0;
            if (parameter != null) double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out p);
            return Math.Max(0, d - p);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
