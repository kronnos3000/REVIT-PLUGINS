using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using CCorpPrint.Engine;
using CCorpPrint.Models;
using CCorpPrint.RevitIO;
using CCorpPrint.Services;
using Binding = System.Windows.Data.Binding;
using UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger;
using Grid = System.Windows.Controls.Grid;

namespace CCorpPrint.UI
{
    public class PrintSheetsWindow : Window
    {
        private readonly Document _doc;
        private readonly PrintConfig _cfg;
        private readonly NameTemplateEngine _engine;
        private readonly Logger _log;
        private readonly List<NamingTemplate> _templates;

        private ObservableCollection<SheetVm> _all;
        private ObservableCollection<SheetVm> _filtered;

        // UI controls
        private DataGrid _grid;
        private TextBox _txtFilter;
        private ComboBox _cmbPrintSet;
        private ComboBox _cmbRevision;
        private RadioButton _rbSeparate, _rbCombined, _rbPhysical;
        private ComboBox _cmbPrinter;
        private CheckBox _chkSystemPrintDialog;
        private TextBox _txtFolder;
        private CheckBox _chkDated;
        private ComboBox _cmbGroupBy;
        private TokenAutocompleteBox _txtPerSheet;
        private TokenAutocompleteBox _txtCombined;
        private ComboBox _cmbTemplate;
        private ListBox _previewList;
        private TextBlock _selectedCount;
        private DispatcherTimer _previewTimer;

        public ResultSummary Result { get; private set; }

        public PrintSheetsWindow(Document doc, PrintConfig cfg, Logger log)
        {
            _doc    = doc;
            _cfg    = cfg;
            _log    = log;
            _engine = new NameTemplateEngine(cfg, doc);

            var store = new ProjectInfoTemplateStore(doc);
            _templates = store.MergeWithUser(cfg.Templates);

            Title  = "CCorp Print — Print Sheets";
            Width  = 1200;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Build();
            LoadSheets();
        }

        // ── layout ───────────────────────────────────────────────────────────

        private void Build()
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── filter bar (row 0, span 2) ───────────────────────────────────
            var filterBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            filterBar.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _txtFilter = new TextBox { Width = 180 };
            _txtFilter.TextChanged += (_, __) => ApplyFilter();
            filterBar.Children.Add(_txtFilter);

            filterBar.Children.Add(new TextBlock { Text = "Print Set:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
            _cmbPrintSet = new ComboBox { Width = 180 };
            _cmbPrintSet.SelectionChanged += (_, __) => ApplyPrintSetFilter();
            filterBar.Children.Add(_cmbPrintSet);

            filterBar.Children.Add(new TextBlock { Text = "Revision:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
            _cmbRevision = new ComboBox { Width = 120 };
            _cmbRevision.SelectionChanged += (_, __) => ApplyFilter();
            filterBar.Children.Add(_cmbRevision);

            var btnSelectAll = WpfHelpers.BtnSecondary("Select all");
            btnSelectAll.Click += (_, __) => { foreach (var s in _filtered) s.IsSelected = true; UpdatePreview(); };
            btnSelectAll.Margin = new Thickness(12, 0, 4, 0);
            filterBar.Children.Add(btnSelectAll);
            var btnSelectNone = WpfHelpers.BtnSecondary("Clear");
            btnSelectNone.Click += (_, __) => { foreach (var s in _filtered) s.IsSelected = false; UpdatePreview(); };
            filterBar.Children.Add(btnSelectNone);

            _selectedCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Foreground = Brushes.Gray };
            filterBar.Children.Add(_selectedCount);

            Grid.SetRow(filterBar, 0);
            Grid.SetColumnSpan(filterBar, 2);
            root.Children.Add(filterBar);

            // ── sheet grid (row 1, col 0) ────────────────────────────────────
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows      = false,
                CanUserDeleteRows   = false,
                IsReadOnly          = false,
                SelectionMode       = DataGridSelectionMode.Extended,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                Margin              = new Thickness(0, 0, 6, 0),
            };
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "", Binding = new Binding(nameof(SheetVm.IsSelected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 32 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Number",   Binding = new Binding(nameof(SheetVm.Number)),   IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Auto) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Name",     Binding = new Binding(nameof(SheetVm.Name)),     IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Revision", Binding = new Binding(nameof(SheetVm.Revision)), IsReadOnly = true });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Issued",   Binding = new Binding(nameof(SheetVm.IssueDate)), IsReadOnly = true });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Size",     Binding = new Binding(nameof(SheetVm.Size)),     IsReadOnly = true });
            Grid.SetRow(_grid, 1);
            Grid.SetColumn(_grid, 0);
            root.Children.Add(_grid);

            // ── output panel (row 1, col 1) ──────────────────────────────────
            var rightScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var right = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };

            // Output mode
            var modeStack = new StackPanel();
            _rbSeparate = new RadioButton { Content = "Separate PDFs",    IsChecked = true, GroupName = "mode" };
            _rbCombined = new RadioButton { Content = "Combined PDF",     GroupName = "mode" };
            _rbPhysical = new RadioButton { Content = "Physical printer", GroupName = "mode" };
            _rbSeparate.Checked += (_, __) => RefreshModeUi();
            _rbCombined.Checked += (_, __) => RefreshModeUi();
            _rbPhysical.Checked += (_, __) => RefreshModeUi();
            modeStack.Children.Add(_rbSeparate);
            modeStack.Children.Add(_rbCombined);
            modeStack.Children.Add(_rbPhysical);
            right.Children.Add(WpfHelpers.Group("Output mode", modeStack));

            // Printer (only when physical)
            var printerStack = new StackPanel();
            _cmbPrinter = new ComboBox();
            var printers = new List<string>();
            foreach (string p in System.Drawing.Printing.PrinterSettings.InstalledPrinters) printers.Add(p);
            foreach (var p in printers) _cmbPrinter.Items.Add(p);
            _cmbPrinter.SelectedItem = PickDefaultPrinter(printers);
            printerStack.Children.Add(_cmbPrinter);

            _chkSystemPrintDialog = new CheckBox
            {
                Content   = "Use Windows print dialog (advanced options)",
                IsChecked = false,
                Margin    = new Thickness(0, 6, 0, 0),
                ToolTip   = "If checked, the standard Windows print dialog opens before submitting.\n" +
                            "Use it to access duplex, copies, paper trays, and any other\n" +
                            "printer-specific options that aren't exposed here."
            };
            printerStack.Children.Add(_chkSystemPrintDialog);
            var printerGroup = WpfHelpers.Group("Printer", printerStack);
            printerGroup.Name = "PrinterGroup";
            right.Children.Add(printerGroup);

            // Folder + group rules
            var folderStack = new StackPanel();
            var folderRow = new Grid();
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _txtFolder = new TextBox { Text = _cfg.DefaultOutputFolder ?? "" };
            Grid.SetColumn(_txtFolder, 0);
            folderRow.Children.Add(_txtFolder);
            var btnBrowse = WpfHelpers.BtnSecondary("Browse...");
            btnBrowse.Margin = new Thickness(6, 0, 0, 0);
            btnBrowse.Click += (_, __) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                { SelectedPath = Directory.Exists(_txtFolder.Text) ? _txtFolder.Text : "" };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    _txtFolder.Text = dlg.SelectedPath;
            };
            Grid.SetColumn(btnBrowse, 1);
            folderRow.Children.Add(btnBrowse);
            folderStack.Children.Add(folderRow);
            _chkDated = new CheckBox { Content = "Dated subfolder (yyyy-MM-dd)", IsChecked = _cfg.UseDatedSubfolder, Margin = new Thickness(0, 6, 0, 0) };
            folderStack.Children.Add(_chkDated);
            folderStack.Children.Add(new TextBlock { Text = "Group by:", Margin = new Thickness(0, 6, 0, 2) });
            _cmbGroupBy = new ComboBox();
            foreach (GroupBy g in Enum.GetValues(typeof(GroupBy))) _cmbGroupBy.Items.Add(g);
            _cmbGroupBy.SelectedItem = _cfg.DefaultGroupBy;
            folderStack.Children.Add(_cmbGroupBy);
            right.Children.Add(WpfHelpers.Group("Output folder", folderStack));

            // Templates
            var nameStack = new StackPanel();
            nameStack.Children.Add(new TextBlock { Text = "Saved template:" });
            _cmbTemplate = new ComboBox { DisplayMemberPath = "Name" };
            _cmbTemplate.ItemsSource = _templates;
            _cmbTemplate.SelectionChanged += (_, __) => LoadTemplate();
            nameStack.Children.Add(_cmbTemplate);

            nameStack.Children.Add(new TextBlock { Text = "Per-sheet name (use {tokens})", Margin = new Thickness(0, 6, 0, 2) });
            _txtPerSheet = new TokenAutocompleteBox { Text = "{Sheet Number}_{Sheet Name}" };
            _txtPerSheet.SetTokens(_engine.AvailableTokens());
            _txtPerSheet.TextChanged += (_, __) => DebouncePreview();
            nameStack.Children.Add(_txtPerSheet);

            nameStack.Children.Add(new TextBlock { Text = "Combined-PDF name", Margin = new Thickness(0, 6, 0, 2) });
            _txtCombined = new TokenAutocompleteBox { Text = "{ProjectFileName}_FullSet_{Today:yyyy-MM-dd}" };
            _txtCombined.SetTokens(_engine.AvailableTokens());
            nameStack.Children.Add(_txtCombined);

            right.Children.Add(WpfHelpers.Group("Naming", nameStack));

            // Live preview
            var previewStack = new StackPanel();
            previewStack.Children.Add(new TextBlock
            {
                Text = "First 5 selected sheets:",
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = Brushes.Gray
            });
            _previewList = new ListBox
            {
                Height = 130,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            };
            previewStack.Children.Add(_previewList);
            right.Children.Add(WpfHelpers.Group("Live preview", previewStack));

            rightScroll.Content = right;
            Grid.SetRow(rightScroll, 1);
            Grid.SetColumn(rightScroll, 1);
            root.Children.Add(rightScroll);

            // ── bottom buttons (row 2, span 2) ───────────────────────────────
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var btnPrint = WpfHelpers.BtnPrimary("Print");
            btnPrint.Click += (_, __) => RunPrint();
            var btnCancel = WpfHelpers.BtnSecondary("Cancel");
            btnCancel.Click += (_, __) => Close();
            btnRow.Children.Add(btnPrint);
            btnRow.Children.Add(btnCancel);
            Grid.SetRow(btnRow, 2);
            Grid.SetColumnSpan(btnRow, 2);
            root.Children.Add(btnRow);

            // Wrap the print UI in a TabControl so Templates and Settings live
            // alongside it instead of needing separate ribbon buttons.
            var tabs = new TabControl { Margin = new Thickness(0) };
            tabs.Items.Add(new TabItem { Header = "Print",     Content = root });
            tabs.Items.Add(new TabItem { Header = "Templates", Content = BuildTemplatesPanel() });
            tabs.Items.Add(new TabItem { Header = "Settings",  Content = BuildSettingsPanel() });
            Content = tabs;

            // Debounced preview timer
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _previewTimer.Tick += (_, __) => { _previewTimer.Stop(); UpdatePreview(); };

            RefreshModeUi();
        }

        // ── data ─────────────────────────────────────────────────────────────

        private void LoadSheets()
        {
            var rows = new SheetReader(_doc).Read();
            _all = new ObservableCollection<SheetVm>(rows.Select(r => new SheetVm
            {
                Sheet = r.Sheet,
                Number = r.Number,
                Name = r.Name,
                Revision = r.CurrentRevision,
                IssueDate = r.IssueDate,
                Size = r.TitleblockSize,
                Discipline = r.Discipline,
            }));
            foreach (var s in _all) s.PropertyChanged += (_, __) => DebouncePreview();
            _filtered = new ObservableCollection<SheetVm>(_all);
            _grid.ItemsSource = _filtered;

            _cmbPrintSet.Items.Clear();
            _cmbPrintSet.Items.Add("(all)");
            foreach (var s in new PrintSetService(_doc).AllSets()) _cmbPrintSet.Items.Add(s.Name);
            _cmbPrintSet.SelectedIndex = 0;

            _cmbRevision.Items.Clear();
            _cmbRevision.Items.Add("(any)");
            foreach (var rev in _all.Select(s => s.Revision).Where(r => !string.IsNullOrEmpty(r)).Distinct().OrderBy(r => r))
                _cmbRevision.Items.Add(rev);
            _cmbRevision.SelectedIndex = 0;

            UpdatePreview();
        }

        private void ApplyFilter()
        {
            string txt = (_txtFilter.Text ?? "").Trim();
            string rev = (_cmbRevision.SelectedItem as string) ?? "(any)";
            _filtered.Clear();
            foreach (var s in _all)
            {
                if (!string.IsNullOrEmpty(txt) &&
                    s.Number.IndexOf(txt, StringComparison.OrdinalIgnoreCase) < 0 &&
                    s.Name.IndexOf(txt, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (rev != "(any)" && !string.Equals(s.Revision, rev, StringComparison.OrdinalIgnoreCase))
                    continue;
                _filtered.Add(s);
            }
            UpdatePreview();
        }

        private void ApplyPrintSetFilter()
        {
            var name = _cmbPrintSet.SelectedItem as string;
            if (string.IsNullOrEmpty(name) || name == "(all)") { ApplyFilter(); return; }

            var set = new PrintSetService(_doc).ByName(name);
            if (set == null) { ApplyFilter(); return; }

            var allowed = new HashSet<ElementId>();
            foreach (View v in set.Views) allowed.Add(v.Id);

            // Pre-select sheets in the print set
            foreach (var s in _all)
                s.IsSelected = allowed.Contains(s.Sheet.Id);

            ApplyFilter();
        }

        // ── mode UI ──────────────────────────────────────────────────────────

        private void RefreshModeUi()
        {
            bool physical = _rbPhysical?.IsChecked == true;
            if (_cmbPrinter != null)
                _cmbPrinter.IsEnabled = physical;
            UpdatePreview();
        }

        // ── preview ──────────────────────────────────────────────────────────

        private void DebouncePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
            UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            if (_selectedCount == null || _all == null) return;
            int n = _all.Count(s => s.IsSelected);
            _selectedCount.Text = $"  Selected: {n} / {_all.Count}";
        }

        private void UpdatePreview()
        {
            UpdateSelectedCount();
            if (_previewList == null) return;
            _previewList.Items.Clear();

            var selected = _all?.Where(s => s.IsSelected).Select(s => s.Sheet).Take(5).ToList()
                          ?? new List<ViewSheet>();
            string template = _rbCombined?.IsChecked == true ? _txtCombined.Text : _txtPerSheet.Text;

            if (selected.Count == 0)
            {
                _previewList.Items.Add(new ListBoxItem { Content = "(select sheets to preview)", Foreground = Brushes.Gray });
                return;
            }

            var resolved = _engine.Preview(template ?? "", selected);
            for (int i = 0; i < resolved.Count; i++)
            {
                var r = resolved[i];
                var ext = ".pdf";
                var label = $"{i + 1,2}. {r.FileName}{ext}";
                if (r.MissingTokens?.Count > 0)
                    label += "   ⚠ missing: " + string.Join(", ", r.MissingTokens);
                _previewList.Items.Add(label);
            }
        }

        private void LoadTemplate()
        {
            if (_cmbTemplate.SelectedItem is NamingTemplate t)
            {
                _txtPerSheet.Text = t.PerSheet ?? "";
                _txtCombined.Text = t.Combined ?? "";
                UpdatePreview();
            }
        }

        // ── run ──────────────────────────────────────────────────────────────

        private void RunPrint()
        {
            var selected = _all.Where(s => s.IsSelected).Select(s => s.Sheet).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select at least one sheet.", "CCorp Print");
                return;
            }

            OutputMode mode = _rbCombined.IsChecked == true ? OutputMode.CombinedPdf
                            : _rbPhysical.IsChecked == true ? OutputMode.PhysicalPrinter
                            : OutputMode.SeparatePdf;

            var template = new NamingTemplate
            {
                Name     = "(adhoc)",
                PerSheet = _txtPerSheet.Text,
                Combined = _txtCombined.Text,
            };

            string baseFolder = _txtFolder.Text;
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                MessageBox.Show(this, "Choose an output folder.", "CCorp Print");
                return;
            }

            // Apply dated/group rules. Note: per-sheet grouping (group by discipline/revision)
            // would require splitting the job — for v1 we apply to the base only and let the
            // user re-run per discipline if needed.
            string folder = baseFolder;
            if (_chkDated.IsChecked == true)
                folder = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(folder);

            string printer = null;
            if (mode == OutputMode.PhysicalPrinter)
            {
                printer = _cmbPrinter.SelectedItem as string;

                if (_chkSystemPrintDialog?.IsChecked == true)
                {
                    using var dlg = new System.Windows.Forms.PrintDialog
                    {
                        AllowSomePages = false,
                        AllowSelection = false,
                        AllowPrintToFile = false,
                        UseEXDialog = true,
                    };
                    if (!string.IsNullOrEmpty(printer))
                        dlg.PrinterSettings.PrinterName = printer;

                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return; // user cancelled

                    printer = dlg.PrinterSettings.PrinterName;
                    // Reflect the choice in the dropdown
                    if (_cmbPrinter.Items.Cast<object>().Any(o => string.Equals(o as string, printer, StringComparison.OrdinalIgnoreCase)))
                        _cmbPrinter.SelectedItem = printer;
                }

                if (string.IsNullOrEmpty(printer))
                {
                    MessageBox.Show(this, "Pick a printer.", "CCorp Print");
                    return;
                }
            }

            // Persist convenience settings
            _cfg.DefaultOutputFolder = baseFolder;
            _cfg.UseDatedSubfolder = _chkDated.IsChecked == true;
            _cfg.DefaultGroupBy = (GroupBy)_cmbGroupBy.SelectedItem;
            try { _cfg.Save(); } catch { /* non-fatal */ }

            var runner = new PrintJobRunner(_doc, _cfg, _log);
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                Result = runner.Run(selected, template, mode, folder, printer);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            DialogResult = true;
            Close();
        }

        // ── tab content builders ─────────────────────────────────────────────

        // Templates tab — list + edit pane. Reuses NamingTemplatesWindow's logic
        // by hosting it as a child Window... well, can't host a Window inside a
        // tab. So we build a minimal in-tab editor that calls the existing
        // ProjectInfoTemplateStore / NameTemplateEngine APIs directly.
        private UIElement BuildTemplatesPanel()
        {
            var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var btnOpen = WpfHelpers.BtnPrimary("Open templates editor...");
            btnOpen.Click += (_, __) =>
            {
                var win = new NamingTemplatesWindow(_doc, _cfg) { Owner = this };
                win.ShowDialog();
                // Refresh the saved-template dropdown in the Print tab in case the user added one.
                var store = new ProjectInfoTemplateStore(_doc);
                var refreshed = store.MergeWithUser(_cfg.Templates);
                _cmbTemplate.ItemsSource = null;
                _cmbTemplate.ItemsSource = refreshed;
            };
            btnRow.Children.Add(btnOpen);
            DockPanel.SetDock(btnRow, Dock.Top);
            panel.Children.Add(btnRow);

            var hint = new TextBlock
            {
                Text = "Naming templates are stored either in your %AppData%\\CCorpPrint\\config.json\n" +
                       "or inside the .rvt itself (so they travel with the project).\n\n" +
                       "Open the editor to add, rename, or delete templates. The Print tab's\n" +
                       "\"Saved template\" dropdown will pick them up automatically.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
            };
            panel.Children.Add(hint);
            return panel;
        }

        // Settings tab — same: launch the existing SettingsWindow as a modal.
        private UIElement BuildSettingsPanel()
        {
            var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(10) };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var btnOpen = WpfHelpers.BtnPrimary("Open settings...");
            btnOpen.Click += (_, __) =>
            {
                var win = new SettingsWindow(_cfg) { Owner = this };
                if (win.ShowDialog() == true)
                {
                    // Reflect updated defaults in the Print tab.
                    _txtFolder.Text = _cfg.DefaultOutputFolder ?? "";
                    _chkDated.IsChecked = _cfg.UseDatedSubfolder;
                    _cmbGroupBy.SelectedItem = _cfg.DefaultGroupBy;
                }
            };
            btnRow.Children.Add(btnOpen);
            DockPanel.SetDock(btnRow, Dock.Top);
            panel.Children.Add(btnRow);

            var hint = new TextBlock
            {
                Text = "Settings include:\n" +
                       "  • Default output folder + dated-subfolder behavior\n" +
                       "  • Missing-token policy (BlankOut / LiteralToken / Error)\n" +
                       "  • Filename sanitizer rules (max length, whitespace handling)\n" +
                       "  • Logging on/off (logs land at %AppData%\\CCorpPrint\\logs\\)",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
            };
            panel.Children.Add(hint);
            return panel;
        }

        // ── printer default selection ────────────────────────────────────────

        // Preference order for the physical-printer dropdown:
        //   1. Adobe PDF             (real PDF driver — best quality, widely installed)
        //   2. PDF24
        //   3. Microsoft Print to PDF (Windows built-in, last-resort PDF)
        //   4. The first installed printer that is NOT OneNote-style or Fax
        //   5. Whatever is at index 0
        //
        // OneNote and Fax are explicitly de-prioritized — they are almost
        // never the user's actual intent and they were the reason this
        // defaulted to "OneNote (Desktop)" before.
        private static string PickDefaultPrinter(IList<string> printers)
        {
            if (printers == null || printers.Count == 0) return null;

            string Find(System.Func<string, bool> pred) =>
                printers.FirstOrDefault(p => p != null && pred(p));

            string m;
            m = Find(p => p.IndexOf("Adobe PDF",            System.StringComparison.OrdinalIgnoreCase) >= 0); if (m != null) return m;
            m = Find(p => p.IndexOf("PDF24",                System.StringComparison.OrdinalIgnoreCase) >= 0); if (m != null) return m;
            m = Find(p => p.IndexOf("Microsoft Print to PDF", System.StringComparison.OrdinalIgnoreCase) >= 0); if (m != null) return m;

            m = Find(p => p.IndexOf("OneNote", System.StringComparison.OrdinalIgnoreCase) < 0
                      && p.IndexOf("Fax",     System.StringComparison.OrdinalIgnoreCase) < 0
                      && p.IndexOf("XPS",     System.StringComparison.OrdinalIgnoreCase) < 0);
            return m ?? printers[0];
        }

        // ── view-model ───────────────────────────────────────────────────────

        public class SheetVm : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _selected;
            public ViewSheet Sheet { get; set; }
            public string Number { get; set; }
            public string Name { get; set; }
            public string Revision { get; set; }
            public string IssueDate { get; set; }
            public string Size { get; set; }
            public string Discipline { get; set; }
            public bool IsSelected
            {
                get => _selected;
                set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))); } }
            }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }
    }
}
