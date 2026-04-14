using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindCalc.Engine;
using WindCalc.Models;

namespace WindCalc.UI
{
    public class SetupDataWindow : Window
    {
        private readonly WindCalcConfig   _config;
        private CancellationTokenSource   _cts;
        private bool                      _running;

        private Button       BtnStart;
        private Button       BtnClose;
        private TextBox      TxtLog;
        private ProgressBar  ProgressBar;
        private ScrollViewer LogScroller;

        public SetupDataWindow(WindCalcConfig config)
        {
            _config = config;
            Title   = "Setup Local Data \u2013 Wind Calculator";
            Width   = 580;
            Height  = 530;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Closing              += Window_Closing;

            BuildWindow();
        }

        private void BuildWindow()
        {
            var outer = new Grid { Margin = new Thickness(12) };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text         = "Download local reference datasets for offline use:\n" +
                               "  \u2022 FEMA NFHL flood zone shapefiles (5 counties)\n" +
                               "  \u2022 Wind speeds: built-in FL county table (no download needed)\n" +
                               "  \u2022 NOAA mean high-water shoreline (coastal proximity)\n" +
                               "  \u2022 NLCD 2021 land cover \u2014 Florida (exposure category)\n" +
                               "  \u2022 Pinellas County + Charlotte County parcel shapefiles\n\n" +
                               "This may take 10\u201320 minutes. Failed downloads are logged with manual instructions.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(header, 0);
            outer.Children.Add(header);

            TxtLog = new TextBox
            {
                IsReadOnly              = true,
                TextWrapping            = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                FontFamily              = new FontFamily("Consolas"),
                FontSize                = 11,
                Background              = Brushes.White,
                Padding                 = new Thickness(6)
            };
            LogScroller = new ScrollViewer
            {
                Content                        = TxtLog,
                VerticalScrollBarVisibility    = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility  = ScrollBarVisibility.Disabled,
                Margin                         = new Thickness(0, 0, 0, 8),
                BorderThickness                = new Thickness(1),
                BorderBrush                    = Brushes.LightGray
            };
            Grid.SetRow(LogScroller, 1);
            outer.Children.Add(LogScroller);

            ProgressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height  = 18,
                Margin  = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(ProgressBar, 2);
            outer.Children.Add(ProgressBar);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            BtnStart = new Button { Content = "Start Download", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            BtnStart.Click += BtnStart_Click;
            BtnClose = new Button { Content = "Close", Padding = new Thickness(10, 6, 10, 6) };
            BtnClose.Click += BtnClose_Click;
            btnPanel.Children.Add(BtnStart);
            btnPanel.Children.Add(BtnClose);
            Grid.SetRow(btnPanel, 3);
            outer.Children.Add(btnPanel);

            Content = outer;
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            _running           = true;
            BtnStart.IsEnabled = false;
            BtnClose.IsEnabled = false;
            TxtLog.Text        = "";
            ProgressBar.Value  = 0;

            _cts = new CancellationTokenSource();
            var downloader = new LocalDataDownloader(_config.LocalDataFolder);

            downloader.ProgressChanged += (msg, pct) =>
                Dispatcher.Invoke(() =>
                {
                    AppendLog(msg);
                    if (pct >= 0) ProgressBar.Value = pct;
                });

            downloader.ErrorOccurred += err =>
                Dispatcher.Invoke(() => AppendLog($"[WARNING] {err}"));

            try
            {
                await downloader.RunFullSetupAsync(_cts.Token);
                AppendLog("All downloads complete. You may close this window.");
            }
            catch (OperationCanceledException)
            {
                AppendLog("Download cancelled.");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] {ex.Message}");
            }
            finally
            {
                _running           = false;
                BtnStart.IsEnabled = true;
                BtnClose.IsEnabled = true;
                ProgressBar.Value  = 100;
            }
        }

        private void AppendLog(string message)
        {
            TxtLog.Text += message + "\n";
            LogScroller.ScrollToBottom();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_running)
            {
                var result = MessageBox.Show(
                    "Download is in progress. Cancel and close?",
                    "Confirm Close", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                _cts?.Cancel();
            }
        }
    }
}
