using System.IO;
using System.Windows;
using System.Windows.Controls;
using CCorpPrint.Models;

namespace CCorpPrint.UI
{
    public class SettingsWindow : Window
    {
        public PrintConfig Config { get; }

        private TextBox       _txtFolder;
        private CheckBox      _chkDated;
        private CheckBox      _chkLogging;
        private CheckBox      _chkUnderscore;
        private TextBox       _txtMaxLen;
        private ComboBox      _cmbMissing;
        private ComboBox      _cmbGroupBy;

        public SettingsWindow(PrintConfig config)
        {
            Config  = config;
            Title   = "CCorp Print — Settings";
            Width   = 520;
            Height  = 460;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Build();
        }

        private void Build()
        {
            var outer = new StackPanel { Margin = new Thickness(12) };

            outer.Children.Add(WpfHelpers.Label("Default output folder"));
            var folderRow = new Grid();
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _txtFolder = new TextBox { Text = Config.DefaultOutputFolder ?? "" };
            Grid.SetColumn(_txtFolder, 0);
            folderRow.Children.Add(_txtFolder);
            var btnBrowse = WpfHelpers.BtnSecondary("Browse...");
            btnBrowse.Margin = new Thickness(6, 0, 0, 0);
            btnBrowse.Click += (_, __) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    SelectedPath = Directory.Exists(_txtFolder.Text) ? _txtFolder.Text : ""
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    _txtFolder.Text = dlg.SelectedPath;
            };
            Grid.SetColumn(btnBrowse, 1);
            folderRow.Children.Add(btnBrowse);
            outer.Children.Add(folderRow);

            _chkDated = new CheckBox { Content = "Use dated subfolder (yyyy-MM-dd)", IsChecked = Config.UseDatedSubfolder, Margin = new Thickness(0, 8, 0, 0) };
            outer.Children.Add(_chkDated);

            outer.Children.Add(WpfHelpers.Label("Group output by"));
            _cmbGroupBy = new ComboBox();
            foreach (GroupBy g in System.Enum.GetValues(typeof(GroupBy))) _cmbGroupBy.Items.Add(g);
            _cmbGroupBy.SelectedItem = Config.DefaultGroupBy;
            outer.Children.Add(_cmbGroupBy);

            outer.Children.Add(WpfHelpers.Label("Missing-token policy"));
            _cmbMissing = new ComboBox();
            foreach (MissingParamPolicy p in System.Enum.GetValues(typeof(MissingParamPolicy))) _cmbMissing.Items.Add(p);
            _cmbMissing.SelectedItem = Config.MissingParamPolicy;
            outer.Children.Add(_cmbMissing);

            _chkUnderscore = new CheckBox { Content = "Replace whitespace with underscore", IsChecked = Config.ReplaceWhitespaceWithUnderscore, Margin = new Thickness(0, 8, 0, 0) };
            outer.Children.Add(_chkUnderscore);

            outer.Children.Add(WpfHelpers.Label("Max filename length"));
            _txtMaxLen = new TextBox { Text = Config.MaxFilenameLength.ToString() };
            outer.Children.Add(_txtMaxLen);

            _chkLogging = new CheckBox { Content = "Enable logging", IsChecked = Config.LoggingEnabled, Margin = new Thickness(0, 8, 0, 0) };
            outer.Children.Add(_chkLogging);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var save = WpfHelpers.BtnPrimary("Save");
            save.Click += (_, __) => SaveAndClose();
            var cancel = WpfHelpers.BtnSecondary("Cancel");
            cancel.Click += (_, __) => Close();
            btnRow.Children.Add(save);
            btnRow.Children.Add(cancel);
            outer.Children.Add(btnRow);

            Content = new ScrollViewer { Content = outer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void SaveAndClose()
        {
            Config.DefaultOutputFolder = _txtFolder.Text;
            Config.UseDatedSubfolder = _chkDated.IsChecked == true;
            Config.DefaultGroupBy = (GroupBy)_cmbGroupBy.SelectedItem;
            Config.MissingParamPolicy = (MissingParamPolicy)_cmbMissing.SelectedItem;
            Config.ReplaceWhitespaceWithUnderscore = _chkUnderscore.IsChecked == true;
            if (int.TryParse(_txtMaxLen.Text, out var n) && n >= 20) Config.MaxFilenameLength = n;
            Config.LoggingEnabled = _chkLogging.IsChecked == true;
            Config.Save();
            DialogResult = true;
            Close();
        }
    }
}
