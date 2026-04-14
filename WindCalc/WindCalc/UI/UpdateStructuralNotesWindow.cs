using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;

// Alias to avoid ambiguity with Autodesk.Revit.DB.Color
using WpfColor = System.Windows.Media.Color;

namespace WindCalc.UI
{
    /// <summary>
    /// Dialog for updating code-edition references in the S-001 Structural Notes sheet.
    /// Finds every TextNote in every view placed on the sheet and applies
    /// ordered string replacements to match the selected FBC edition.
    /// </summary>
    public class UpdateStructuralNotesWindow : Window
    {
        private readonly Document _doc;

        private RadioButton _rb8th;
        private RadioButton _rb9th;
        private TextBox     _txtSheet;
        private TextBox     _txtLog;
        private ScrollViewer _logScroller;
        private Button      _btnUpdate;

        // ── Replacement tables (most-specific first) ──────────────────────────
        private static readonly (string Old, string New)[] To9th =
        {
            ("FBC-B 2023, 8th Edition",  "FBC-B 2026, 9th Edition"),
            ("FBC-R 2023, 8th Edition",  "FBC-R 2026, 9th Edition"),
            ("FBC-M 2023, 8th Edition",  "FBC-M 2026, 9th Edition"),
            ("FBC-P 2023, 8th Edition",  "FBC-P 2026, 9th Edition"),
            ("FBC-F 2023, 8th Edition",  "FBC-F 2026, 9th Edition"),
            ("FBC-G 2023, 8th Edition",  "FBC-G 2026, 9th Edition"),
            ("FBC 2023 (8th Edition)",   "FBC 2026 (9th Edition)"),
            ("FBC 2023, 8th Edition",    "FBC 2026, 9th Edition"),
            ("2023 Florida Building Code, 8th Edition", "2026 Florida Building Code, 9th Edition"),
            ("2023 Florida Building Code",              "2026 Florida Building Code"),
            ("FBC-B 2023",  "FBC-B 2026"),
            ("FBC-R 2023",  "FBC-R 2026"),
            ("FBC-M 2023",  "FBC-M 2026"),
            ("FBC-P 2023",  "FBC-P 2026"),
            ("FBC-F 2023",  "FBC-F 2026"),
            ("FBC-G 2023",  "FBC-G 2026"),
            ("FBC 2023",    "FBC 2026"),
            ("8th Edition", "9th Edition"),
            ("NEC 2020, NFPA 70",  "NEC 2023, NFPA 70"),
            ("NEC 2020",           "NEC 2023"),
            ("NDS 2018",  "NDS 2024"),
            ("NDS-2018",  "NDS-2024"),
            ("FFPC 2023",  "FFPC 2026"),
            ("AWC SDPWS-2015",  "AWC SDPWS-2021"),
            ("SDPWS-2015",      "SDPWS-2021"),
            ("IPC 2021",  "IPC 2024"),
        };

        // 9th→8th is the exact inverse, applied in reverse order
        private static readonly (string Old, string New)[] To8th =
            To9th.Select(t => (t.New, t.Old)).Reverse().ToArray();

        public UpdateStructuralNotesWindow(Document doc)
        {
            _doc  = doc;
            Title = "Update Structural Notes – Code Edition";
            Width  = 530;
            Height = 500;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            BuildUI();
        }

        private void BuildUI()
        {
            var outer = new StackPanel { Margin = new Thickness(12) };

            // ── Instructions ─────────────────────────────────────────────────
            outer.Children.Add(new TextBlock
            {
                Text = "Select the target code edition. Every text note in every view\n" +
                       "placed on the specified sheet will be updated — FBC edition,\n" +
                       "NEC, NDS, FFPC, SDPWS, and IPC citations are all replaced.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10)
            });

            // ── Edition radios ────────────────────────────────────────────────
            outer.Children.Add(new TextBlock
            {
                Text       = "Target Code Edition:",
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 4)
            });

            _rb8th = new RadioButton
            {
                Content = "FBC 8th Edition (2023)  –  NEC 2020  |  NDS 2018",
                Margin  = new Thickness(8, 2, 0, 2)
            };
            _rb9th = new RadioButton
            {
                Content   = "FBC 9th Edition (2026)  –  NEC 2023  |  NDS 2024",
                IsChecked = true,
                Margin    = new Thickness(8, 2, 0, 10)
            };
            outer.Children.Add(_rb8th);
            outer.Children.Add(_rb9th);

            // ── Sheet number ──────────────────────────────────────────────────
            var sheetRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 10)
            };
            sheetRow.Children.Add(new TextBlock
            {
                Text              = "Sheet Number:",
                FontWeight        = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0)
            });
            _txtSheet = new TextBox
            {
                Text              = "S-001",
                Width             = 80,
                Padding           = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            sheetRow.Children.Add(_txtSheet);
            sheetRow.Children.Add(new TextBlock
            {
                Text              = "  (editable if your sheet is numbered differently)",
                Foreground        = Brushes.Gray,
                FontStyle         = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center
            });
            outer.Children.Add(sheetRow);

            outer.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 8) });

            // ── Log ──────────────────────────────────────────────────────────
            _txtLog = new TextBox
            {
                IsReadOnly   = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily   = new FontFamily("Consolas"),
                FontSize     = 11,
                Background   = Brushes.WhiteSmoke,
                Padding      = new Thickness(6),
                Text         = "Ready. Click \"Update Notes\" to begin.",
                Height       = 220
            };
            _logScroller = new ScrollViewer
            {
                Content                     = _txtLog,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin                      = new Thickness(0, 0, 0, 10),
                BorderThickness             = new Thickness(1),
                BorderBrush                 = Brushes.LightGray
            };
            outer.Children.Add(_logScroller);

            // ── Buttons ───────────────────────────────────────────────────────
            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _btnUpdate = new Button
            {
                Content    = "Update Notes",
                Padding    = new Thickness(14, 6, 14, 6),
                Margin     = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(WpfColor.FromRgb(26, 82, 118)),
                Foreground = Brushes.White
            };
            _btnUpdate.Click += BtnUpdate_Click;

            var btnClose = new Button { Content = "Close", Padding = new Thickness(14, 6, 14, 6) };
            btnClose.Click += (s, e) => Close();

            btnRow.Children.Add(_btnUpdate);
            btnRow.Children.Add(btnClose);
            outer.Children.Add(btnRow);

            Content = new ScrollViewer
            {
                Content                     = outer,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            _btnUpdate.IsEnabled = false;
            _txtLog.Text         = "";

            try
            {
                string sheetNum = _txtSheet.Text.Trim();
                bool   to9th    = _rb9th.IsChecked == true;
                var    table    = to9th ? To9th : To8th;
                string edition  = to9th ? "9th Edition (2026)" : "8th Edition (2023)";

                Log($"Target edition : FBC {edition}");
                Log($"Searching sheet: {sheetNum}");

                // ── Find the sheet ───────────────────────────────────────────
                var sheet = new FilteredElementCollector(_doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .FirstOrDefault(vs =>
                        string.Equals(vs.SheetNumber, sheetNum,
                            StringComparison.OrdinalIgnoreCase));

                if (sheet == null)
                {
                    // Fallback: name contains "STRUCTURAL" and "NOTE"
                    sheet = new FilteredElementCollector(_doc)
                        .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .FirstOrDefault(vs =>
                            vs.Name.IndexOf("STRUCTURAL", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            vs.Name.IndexOf("NOTE",       StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (sheet == null)
                {
                    Log($"[ERROR] Sheet \"{sheetNum}\" not found.");
                    Log("       Check the sheet number and try again.");
                    return;
                }

                Log($"Found         : {sheet.SheetNumber} – {sheet.Name}");

                // ── Collect all views on the sheet ───────────────────────────
                var viewIds = new HashSet<ElementId>();
                viewIds.Add(sheet.Id);   // text notes placed directly on the sheet

                foreach (ElementId vpId in sheet.GetAllViewports())
                {
                    if (_doc.GetElement(vpId) is Viewport vp)
                        viewIds.Add(vp.ViewId);
                }

                Log($"Views to scan : {viewIds.Count}");

                // ── Collect TextNotes ────────────────────────────────────────
                var notes = new List<TextNote>();
                foreach (ElementId vid in viewIds)
                {
                    notes.AddRange(
                        new FilteredElementCollector(_doc, vid)
                            .OfClass(typeof(TextNote))
                            .Cast<TextNote>());
                }

                Log($"Text notes    : {notes.Count}");

                // ── Replace inside a transaction ─────────────────────────────
                int changed      = 0;
                int replacements = 0;

                using (var tx = new Transaction(_doc,
                    $"Update Structural Notes → FBC {edition}"))
                {
                    tx.Start();

                    foreach (var tn in notes)
                    {
                        string orig    = tn.Text;
                        string updated = orig;

                        foreach (var (Old, New) in table)
                            updated = updated.Replace(Old, New);

                        if (updated == orig) continue;

                        tn.Text = updated;
                        changed++;

                        // Count individual hits
                        foreach (var (Old, _) in table)
                        {
                            int idx = 0;
                            while ((idx = orig.IndexOf(Old, idx, StringComparison.Ordinal)) >= 0)
                            {
                                replacements++;
                                idx += Old.Length;
                            }
                        }
                    }

                    tx.Commit();
                }

                Log("");
                Log($"✓ Done: {changed} note(s) updated, {replacements} replacement(s).");
                Log($"  All citations now reference FBC {edition}.");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] {ex.Message}");
            }
            finally
            {
                _btnUpdate.IsEnabled = true;
                _logScroller.ScrollToBottom();
            }
        }

        private void Log(string msg)
        {
            _txtLog.Text += msg + "\n";
        }
    }
}
