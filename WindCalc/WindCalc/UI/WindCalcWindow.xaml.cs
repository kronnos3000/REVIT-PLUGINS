using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WindCalc.Engine;
using WindCalc.Models;

namespace WindCalc.UI
{
    public class WindCalcWindow : Window
    {
        // ── Confidence indicator colors ────────────────────────────────────────
        private static readonly SolidColorBrush _confirmed  = new SolidColorBrush(Color.FromRgb(46,  125, 50));   // green
        private static readonly SolidColorBrush _estimated  = new SolidColorBrush(Color.FromRgb(245, 124,  0));   // amber
        private static readonly SolidColorBrush _manual     = new SolidColorBrush(Color.FromRgb(100, 150, 220));  // blue-gray (manual)

        // ── Named controls — Site tab ─────────────────────────────────────────
        private TabControl  TabMain;
        private TextBox     TxtAddress;
        private Button      BtnFetch;
        private TextBlock   TxtStatus;

        // Editable site data fields (always visible, always editable)
        private TextBox     TxtLat, TxtLon;
        private TextBox     TxtVultRcI, TxtVultRcII, TxtVultRcIII, TxtVultRcIV, TxtVasd;
        private TextBox     TxtAppliedVult;
        private TextBox     TxtElevation;
        private ComboBox    CboFloodZone;
        private TextBox     TxtBfe;
        private ComboBox    CboExposure;
        private ComboBox    CboRiskCategory;
        private CheckBox    ChkCoastal;
        private TextBox     TxtYearBuilt, TxtConstClass, TxtParcelId;

        // Confidence borders (set after fetch)
        private Border      BrdLat, BrdLon;
        private Border      BrdVultRcI, BrdVultRcII, BrdVultRcIII, BrdVultRcIV, BrdVasd;
        private Border      BrdApplied;
        private Border      BrdElevation;
        private Border      BrdFloodZone, BrdBfe;
        private Border      BrdExposure;
        private Border      BrdYearBuilt, BrdConstClass, BrdParcelId;

        // Computed indicators
        private TextBlock   TxtWindborneStatus, TxtEnvelope160Status;

        // ── Named controls — Building tab ─────────────────────────────────────
        private ComboBox    CboRoofType;
        private TextBox     TxtRoofPitch, TxtRidgeHeight, TxtEaveHeight, TxtStories;
        private TextBlock   TxtMeanHeight, TxtRoofNote;

        // ── Named controls — Results tab ──────────────────────────────────────
        private TextBlock   PrvVult, PrvAsceVult, PrvVasd, PrvExposure, PrvRiskCat,
                            PrvElev, PrvLat, PrvLon, PrvFloodZone, PrvBfe,
                            PrvRoofType, PrvPitch, PrvRidge, PrvEave, PrvMean,
                            PrvWindborne, PrvEnvelope, PrvCodeEdition, TxtFirmNote;

        // ── State ─────────────────────────────────────────────────────────────
        private readonly WindCalcConfig  _config;
        private readonly WindCalcEngine  _engine;
        private SiteData                 _siteData;
        private BuildingData             _buildingData;

        public WindCalcResult Result { get; private set; }

        public WindCalcWindow(WindCalcConfig config, BuildingData prefilledBuilding)
        {
            _config       = config;
            _engine       = new WindCalcEngine(config);
            _buildingData = prefilledBuilding;

            string editionLabel = config.CodeEdition == "9th" ? "FBC 2026" : "FBC 2023";
            Title                 = $"Wind Calculator \u2013 ASCE 7-22 / {editionLabel}";
            Width                 = 680;
            Height                = 660;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));

            BuildWindow();
            PopulateBuildingTab(prefilledBuilding);
            SelectComboItem(CboRiskCategory, config.DefaultRiskCategory);
            SelectComboItem(CboExposure, config.DefaultExposureCategory);
            TxtAppliedVult.Text = config.FirmMinimumVult.ToString("F0");
        }

        // ── Window construction ───────────────────────────────────────────────

        private void BuildWindow()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TabMain = new TabControl();
            TabMain.Items.Add(BuildSiteTab());
            TabMain.Items.Add(BuildBuildingTab());
            TabMain.Items.Add(BuildResultsTab());
            Grid.SetRow(TabMain, 0);
            root.Children.Add(TabMain);

            // Bottom button bar
            var btnBar = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 10, 0, 0)
            };
            var btnSettings = new Button { Content = "Settings", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            btnSettings.Click += BtnSettings_Click;
            var btnCancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            var btnApply = new Button
            {
                Name       = "BtnApply",
                Content    = "Apply to Revit",
                IsEnabled  = false,
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding    = new Thickness(14, 6, 14, 6)
            };
            btnApply.Click += BtnApply_Click;
            btnBar.Children.Add(btnSettings);
            btnBar.Children.Add(btnCancel);
            btnBar.Children.Add(btnApply);
            // Store reference
            _btnApply = btnApply;
            Grid.SetRow(btnBar, 1);
            root.Children.Add(btnBar);

            Content = root;
        }

        private Button _btnApply;

        // ── Tab 1: Site Data ──────────────────────────────────────────────────

        private TabItem BuildSiteTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };

            // Legend
            panel.Children.Add(BuildLegend());

            // Address
            panel.Children.Add(SectionHeader("Project Address"));
            var addrGrid = new Grid();
            addrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            TxtAddress = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 3, 6, 3) };
            BtnFetch = new Button
            {
                Content    = "Fetch Data \u25ba",
                Background = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding    = new Thickness(8, 4, 8, 4)
            };
            BtnFetch.Click += BtnFetch_Click;
            Grid.SetColumn(TxtAddress, 0); Grid.SetColumn(BtnFetch, 1);
            addrGrid.Children.Add(TxtAddress); addrGrid.Children.Add(BtnFetch);
            panel.Children.Add(addrGrid);

            // ── Location ──────────────────────────────────────────────────────
            panel.Children.Add(SectionHeader("Location"));
            var locGrid = MakeFieldGrid(2);
            TxtLat = EditBox(); TxtLon = EditBox();
            BrdLat = ConfWrap(TxtLat, null);
            BrdLon = ConfWrap(TxtLon, null);
            AddFieldRow(locGrid, 0, "Latitude (°N):",  BrdLat,  "SITE_LATITUDE");
            AddFieldRow(locGrid, 1, "Longitude (°W):", BrdLon,  "SITE_LONGITUDE");
            panel.Children.Add(locGrid);

            // ── Wind Speed ────────────────────────────────────────────────────
            panel.Children.Add(SectionHeader("Wind Speed — ASCE 7-22 (Vult mph)"));
            var windGrid = MakeFieldGrid(6);
            TxtVultRcI   = EditBox(); TxtVultRcII  = EditBox();
            TxtVultRcIII = EditBox(); TxtVultRcIV  = EditBox();
            TxtVasd      = EditBox(); TxtAppliedVult = EditBox();
            BrdVultRcI   = ConfWrap(TxtVultRcI,   null, "RC I — lowest risk (storage, minor occupancy)");
            BrdVultRcII  = ConfWrap(TxtVultRcII,  null, "RC II — standard residential and commercial");
            BrdVultRcIII = ConfWrap(TxtVultRcIII, null, "RC III — schools, hospitals, assembly >300");
            BrdVultRcIV  = ConfWrap(TxtVultRcIV,  null, "RC IV — essential facilities (EOC, fire stations)");
            BrdVasd      = ConfWrap(TxtVasd,       null, "Vasd = nominal ASD wind speed (RC II)");
            BrdApplied   = ConfWrap(TxtAppliedVult, null, "Applied = max(ASCE RC II, firm minimum)");
            TxtAppliedVult.TextChanged += TxtApplied_Changed;
            AddFieldRow(windGrid, 0, "Vult RC I:",          BrdVultRcI,   "—");
            AddFieldRow(windGrid, 1, "Vult RC II (ASCE):",  BrdVultRcII,  "—");
            AddFieldRow(windGrid, 2, "Vult RC III:",        BrdVultRcIII, "—");
            AddFieldRow(windGrid, 3, "Vult RC IV:",         BrdVultRcIV,  "—");
            AddFieldRow(windGrid, 4, "Vasd RC II:",         BrdVasd,      "—");
            AddFieldRow(windGrid, 5, "Applied Vult:",       BrdApplied,   "WIND_VULT_RC_II");
            panel.Children.Add(windGrid);

            // Firm min note
            panel.Children.Add(new TextBlock
            {
                Text       = $"Firm minimum: {_config.FirmMinimumVult:F0} mph — applied Vult will not go below this.",
                FontSize   = 10, Foreground = Brushes.Gray,
                Margin     = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap
            });

            // ── Site Conditions ───────────────────────────────────────────────
            panel.Children.Add(SectionHeader("Site Conditions"));
            var siteGrid = MakeFieldGrid(3);
            TxtElevation = EditBox();
            CboFloodZone = ComboField("X", "AE", "AH", "AO", "A", "VE", "V", "D");
            TxtBfe       = EditBox();
            BrdElevation = ConfWrap(TxtElevation, null, "USGS EPQS — NAVD88 datum");
            BrdFloodZone = ConfWrap(CboFloodZone,  null, "FEMA NFHL flood zone");
            BrdBfe       = ConfWrap(TxtBfe,        null, "FEMA Base Flood Elevation (ft NAVD88); N/A if Zone X");
            AddFieldRow(siteGrid, 0, "Elevation (ft):",    BrdElevation, "SITE_ELEVATION_FT");
            AddFieldRow(siteGrid, 1, "Flood Zone:",        BrdFloodZone, "FLOOD_ZONE");
            AddFieldRow(siteGrid, 2, "BFE (ft NAVD88):",  BrdBfe,       "FLOOD_BFE_FT");
            panel.Children.Add(siteGrid);

            // ── Engineering Assessment ────────────────────────────────────────
            panel.Children.Add(SectionHeader("Engineering Assessment"));
            var engGrid = MakeFieldGrid(3);
            CboRiskCategory = ComboField("I", "II", "III", "IV");
            CboExposure     = ComboField("B", "C", "D");
            ChkCoastal      = new CheckBox
            {
                Content     = "Within 1 mile of mean high-water line",
                FontSize    = 12,
                Margin      = new Thickness(0, 4, 0, 4),
                ToolTip     = "ASCE 7-22 §26.11.1 — windborne debris region threshold is 130 mph (vs 140 mph general)"
            };
            ChkCoastal.Checked   += ChkCoastal_Changed;
            ChkCoastal.Unchecked += ChkCoastal_Changed;
            BrdExposure = ConfWrap(CboExposure, false,
                "Exposure Category requires engineer judgment — MRLC/NLCD auto-detect is unreliable.");
            AddFieldRow(engGrid, 0, "Risk Category:",      ConfWrap(CboRiskCategory, true, "Per ASCE 7-22 Table 1.5-1"),  "WIND_RISK_CATEGORY");
            AddFieldRow(engGrid, 1, "Exposure Category:",  BrdExposure,  "WIND_EXPOSURE_CATEGORY");
            Grid.SetRow(ChkCoastal, 2); Grid.SetColumnSpan(ChkCoastal, 3);
            engGrid.Children.Add(ChkCoastal);
            panel.Children.Add(engGrid);

            // Computed flags
            var flagPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 4), Orientation = Orientation.Horizontal };
            TxtWindborneStatus  = FlagLabel("WINDBORNE DEBRIS: —",   Brushes.Gray);
            TxtEnvelope160Status = FlagLabel("160 MPH ENVELOPE: —", Brushes.Gray);
            flagPanel.Children.Add(TxtWindborneStatus);
            flagPanel.Children.Add(new TextBlock { Text = "   ", FontSize = 12 });
            flagPanel.Children.Add(TxtEnvelope160Status);
            panel.Children.Add(flagPanel);

            // ── Parcel Data ───────────────────────────────────────────────────
            panel.Children.Add(SectionHeader("Parcel Data"));
            var parcelGrid = MakeFieldGrid(3);
            TxtParcelId   = EditBox(); TxtYearBuilt = EditBox(); TxtConstClass = EditBox();
            BrdParcelId   = ConfWrap(TxtParcelId,   null, "County parcel ID");
            BrdYearBuilt  = ConfWrap(TxtYearBuilt,  null, "Year built — from county parcel service");
            BrdConstClass = ConfWrap(TxtConstClass,  null, "Construction class — available from Pinellas EGIS only; other counties require manual entry");
            AddFieldRow(parcelGrid, 0, "Parcel ID:",           BrdParcelId,   "—");
            AddFieldRow(parcelGrid, 1, "Year Built:",          BrdYearBuilt,  "—");
            AddFieldRow(parcelGrid, 2, "Construction Class:",  BrdConstClass, "—");
            panel.Children.Add(parcelGrid);

            // Status
            TxtStatus = new TextBlock
            {
                FontSize     = 11,
                Foreground   = new SolidColorBrush(Color.FromRgb(0x54, 0x6E, 0x7A)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 6, 0, 0)
            };
            panel.Children.Add(TxtStatus);

            var sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return new TabItem { Header = "Site Data", Content = sv };
        }

        // ── Tab 2: Building Data ──────────────────────────────────────────────

        private TabItem BuildBuildingTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(SectionHeader("Roof Geometry"));

            TxtRoofNote = new TextBlock
            {
                FontSize     = 10,
                Foreground   = new SolidColorBrush(Color.FromRgb(0x54, 0x6E, 0x7A)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6)
            };
            panel.Children.Add(TxtRoofNote);

            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 6; i++) bg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            CboRoofType    = ComboField("Hip", "Gable", "Flat", "Mansard", "Shed", "Gambrel", "Unknown");
            TxtRoofPitch   = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 3, 0, 3) };
            TxtRidgeHeight = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 3, 0, 3) };
            TxtRidgeHeight.TextChanged += RoofHeight_Changed;
            TxtEaveHeight  = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 3, 0, 3) };
            TxtEaveHeight.TextChanged += RoofHeight_Changed;
            TxtMeanHeight  = new TextBlock
            {
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
                Margin            = new Thickness(0, 3, 0, 3)
            };
            TxtStories = new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 3, 0, 3), Text = "1" };

            AddGridRow(bg, 0, "Roof Type:",            CboRoofType);
            AddGridRow(bg, 1, "Roof Pitch:",           TxtRoofPitch);
            AddGridRow(bg, 2, "Ridge Height (ft):",    TxtRidgeHeight);
            AddGridRow(bg, 3, "Eave Height (ft):",     TxtEaveHeight);
            AddGridRow(bg, 4, "Mean Roof Height (ft):", TxtMeanHeight);
            AddGridRow(bg, 5, "Number of Stories:",    TxtStories);
            panel.Children.Add(bg);

            var sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return new TabItem { Header = "Building Data", Content = sv };
        }

        // ── Tab 3: Results Preview ────────────────────────────────────────────

        private TabItem BuildResultsTab()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(SectionHeader("Values to be Written to Revit"));
            panel.Children.Add(new TextBlock
            {
                Text         = "Click \"Apply to Revit\" below to write shared parameters, update Project Information, and create the Design Data drafting view.",
                FontSize     = 10, Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });

            var resultBorder = new Border
            {
                Background      = Brushes.White,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0xCF, 0xD8, 0xDC)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(10)
            };
            var rg = new Grid();
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 18; i++) rg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            PrvVult        = PrvValue(); PrvAsceVult  = PrvValue(); PrvVasd       = PrvValue();
            PrvExposure    = PrvValue(); PrvRiskCat   = PrvValue(); PrvElev       = PrvValue();
            PrvLat         = PrvValue(); PrvLon       = PrvValue(); PrvFloodZone  = PrvValue();
            PrvBfe         = PrvValue(); PrvRoofType  = PrvValue(); PrvPitch      = PrvValue();
            PrvRidge       = PrvValue(); PrvEave      = PrvValue(); PrvMean       = PrvValue();
            PrvWindborne   = PrvValue(); PrvEnvelope  = PrvValue(); PrvCodeEdition = PrvValue();

            AddResultRow(rg,  0, "WIND_VULT_RC_II (mph)",       PrvVult,        bold: true);
            AddResultRow(rg,  1, "WIND_ASCE_COMPUTED (mph)",     PrvAsceVult,    bold: true);
            AddResultRow(rg,  2, "WIND_VASD_RC_II (mph)",        PrvVasd);
            AddResultRow(rg,  3, "WIND_EXPOSURE_CATEGORY",       PrvExposure);
            AddResultRow(rg,  4, "WIND_RISK_CATEGORY",           PrvRiskCat);
            AddResultRow(rg,  5, "SITE_ELEVATION_FT",            PrvElev);
            AddResultRow(rg,  6, "SITE_LATITUDE",                PrvLat);
            AddResultRow(rg,  7, "SITE_LONGITUDE",               PrvLon);
            AddResultRow(rg,  8, "FLOOD_ZONE",                   PrvFloodZone);
            AddResultRow(rg,  9, "FLOOD_BFE_FT",                 PrvBfe);
            AddResultRow(rg, 10, "ROOF_TYPE",                    PrvRoofType);
            AddResultRow(rg, 11, "ROOF_PITCH",                   PrvPitch);
            AddResultRow(rg, 12, "ROOF_RIDGE_HEIGHT_FT",         PrvRidge);
            AddResultRow(rg, 13, "ROOF_EAVE_HEIGHT_FT",          PrvEave);
            AddResultRow(rg, 14, "ROOF_MEAN_HEIGHT_FT",          PrvMean);
            AddResultRow(rg, 15, "WIND_WINDBORNE_DEBRIS",        PrvWindborne,   bold: true);
            AddResultRow(rg, 16, "WIND_160MPH_ENVELOPE",         PrvEnvelope,    bold: true);
            AddResultRow(rg, 17, "CODE_EDITION",                 PrvCodeEdition);

            resultBorder.Child = rg;
            panel.Children.Add(resultBorder);

            TxtFirmNote = new TextBlock
            {
                FontSize     = 10,
                Foreground   = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 6, 0, 0)
            };
            panel.Children.Add(TxtFirmNote);

            var sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return new TabItem { Header = "Results Preview", Content = sv };
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            string address = TxtAddress.Text.Trim();
            if (string.IsNullOrWhiteSpace(address))
            {
                TxtStatus.Text = "Please enter a project address.";
                return;
            }

            BtnFetch.IsEnabled    = false;
            _btnApply.IsEnabled   = false;
            TxtStatus.Text        = "Fetching site data...";

            string riskCat = ComboValue(CboRiskCategory) ?? "II";

            try
            {
                _siteData = await _engine.FetchSiteDataAsync(address, riskCat,
                    msg => Dispatcher.Invoke(() => TxtStatus.Text = msg));

                PopulateSiteFields(_siteData);
                UpdateResultsPreview();
                _btnApply.IsEnabled   = _siteData.GeocodingSuccess;
                TabMain.SelectedIndex = 2;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                BtnFetch.IsEnabled = true;
            }
        }

        private void PopulateSiteFields(SiteData s)
        {
            // Location
            TxtLat.Text = s.GeocodingSuccess ? $"{s.Latitude:F6}" : "";
            TxtLon.Text = s.GeocodingSuccess ? $"{s.Longitude:F6}" : "";
            SetConf(BrdLat, s.GeocodingSuccess); SetConf(BrdLon, s.GeocodingSuccess);

            // Wind speeds
            if (s.WindSpeedSuccess)
            {
                TxtVultRcI.Text   = s.AsceVultRcI   > 0 ? $"{s.AsceVultRcI:F0}"   : "";
                TxtVultRcII.Text  = s.AsceVultRcII  > 0 ? $"{s.AsceVultRcII:F0}"  : "";
                TxtVultRcIII.Text = s.AsceVultRcIII > 0 ? $"{s.AsceVultRcIII:F0}" : "";
                TxtVultRcIV.Text  = s.AsceVultRcIV  > 0 ? $"{s.AsceVultRcIV:F0}"  : "";
                TxtVasd.Text      = s.AsceVasdRcII  > 0 ? $"{s.AsceVasdRcII:F0}"  : "";
                double applied    = Math.Max(s.AsceVultRcII, _config.FirmMinimumVult);
                TxtAppliedVult.Text = $"{applied:F0}";
                SetConf(BrdVultRcI, true); SetConf(BrdVultRcII, true);
                SetConf(BrdVultRcIII, true); SetConf(BrdVultRcIV, true);
                SetConf(BrdVasd, true); SetConf(BrdApplied, true);
                if (!string.IsNullOrWhiteSpace(s.WindSpeedSource))
                    TxtStatus.Text = $"Wind speeds from: {s.WindSpeedSource}.";
            }
            else
            {
                // API failed — leave ASCE fields blank, applied = firm minimum
                TxtVultRcI.Text = TxtVultRcII.Text = TxtVultRcIII.Text = TxtVultRcIV.Text = TxtVasd.Text = "";
                TxtAppliedVult.Text = _config.FirmMinimumVult.ToString("F0");
                SetConf(BrdVultRcI, false); SetConf(BrdVultRcII, false);
                SetConf(BrdVultRcIII, false); SetConf(BrdVultRcIV, false);
                SetConf(BrdVasd, false); SetConf(BrdApplied, false);

                string apiNote;
                if (s.WindSpeedError == "ASCE_KEY_MISSING" || s.AsceApiKey_Missing)
                    apiNote = " Run Setup Data to download local wind speed shapefiles (no API key needed).";
                else
                    apiNote = string.IsNullOrWhiteSpace(s.WindSpeedError)
                        ? " Run Setup Data to download local wind speed shapefiles."
                        : $" ({s.WindSpeedError})";
                TxtStatus.Text = "Wind speeds not found — enter manually or run Setup Data first." + apiNote;
            }

            // Elevation
            TxtElevation.Text = s.ElevationSuccess ? $"{s.ElevationFt:F1}" : "";
            SetConf(BrdElevation, s.ElevationSuccess);

            // Flood zone
            SelectComboItem(CboFloodZone, s.FloodZone);
            SetConf(BrdFloodZone, s.FloodZoneSuccess);
            bool bfeReal = s.FloodZoneSuccess && !double.IsNaN(s.FloodBfeFt);
            TxtBfe.Text = bfeReal ? $"{s.FloodBfeFt:F1}" : "N/A";
            SetConf(BrdBfe, bfeReal);

            // Exposure — always amber (engineering judgment required)
            SelectComboItem(CboExposure, s.ExposureCategory);
            SetConf(BrdExposure, false);

            // Parcel
            bool p = s.ParcelSuccess;
            TxtParcelId.Text   = s.ParcelId;
            TxtYearBuilt.Text  = s.YearBuilt > 0 ? s.YearBuilt.ToString() : "";
            TxtConstClass.Text = s.ConstructionClass;
            SetConf(BrdParcelId,   p); SetConf(BrdYearBuilt, p); SetConf(BrdConstClass, p);

            // Derived flags (will refresh when applied vult changes)
            RefreshDerivedFlags();

            if (s.GeocodingSuccess && TxtStatus.Text == "Fetching site data..." || TxtStatus.Text == "Done.")
                TxtStatus.Text = s.ParcelSuccess
                    ? $"Data fetched. Parcel: {s.ParcelSource}"
                    : "Data fetched. Parcel data not available for this area — enter manually.";
        }

        private void TxtApplied_Changed(object sender, TextChangedEventArgs e) => RefreshDerivedFlags();
        private void ChkCoastal_Changed(object sender, RoutedEventArgs e) => RefreshDerivedFlags();

        private void RefreshDerivedFlags()
        {
            bool coastal = ChkCoastal.IsChecked == true;
            if (!double.TryParse(TxtAppliedVult.Text, out double vult)) vult = _config.FirmMinimumVult;

            bool windborne = vult >= 140.0 || (coastal && vult >= 130.0);
            bool env160    = vult >= 130.0;

            TxtWindborneStatus.Text       = $"WINDBORNE DEBRIS: {(windborne ? "YES" : "NO")}";
            TxtWindborneStatus.Foreground = windborne
                ? new SolidColorBrush(Color.FromRgb(183, 28, 28))
                : Brushes.Gray;

            TxtEnvelope160Status.Text       = $"160 MPH ENVELOPE (FBC 9th): {(env160 ? "YES" : "NO")}";
            TxtEnvelope160Status.Foreground = env160
                ? new SolidColorBrush(Color.FromRgb(230, 81, 0))
                : Brushes.Gray;
        }

        private void PopulateBuildingTab(BuildingData b)
        {
            SelectComboItem(CboRoofType, b.RoofType);
            TxtRoofPitch.Text   = b.RoofPitch;
            TxtRidgeHeight.Text = b.RidgeHeightFt.ToString("F2");
            TxtEaveHeight.Text  = b.EaveHeightFt.ToString("F2");
            TxtStories.Text     = b.Stories.ToString();
            TxtMeanHeight.Text  = b.MeanRoofHeightFt.ToString("F2") + " ft";
            TxtRoofNote.Text    = b.DetectionNote;
        }

        private void RoofHeight_Changed(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(TxtRidgeHeight.Text, out double ridge) &&
                double.TryParse(TxtEaveHeight.Text,  out double eave))
                TxtMeanHeight.Text = $"{(ridge + eave) / 2.0:F2} ft";
        }

        private void UpdateResultsPreview()
        {
            if (_siteData == null) return;

            double applied = double.TryParse(TxtAppliedVult.Text, out double ov) ? ov : _config.FirmMinimumVult;
            bool coastal   = ChkCoastal.IsChecked == true;

            PrvVult.Text       = $"{applied:F0}";
            PrvAsceVult.Text   = _siteData.WindSpeedSuccess ? $"{_siteData.AsceVultRcII:F0}" : "N/A";
            PrvVasd.Text       = TxtVasd.Text.Length > 0 ? TxtVasd.Text : "N/A";
            PrvExposure.Text   = ComboValue(CboExposure) ?? _siteData.ExposureCategory;
            PrvRiskCat.Text    = ComboValue(CboRiskCategory) ?? _siteData.RiskCategory;
            PrvElev.Text       = TxtElevation.Text.Length > 0 ? TxtElevation.Text : "N/A";
            PrvLat.Text        = TxtLat.Text;
            PrvLon.Text        = TxtLon.Text;
            PrvFloodZone.Text  = ComboValue(CboFloodZone) ?? _siteData.FloodZone;
            PrvBfe.Text        = TxtBfe.Text;
            PrvRoofType.Text   = ComboValue(CboRoofType) ?? "";
            PrvPitch.Text      = TxtRoofPitch.Text;
            PrvRidge.Text      = TxtRidgeHeight.Text;
            PrvEave.Text       = TxtEaveHeight.Text;
            PrvMean.Text       = TxtMeanHeight.Text;

            bool windborne = applied >= 140.0 || (coastal && applied >= 130.0);
            bool env160    = applied >= 130.0;
            PrvWindborne.Text   = windborne  ? "YES" : "NO";
            PrvEnvelope.Text    = env160     ? "YES" : "NO";
            PrvCodeEdition.Text = _config.CodeEdition == "9th"
                ? "FBC 9th Edition 2026 / ASCE 7-22"
                : "FBC 8th Edition 2023 / ASCE 7-22";

            TxtFirmNote.Text = _siteData.FirmMinOverrideActive
                ? $"Note: ASCE 7-22 computed {_siteData.AsceVultRcII:F0} mph; firm minimum {_config.FirmMinimumVult:F0} mph applied per company standard."
                : "";
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_siteData == null) return;

            double applied = double.TryParse(TxtAppliedVult.Text, out double ov)
                ? Math.Max(ov, _config.FirmMinimumVult)
                : _config.FirmMinimumVult;

            bool coastal = ChkCoastal.IsChecked == true;

            // Overwrite siteData with user-edited values before building result
            _siteData.AppliedVult          = applied;
            _siteData.ExposureCategory     = ComboValue(CboExposure) ?? _siteData.ExposureCategory;
            _siteData.RiskCategory         = ComboValue(CboRiskCategory) ?? _siteData.RiskCategory;
            _siteData.FirmMinOverrideActive = applied > _siteData.AsceVultRcII;
            _siteData.CoastalProximity      = coastal;

            if (double.TryParse(TxtElevation.Text, out double elev)) _siteData.ElevationFt = elev;
            if (double.TryParse(TxtLat.Text, out double lat))         _siteData.Latitude    = lat;
            if (double.TryParse(TxtLon.Text, out double lon))         _siteData.Longitude   = lon;
            _siteData.FloodZone = ComboValue(CboFloodZone) ?? _siteData.FloodZone;
            if (double.TryParse(TxtBfe.Text, out double bfe))         _siteData.FloodBfeFt  = bfe;
            else _siteData.FloodBfeFt = double.NaN;

            _siteData.YearBuilt         = int.TryParse(TxtYearBuilt.Text, out int yb) ? yb : 0;
            _siteData.ConstructionClass = TxtConstClass.Text;
            _siteData.ParcelId          = TxtParcelId.Text;

            // Recompute derived flags with final values
            WindCalcEngine.RecomputeDerivedFlags(_siteData);

            double.TryParse(TxtRidgeHeight.Text, out double ridge);
            double.TryParse(TxtEaveHeight.Text,  out double eave);

            var building = new BuildingData
            {
                RoofType      = ComboValue(CboRoofType) ?? "",
                RoofPitch     = TxtRoofPitch.Text,
                Stories       = int.TryParse(TxtStories.Text, out int st) ? st : 1,
                AutoDetected  = _buildingData.AutoDetected,
                RidgeHeightFt = ridge,
                EaveHeightFt  = eave
            };

            Result = new WindCalcResult
            {
                Site        = _siteData,
                Building    = building,
                Success     = true,
                CodeEdition = _config.CodeEdition
            };
            DialogResult = true;
            Close();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_config);
            if (dlg.ShowDialog() == true)
            {
                string editionLabel = _config.CodeEdition == "9th" ? "FBC 2026" : "FBC 2023";
                Title          = $"Wind Calculator \u2013 ASCE 7-22 / {editionLabel}";
                TxtStatus.Text = "Settings saved. Re-fetch data to use updated API key.";
            }
        }

        // ── UI helper factories ───────────────────────────────────────────────

        private static StackPanel BuildLegend()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            sp.Children.Add(ConfDot(_confirmed));
            sp.Children.Add(new TextBlock { Text = " API-confirmed   ", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            sp.Children.Add(ConfDot(_estimated));
            sp.Children.Add(new TextBlock { Text = " Estimated / needs review   ", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            sp.Children.Add(ConfDot(_manual));
            sp.Children.Add(new TextBlock { Text = " Engineering judgment", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            return sp;
        }

        private static Ellipse ConfDot(SolidColorBrush color) => new Ellipse
        {
            Width = 10, Height = 10, Fill = color,
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>
        /// Wraps a control in a border with a 3 px left indicator strip.
        /// confirmed=true → green, false → amber, null → gray (unknown/pre-fetch).
        /// </summary>
        private static Border ConfWrap(UIElement ctrl, bool? confirmed, string tooltip = null)
        {
            var b = new Border
            {
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush     = ConfBrush(confirmed),
                Margin          = new Thickness(0, 2, 0, 2),
                Child           = ctrl
            };
            if (tooltip != null) b.ToolTip = tooltip;
            return b;
        }

        private static SolidColorBrush ConfBrush(bool? confirmed) =>
            confirmed == true  ? _confirmed :
            confirmed == false ? _estimated :
            new SolidColorBrush(Color.FromRgb(200, 200, 200));

        private static void SetConf(Border brd, bool confirmed) =>
            brd.BorderBrush = ConfBrush(confirmed);

        private static TextBox EditBox() =>
            new TextBox { Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 1, 0, 1), FontSize = 12 };

        private static TextBlock FlagLabel(string text, Brush fg) => new TextBlock
        {
            Text       = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = fg,   Margin   = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>
        /// Creates a 3-column Grid: label(col0), indicator+field(col1 spans 2), param name (col2).
        /// Returns the Grid ready for AddFieldRow.
        /// </summary>
        private static Grid MakeFieldGrid(int rows)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            for (int i = 0; i < rows; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            return g;
        }

        private static void AddFieldRow(Grid g, int row, string label, UIElement field, string paramName)
        {
            var lbl = new TextBlock
            {
                Text = label, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 8, 3)
            };
            var pnm = new TextBlock
            {
                Text = paramName, FontSize = 10,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetRow(lbl,   row); Grid.SetColumn(lbl,   0);
            Grid.SetRow(field, row); Grid.SetColumn(field, 1);
            Grid.SetRow(pnm,   row); Grid.SetColumn(pnm,   2);
            g.Children.Add(lbl); g.Children.Add(field); g.Children.Add(pnm);
        }

        private static TextBlock SectionHeader(string text) => new TextBlock
        {
            Text       = text,
            FontWeight = FontWeights.SemiBold,
            FontSize   = 12,
            Margin     = new Thickness(0, 10, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F))
        };

        private static TextBlock PrvValue() => new TextBlock
        {
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 2, 0, 2)
        };

        private static ComboBox ComboField(params string[] items)
        {
            var cbo = new ComboBox { FontSize = 12, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 1, 0, 1) };
            foreach (var item in items) cbo.Items.Add(new ComboBoxItem { Content = item });
            if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
            return cbo;
        }

        private static void AddGridRow(Grid g, int row, string label, UIElement ctrl)
        {
            var lbl = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 0) };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(ctrl, row); Grid.SetColumn(ctrl, 1);
            g.Children.Add(lbl); g.Children.Add(ctrl);
        }

        private static void AddResultRow(Grid g, int row, string paramName, TextBlock value, bool bold = false)
        {
            var lbl = new TextBlock
            {
                Text              = paramName,
                FontSize          = 12,
                FontWeight        = bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 4, 8, 0)
            };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            g.Children.Add(lbl); g.Children.Add(value);
        }

        private static string ComboValue(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Content?.ToString();

        private static void SelectComboItem(ComboBox combo, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }
    }
}
