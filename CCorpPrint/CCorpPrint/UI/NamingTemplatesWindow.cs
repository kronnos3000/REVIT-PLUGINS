using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using CCorpPrint.Engine;
using CCorpPrint.Models;
using CCorpPrint.RevitIO;
using Grid = System.Windows.Controls.Grid;

namespace CCorpPrint.UI
{
    public class NamingTemplatesWindow : Window
    {
        private readonly Document _doc;
        private readonly PrintConfig _cfg;
        private readonly List<NamingTemplate> _templates;
        private readonly NameTemplateEngine _engine;

        private ListBox _list;
        private TextBox _txtName;
        private TokenAutocompleteBox _txtPerSheet;
        private TokenAutocompleteBox _txtCombined;

        public NamingTemplatesWindow(Document doc, PrintConfig cfg)
        {
            _doc = doc;
            _cfg = cfg;
            _engine = new NameTemplateEngine(cfg, doc);

            var store = new ProjectInfoTemplateStore(doc);
            _templates = store.MergeWithUser(cfg.Templates);

            Title = "CCorp Print — Naming Templates";
            Width = 720;
            Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Build();
        }

        private void Build()
        {
            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Left: list + add/delete
            var leftStack = new DockPanel { LastChildFill = true };
            var listToolbar = new StackPanel { Orientation = Orientation.Horizontal };
            var btnAdd = WpfHelpers.BtnSecondary("Add");
            btnAdd.Click += (_, __) =>
            {
                var t = new NamingTemplate { Name = "New template" };
                _templates.Add(t);
                Refresh();
                _list.SelectedItem = t;
            };
            var btnDel = WpfHelpers.BtnSecondary("Delete");
            btnDel.Click += (_, __) =>
            {
                if (_list.SelectedItem is NamingTemplate t)
                {
                    _templates.Remove(t);
                    Refresh();
                }
            };
            listToolbar.Children.Add(btnAdd);
            listToolbar.Children.Add(btnDel);
            DockPanel.SetDock(listToolbar, Dock.Top);

            _list = new ListBox { DisplayMemberPath = "Name" };
            _list.SelectionChanged += (_, __) => LoadEditor();

            leftStack.Children.Add(listToolbar);
            leftStack.Children.Add(_list);
            Grid.SetRow(leftStack, 0);
            Grid.SetColumn(leftStack, 0);
            grid.Children.Add(leftStack);

            // Right: editor
            var editor = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            editor.Children.Add(WpfHelpers.Label("Template name"));
            _txtName = new TextBox();
            _txtName.LostFocus += (_, __) =>
            {
                if (_list.SelectedItem is NamingTemplate t)
                {
                    t.Name = _txtName.Text;
                    Refresh();
                }
            };
            editor.Children.Add(_txtName);

            editor.Children.Add(WpfHelpers.Label("Per-sheet name template"));
            _txtPerSheet = new TokenAutocompleteBox();
            _txtPerSheet.SetTokens(_engine.AvailableTokens());
            _txtPerSheet.LostFocus += (_, __) =>
            {
                if (_list.SelectedItem is NamingTemplate t) t.PerSheet = _txtPerSheet.Text;
            };
            editor.Children.Add(_txtPerSheet);

            editor.Children.Add(WpfHelpers.Label("Combined-PDF name template"));
            _txtCombined = new TokenAutocompleteBox();
            _txtCombined.SetTokens(_engine.AvailableTokens());
            _txtCombined.LostFocus += (_, __) =>
            {
                if (_list.SelectedItem is NamingTemplate t) t.Combined = _txtCombined.Text;
            };
            editor.Children.Add(_txtCombined);

            editor.Children.Add(WpfHelpers.Label("Available tokens (sample values from current doc)"));
            var tokenList = new ListBox { Height = 160, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            foreach (var t in _engine.AvailableTokens())
            {
                tokenList.Items.Add($"{{{t.Name}}}   [{t.Source}]   = {t.SampleValue}");
            }
            editor.Children.Add(tokenList);

            Grid.SetRow(editor, 0);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);

            // Bottom: save buttons
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var saveUser = WpfHelpers.BtnSecondary("Save to user (%AppData%)");
            saveUser.Click += (_, __) =>
            {
                _cfg.Templates = _templates.ToList();
                _cfg.Save();
                MessageBox.Show(this, "Templates saved to user config.", "CCorp Print");
            };
            var saveProject = WpfHelpers.BtnPrimary("Save to project (.rvt)");
            saveProject.Click += (_, __) =>
            {
                var store = new ProjectInfoTemplateStore(_doc);
                store.Save(_templates.ToList());
                MessageBox.Show(this, "Templates saved into project information.", "CCorp Print");
            };
            var close = WpfHelpers.BtnSecondary("Close");
            close.Click += (_, __) => Close();
            btnRow.Children.Add(saveUser);
            btnRow.Children.Add(saveProject);
            btnRow.Children.Add(close);
            Grid.SetRow(btnRow, 1);
            Grid.SetColumnSpan(btnRow, 2);
            grid.Children.Add(btnRow);

            Content = grid;
            Refresh();
            if (_templates.Count > 0) _list.SelectedIndex = 0;
        }

        private void Refresh()
        {
            var sel = _list.SelectedItem;
            _list.ItemsSource = null;
            _list.ItemsSource = _templates;
            _list.SelectedItem = sel;
        }

        private void LoadEditor()
        {
            if (!(_list.SelectedItem is NamingTemplate t)) return;
            _txtName.Text     = t.Name ?? "";
            _txtPerSheet.Text = t.PerSheet ?? "";
            _txtCombined.Text = t.Combined ?? "";
        }
    }
}
