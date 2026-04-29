using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CCorpPrint.UI
{
    internal static class WpfHelpers
    {
        public static TextBlock Label(string text) => new TextBlock
        {
            Text         = text,
            Margin       = new Thickness(0, 6, 0, 2),
            FontWeight   = FontWeights.SemiBold,
            Foreground   = Brushes.Black,
        };

        public static Button BtnPrimary(string text)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(14, 6, 14, 6),
                Margin  = new Thickness(0, 0, 8, 0),
                MinWidth = 90,
            };
        }

        public static Button BtnSecondary(string text)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin  = new Thickness(0, 0, 6, 0),
            };
        }

        public static GroupBox Group(string header, UIElement content)
        {
            return new GroupBox
            {
                Header  = header,
                Margin  = new Thickness(0, 4, 0, 4),
                Padding = new Thickness(8, 6, 8, 6),
                Content = content,
            };
        }
    }
}
