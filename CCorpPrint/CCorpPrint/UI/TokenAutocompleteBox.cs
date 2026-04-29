using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CCorpPrint.Engine;

namespace CCorpPrint.UI
{
    /// <summary>
    /// A TextBox that opens an autocomplete popup of available tokens whenever
    /// the user types '{', filtered by what they type after it. Tab or Enter
    /// inserts the highlighted token (with the closing '}'); Escape cancels.
    /// </summary>
    public class TokenAutocompleteBox : TextBox
    {
        private readonly Popup _popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        private readonly ListBox _list = new ListBox { MinWidth = 220, MaxHeight = 200 };
        private IReadOnlyList<TokenInfo> _all = Array.Empty<TokenInfo>();
        private int _braceStart = -1;

        public TokenAutocompleteBox()
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas");
            FontSize = 13;
            Padding = new Thickness(4);
            AcceptsReturn = false;

            _popup.Child = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Child = _list,
            };
            _popup.PlacementTarget = this;

            TextChanged += OnTextChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            _list.MouseDoubleClick += (_, __) => InsertSelected();
        }

        public void SetTokens(IReadOnlyList<TokenInfo> tokens)
        {
            _all = tokens ?? Array.Empty<TokenInfo>();
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            int caret = CaretIndex;
            if (caret <= 0 || caret > Text.Length)
            {
                _popup.IsOpen = false;
                return;
            }

            // Find an unmatched '{' to the left of the caret
            int brace = -1;
            for (int i = caret - 1; i >= 0; i--)
            {
                char c = Text[i];
                if (c == '}') break;
                if (c == '{')
                {
                    if (i > 0 && Text[i - 1] == '{') break; // {{ escape
                    brace = i;
                    break;
                }
            }

            if (brace < 0)
            {
                _popup.IsOpen = false;
                _braceStart = -1;
                return;
            }

            _braceStart = brace;
            string filter = Text.Substring(brace + 1, caret - brace - 1);
            ShowFiltered(filter);
        }

        private void ShowFiltered(string filter)
        {
            _list.Items.Clear();
            var matches = string.IsNullOrEmpty(filter)
                ? _all
                : _all.Where(t => t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var t in matches)
            {
                var label = t.Name + (string.IsNullOrEmpty(t.SampleValue) ? "" : "   = " + t.SampleValue);
                _list.Items.Add(new ListBoxItem { Content = label, Tag = t.Name });
            }
            if (_list.Items.Count == 0) { _popup.IsOpen = false; return; }
            _list.SelectedIndex = 0;
            _popup.IsOpen = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_popup.IsOpen) return;

            if (e.Key == Key.Down)
            {
                if (_list.SelectedIndex < _list.Items.Count - 1) _list.SelectedIndex++;
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (_list.SelectedIndex > 0) _list.SelectedIndex--;
                e.Handled = true;
            }
            else if (e.Key == Key.Tab || e.Key == Key.Enter)
            {
                InsertSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _popup.IsOpen = false;
                e.Handled = true;
            }
        }

        private void InsertSelected()
        {
            if (_braceStart < 0 || _list.SelectedItem == null) { _popup.IsOpen = false; return; }
            var name = ((ListBoxItem)_list.SelectedItem).Tag as string;
            if (string.IsNullOrEmpty(name)) { _popup.IsOpen = false; return; }

            int caret = CaretIndex;
            string before = Text.Substring(0, _braceStart);
            string after = caret <= Text.Length ? Text.Substring(caret) : "";
            // Skip a closing '}' if user already typed one
            if (after.StartsWith("}")) after = after.Substring(1);

            string insert = "{" + name + "}";
            Text = before + insert + after;
            CaretIndex = before.Length + insert.Length;
            _popup.IsOpen = false;
            _braceStart = -1;
        }
    }
}
