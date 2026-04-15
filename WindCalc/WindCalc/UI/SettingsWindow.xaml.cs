using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
// System.Windows.Forms used via fully-qualified names below to avoid WPF ambiguity
using System.Windows.Media;
using System.Windows.Shapes;
using WindCalc.Models;

namespace WindCalc.UI
{
    public class SettingsWindow : Window
    {
        private readonly WindCalcConfig _config;
        private System.Windows.Controls.TextBox  TxtAsceKey;
        private System.Windows.Controls.TextBox  TxtFirmMin;
        private System.Windows.Controls.ComboBox CboCodeEdition;
        private System.Windows.Controls.TextBox  TxtDataFolder;

        public SettingsWindow(WindCalcConfig config)
        {
            _config = config;
            Title   = "Wind Calculator \u2013 Settings";
            Width   = 560;
            MinHeight     = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            BuildWindow();

            TxtAsceKey.Text    = config.AsceApiKey;
            TxtFirmMin.Text    = config.FirmMinimumVult.ToString("F0");
            TxtDataFolder.Text = config.LocalDataFolder;
            SelectComboItem(CboCodeEdition,
                config.CodeEdition == "9th"
                    ? "FBC 9th Edition (2026) \u2014 Default"
                    : "FBC 8th Edition (2023)");
        }

        private void BuildWindow()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 10; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ── ASCE API Key ───────────────────────────────────────────────────
            AddLabeledRow(grid, 0, "ASCE API Key:",
                TxtAsceKey = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4, 3, 4, 3) });

            // ── Firm Minimum Vult ──────────────────────────────────────────────
            AddLabeledRow(grid, 1, "Firm Minimum Vult (mph):",
                TxtFirmMin = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4, 3, 4, 3) });

            // ── Code Edition ───────────────────────────────────────────────────
            CboCodeEdition = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 4, 0, 4) };
            CboCodeEdition.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "FBC 9th Edition (2026) \u2014 Default" });
            CboCodeEdition.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "FBC 8th Edition (2023)" });
            CboCodeEdition.SelectedIndex = 0;
            AddLabeledRow(grid, 2, "Building Code Edition:", CboCodeEdition);

            // Edition note
            var edNote = new TextBlock
            {
                Text         = "FBC 9th Edition (2026) effective Dec 31, 2026. Use 8th Edition for projects already permitted under FBC 2023.",
                FontSize     = 10,
                Foreground   = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(edNote, 3); Grid.SetColumnSpan(edNote, 3);
            grid.Children.Add(edNote);

            // ── Separator ─────────────────────────────────────────────────────
            var sep1 = new Rectangle { Height = 1, Fill = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 8) };
            Grid.SetRow(sep1, 4); Grid.SetColumnSpan(sep1, 3);
            grid.Children.Add(sep1);

            // ── Local Data Folder (with Browse button) ────────────────────────
            var folderLabel = new TextBlock
            {
                Text                = "Local Data Folder:",
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 3, 8, 3)
            };
            Grid.SetRow(folderLabel, 5); Grid.SetColumn(folderLabel, 0);
            grid.Children.Add(folderLabel);

            TxtDataFolder = new System.Windows.Controls.TextBox
            {
                Margin  = new Thickness(0, 4, 4, 4),
                Padding = new Thickness(4, 3, 4, 3)
            };
            Grid.SetRow(TxtDataFolder, 5); Grid.SetColumn(TxtDataFolder, 1);
            grid.Children.Add(TxtDataFolder);

            var btnBrowse = new System.Windows.Controls.Button
            {
                Content = "Browse…",
                Padding = new Thickness(8, 3, 8, 3),
                Margin  = new Thickness(0, 4, 0, 4)
            };
            btnBrowse.Click += BtnBrowse_Click;
            Grid.SetRow(btnBrowse, 5); Grid.SetColumn(btnBrowse, 2);
            grid.Children.Add(btnBrowse);

            // Folder note
            var folderNote = new TextBlock
            {
                Text         = "All local data (FEMA flood zones, PCPAO parcels, NLCD) is stored here.\n" +
                               "Point multiple workstations to the same UNC path (\\\\server\\share\\WindCalcData)\n" +
                               "so downloads are shared across the office.",
                FontSize     = 10,
                Foreground   = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(folderNote, 6); Grid.SetColumn(folderNote, 1); Grid.SetColumnSpan(folderNote, 2);
            grid.Children.Add(folderNote);

            // ── General notes ─────────────────────────────────────────────────
            var note = new TextBlock
            {
                Text         = "ASCE API key required for wind speed lookup (amplify.asce.org).\n" +
                               "Firm minimum is applied even when ASCE value is lower.\n" +
                               "Data auto-updates every 7 days on Revit startup.",
                FontSize     = 10,
                Foreground   = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(note, 7); Grid.SetColumnSpan(note, 3);
            grid.Children.Add(note);

            // ── Bottom separator ──────────────────────────────────────────────
            var sep2 = new Rectangle { Height = 1, Fill = Brushes.LightGray, Margin = new Thickness(0, 12, 0, 8) };
            Grid.SetRow(sep2, 8); Grid.SetColumnSpan(sep2, 3);
            grid.Children.Add(sep2);

            // ── Save / Cancel ─────────────────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnCancel = new System.Windows.Controls.Button
            {
                Content  = "Cancel",
                IsCancel = true,
                Padding  = new Thickness(10, 6, 10, 6),
                Margin   = new Thickness(0, 0, 8, 0)
            };
            var btnSave = new System.Windows.Controls.Button
            {
                Content   = "Save",
                IsDefault = true,
                Padding   = new Thickness(10, 6, 10, 6)
            };
            btnSave.Click += BtnSave_Click;
            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnSave);
            Grid.SetRow(btnPanel, 9); Grid.SetColumnSpan(btnPanel, 3);
            grid.Children.Add(btnPanel);

            Content = grid;
        }

        private static void AddLabeledRow(Grid grid, int row, string label, UIElement control)
        {
            var lbl = new TextBlock
            {
                Text              = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 3, 8, 3)
            };
            Grid.SetRow(lbl, row);     Grid.SetColumn(lbl, 0);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1); Grid.SetColumnSpan(control, 2);
            grid.Children.Add(lbl);
            grid.Children.Add(control);
        }

        private static void SelectComboItem(System.Windows.Controls.ComboBox combo, string value)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in combo.Items)
            {
                if (item.Content?.ToString() == value)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Select local data folder (can be a UNC network path)",
                ShowNewFolderButton = true,
                SelectedPath        = TxtDataFolder.Text.Trim()
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtDataFolder.Text = dlg.SelectedPath;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _config.AsceApiKey = TxtAsceKey.Text.Trim();

            if (double.TryParse(TxtFirmMin.Text, out double firmMin) && firmMin > 0)
                _config.FirmMinimumVult = firmMin;

            string selected = (CboCodeEdition.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "";
            _config.CodeEdition = selected.StartsWith("FBC 9") ? "9th" : "8th";

            string folder = TxtDataFolder.Text.Trim();
            if (!string.IsNullOrWhiteSpace(folder))
                _config.LocalDataFolder = folder;

            _config.Save();
            DialogResult = true;
            Close();
        }
    }
}
