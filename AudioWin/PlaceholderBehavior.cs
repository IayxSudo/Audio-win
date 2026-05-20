using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioWin
{
    public static class PlaceholderBehavior
    {
        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(PlaceholderBehavior), new PropertyMetadata(default(string), OnPlaceholderChanged));

        public static string GetPlaceholder(DependencyObject obj) => (string)obj.GetValue(PlaceholderProperty);
        public static void SetPlaceholder(DependencyObject obj, string value) => obj.SetValue(PlaceholderProperty, value);

        private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBox)
            {
                textBox.Loaded += (s, args) => UpdatePlaceholder(textBox);
                textBox.GotFocus += (s, args) => UpdatePlaceholder(textBox);
                textBox.LostFocus += (s, args) => UpdatePlaceholder(textBox);
                textBox.TextChanged += (s, args) => UpdatePlaceholder(textBox);
            }
        }

        private static void UpdatePlaceholder(TextBox textBox)
        {
            if (string.IsNullOrEmpty(textBox.Text) && !textBox.IsFocused)
            {
                VisualBrush brush = new VisualBrush();
                TextBlock label = new TextBlock() { Text = GetPlaceholder(textBox), Foreground = Brushes.Gray, Margin = new Thickness(5, 0, 0, 0) };
                brush.Visual = label;
                brush.Stretch = Stretch.None;
                brush.TileMode = TileMode.None;
                brush.AlignmentX = AlignmentX.Left;
                textBox.Background = brush;
            }
            else
            {
                textBox.Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
            }
        }
    }
}
