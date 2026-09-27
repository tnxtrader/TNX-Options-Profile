// =============================================================================
//  TNX OPTIONS PROFILE - DeepCharts-style option profile, docked LEFT
// -----------------------------------------------------------------------------
//  LAYOUT (modelled on the DeepCharts Option Profile / Option GEX Profile)
//    A DEDICATED PANEL is reserved down the left edge with its own opaque
//    background and a divider line, so the profile reads as a column of its own
//    rather than as clutter floating over the candles. Inside the panel the bars
//    split from a CENTRE LINE: call side one way, put side the other. A caption
//    ("GEX", "C/P Sigma", ...) sits at the bottom of the panel and a running
//    summary (net / call total / put total) at the top.
//
//    Quantower cannot physically shrink the candle area from an indicator, so the
//    panel is painted opaque over that strip - visually identical to a reserved
//    column, and the width is yours to set.
//
//  DATA SOURCES  (DeepCharts parity)
//    GEX (call - put) . gamma-weighted, dealer-signed
//    GEX split ....... call GEX and put GEX side by side
//    C/P Sigma ....... raw traded contracts, no gamma weighting
//    C Sigma - P Sigma net traded contracts
//    C/P OI .......... open interest, split
//    C OI - P OI ..... net open interest
//    DEX ............. delta exposure, $ per point
//
//  PEAK LINES  the largest call-side and put-side strikes are projected across
//    the chart as dashed levels - the "quick reference without reading the whole
//    profile" behaviour from DeepCharts.
//
//  ROLLING VALUES  the profile shape as it stood M1 / M5 / M15 / M30 ago, drawn
//    as step outlines, so the shift in positioning during the session is visible.
//
//  Everything numeric lives in OptionsCore.cs. This file is layout and paint.
//  GDI objects are cached and reused; nothing is allocated per bar.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;
using TradingPlatform.BusinessLayer.Native;

namespace TNXIndicators
{
    public class TNXOptionsProfile : Indicator
    {
        public enum Dock { Left, Right }
        public enum Fill { Solid, Hollow, Gradient }
        public enum BarScale { Linear, Sqrt, Log }
        public enum Corner { TopRight, TopLeft, BottomRight, BottomLeft, Hidden }

        // ============================================================ GENERAL
        [InputParameter("Data source", 0, 0, 0, 0, 0, new object[]
        {
            "GEX  (call - put)",      ProfileSource.GexCallMinusPut,
            "GEX  split call / put",  ProfileSource.GexSplit,
            "C/P Sigma  (volume)",    ProfileSource.CallPutVolume,
            "C Sigma - P Sigma",      ProfileSource.CallMinusPutVolume,
            "C/P open interest",      ProfileSource.CallPutOI,
            "C OI - P OI",            ProfileSource.CallMinusPutOI,
            "DEX  (delta exposure)",  ProfileSource.DexCallMinusPut
        })]
        public ProfileSource Source = ProfileSource.GexCallMinusPut;

        [InputParameter("Expiration filter", 1, 0, 0, 0, 0, new object[]
        {
            "0 DTE", ExpFilter.ZeroDTE, "<= 1 day", ExpFilter.Max1D,
            "<= 7 days", ExpFilter.Max7D, "<= 31 days", ExpFilter.Max31D,
            "<= 91 days", ExpFilter.Max91D, "All expirations", ExpFilter.All
        })]
        public ExpFilter Filter = ExpFilter.ZeroDTE;

        [InputParameter("Single expiration index (0 = use filter)", 2, 0, 30, 1, 0)]
        public int ExpirationIndex = 0;

        [InputParameter("Max series to aggregate", 3, 1, 12, 1, 0)]
        public int MaxSeries = 1;

        [InputParameter("Strikes per side", 4, 1, 60, 1, 0)]
        public int StrikesPerSide = 12;

        [InputParameter("Gamma weighting uses", 5, 0, 0, 0, 0, new object[]
        {
            "Open interest (positioning)", GexSource.OpenInterest,
            "Day volume (0DTE / live)",    GexSource.DayVolume
        })]
        public GexSource Weight = GexSource.OpenInterest;

        [InputParameter("Dealer inventory assumption", 6, 0, 0, 0, 0, new object[]
        {
            "Long calls / short puts (standard)", DealerModel.LongCallShortPut,
            "Short calls / short puts",           DealerModel.ShortCallShortPut,
            "Absolute gamma (unsigned)",          DealerModel.AbsoluteGamma
        })]
        public DealerModel Dealer = DealerModel.LongCallShortPut;

        [InputParameter("Risk-free rate %", 7, 0.0, 15.0, 0.25, 2)] public double RatePct = 5.0;
        [InputParameter("Refresh seconds", 8, 2, 300, 1, 0)] public int RefreshSeconds = 15;

        // ============================================================== PANEL
        [InputParameter("Dock side", 10, 0, 0, 0, 0, new object[]
        {
            "Left", Dock.Left, "Right", Dock.Right
        })]
        public Dock Side = Dock.Right;

        [InputParameter("Panel width, % of chart", 11, 3, 60, 1, 0)] public int WidthPct = 12;
        [InputParameter("Panel width px  (0 = use %, or drag the left edge)", 111, 0, 2000, 5, 0)]
        public int PanelWidthPx = 0;
        [InputParameter("Bar length, % of panel", 17, 20, 100, 5, 0)] public int BarLenPct = 88;
        [InputParameter("Min bar pitch (px) - merges strikes when zoomed out", 18, 1, 20, 1, 0)]
        public int MinPitch = 3;
        [InputParameter("Gap between bars (px)", 19, 0, 4, 1, 0)] public int BarGap = 1;
        [InputParameter("Panel background opacity (0 = follow the chart)", 12, 0, 255, 5, 0)] public int PanelAlpha = 0;
        [InputParameter("Panel background: follow the theme", 120)] public bool AutoBackground = true;
        [InputParameter("Panel background colour (when not auto)", 121)] public Color PanelColour = Color.Black;
        [InputParameter("Panel offset px  (- = toward price scale)", 122, -1200, 1200, 5, 0)]
        public int PanelOffset = 0;
        [InputParameter("Show panel divider", 13)] public bool ShowDivider = false;
        [InputParameter("Show centre line", 14)] public bool ShowCentre = true;
        [InputParameter("Caption under panel", 15)] public bool ShowCaption = true;
        [InputParameter("Summary totals on panel", 16)] public bool ShowSummary = true;

        // ------------------------------------------------------- strike ruler
        [InputParameter("Strike ruler (own scale)", 190)] public bool ShowRuler = true;
        [InputParameter("Strike ruler width (px)", 191, 24, 90, 2, 0)] public int RulerWidth = 46;
        [InputParameter("Strike ruler: axis line", 192)] public bool RulerLine = true;

        // =============================================================== BARS
        [InputParameter("Bar style", 20, 0, 0, 0, 0, new object[]
        {
            "Solid", Fill.Solid, "Hollow", Fill.Hollow, "Gradient", Fill.Gradient
        })]
        public Fill BarFill = Fill.Solid;

        [InputParameter("Bar opacity %", 21, 10, 100, 5, 0)] public int BarOpacity = 100;
        [InputParameter("Border width", 22, 0.0, 3.0, 0.5, 1)] public double BorderWidth = 1.0;
        [InputParameter("Strike tick height %", 23, 20, 100, 5, 0)] public int TickHeightPct = 88;
        [InputParameter("Value labels inside bars", 24)] public bool ValueLabels = true;
        [InputParameter("Label text size", 25, 6, 16, 1, 0)] public int LabelSize = 11;
        [InputParameter("Highlight disputed strikes (both sides active)", 26)] public bool Disputed = false;

        [InputParameter("Bar length scale", 27, 0, 0, 0, 0, new object[]
        {
            "Linear (true proportion)", BarScale.Linear,
            "Square root (lifts small strikes)", BarScale.Sqrt,
            "Log (strongest compression)", BarScale.Log
        })]
        public BarScale Scale = BarScale.Sqrt;

        // ============================================================== PEAKS
        [InputParameter("Call peak: enable", 30)] public bool CallPeak = true;
        [InputParameter("Call peak: highlight bar", 31)] public bool CallPeakBar = true;
        [InputParameter("Call peak: horizontal line", 32)] public bool CallPeakLine = true;
        [InputParameter("Put peak: enable", 33)] public bool PutPeak = true;
        [InputParameter("Put peak: highlight bar", 34)] public bool PutPeakBar = true;
        [InputParameter("Put peak: horizontal line", 35)] public bool PutPeakLine = true;
        [InputParameter("Peak line style", 36, 0, 0, 0, 0, new object[]
        {
            "Dash", PeakLineStyle.Dash, "Solid", PeakLineStyle.Solid, "Dot", PeakLineStyle.Dot,
            "Dash dot", PeakLineStyle.DashDot, "Dash dot dot", PeakLineStyle.DashDotDot
        })]
        public PeakLineStyle PeakStyle = PeakLineStyle.Dash;
        [InputParameter("Peak line width", 37, 1, 5, 1, 0)] public int PeakWidth = 1;

        // =========================================================== OVERLAYS
        [InputParameter("Zero gamma line", 40)] public bool ShowZeroGamma = true;
        [InputParameter("Total gamma curve in panel", 41)] public bool ShowCurve = true;
        [InputParameter("Cumulative curve in panel", 42)] public bool ShowCumulative = false;
        [InputParameter("Max pain line", 43)] public bool ShowMaxPain = true;
        [InputParameter("Expected move bands", 44)] public bool ShowExpMove = true;
        [InputParameter("Expected move: second sigma", 45, 1.0, 4.0, 0.25, 2)] public double Sigma2 = 2.0;
        [InputParameter("Level labels", 46)] public bool LevelLabels = true;
        [InputParameter("Expected move line width", 47, 1, 4, 1, 0)] public int ExpMoveWidth = 1;

        // ==================================================== ROLLING VALUES
        [InputParameter("Rolling M1 outline", 50)] public bool RollM1 = false;
        [InputParameter("Rolling M5 outline", 51)] public bool RollM5 = false;
        [InputParameter("Rolling M15 outline", 52)] public bool RollM15 = false;
        [InputParameter("Rolling M30 outline", 53)] public bool RollM30 = false;
        [InputParameter("Rolling line width", 54, 1, 4, 1, 0)] public int RollWidth = 1;

        // ============================================================= VISUAL
        [InputParameter("Theme", 60, 0, 0, 0, 0, new object[]
        {
            "DeepCharts", OptTheme.DeepCharts,
            "DeepCharts heat", OptTheme.DeepChartsHeat,
            "Pro dark", OptTheme.ProDark,
            "Neon", OptTheme.Neon,
            "Classic", OptTheme.Classic,
            "Ocean", OptTheme.Ocean,
            "Monochrome", OptTheme.Monochrome,
            "Ember", OptTheme.Ember,
            "Ice", OptTheme.Ice,
            "Aurora", OptTheme.Aurora,
            "Terminal", OptTheme.Terminal,
            "Sunset", OptTheme.Sunset,
            "High contrast", OptTheme.HighContrast,
            "ATAS X", OptTheme.AtasX,
            "Bookmap", OptTheme.Bookmap,
            "TradingView", OptTheme.TradingView,
            "Volcano", OptTheme.Volcano,
            "Matrix", OptTheme.Matrix,
            "Nord", OptTheme.Nord,
            "Dracula", OptTheme.Dracula,
            "Solarized", OptTheme.Solarized,
            "Cyberpunk", OptTheme.Cyberpunk,
            "Gold", OptTheme.Gold,
            "Arctic", OptTheme.Arctic,
            "Sakura", OptTheme.Sakura,
            "Forest", OptTheme.Forest,
            "Midnight", OptTheme.Midnight,
            "Copper", OptTheme.Copper,
            "Custom", OptTheme.Custom
        })]
        public OptTheme Theme = OptTheme.DeepCharts;

        [InputParameter("Custom: call colour", 61)] public Color CustomCall = Color.FromArgb(34, 197, 94);
        [InputParameter("Custom: put colour", 62)] public Color CustomPut = Color.FromArgb(139, 92, 246);
        [InputParameter("Custom: panel background", 63)] public Color CustomPanel = Color.FromArgb(13, 13, 15);

        [InputParameter("Info table", 64, 0, 0, 0, 0, new object[]
        {
            "Top right", Corner.TopRight, "Top left", Corner.TopLeft,
            "Bottom right", Corner.BottomRight, "Bottom left", Corner.BottomLeft,
            "Hidden", Corner.Hidden
        })]
        public Corner Table = Corner.Hidden;

        [InputParameter("Keep title area clear (px)", 66, 0, 800, 10, 0)] public int TitleAreaWidth = 190;


        [InputParameter("Data mode", 9, 0, 0, 0, 0, new object[]
        {
            "Daily bars only (safe - no feed subscription)", DataMode.HistoryOnly,
            "Live option quotes (NEEDS an options data plan)", DataMode.LiveQuotes
        })]
        public DataMode Mode2 = DataMode.LiveQuotes;

        [InputParameter("Daily-bar cache minutes", 90, 1, 240, 1, 0)] public int HistCacheMin = 20;
        [InputParameter("Max history requests per refresh", 91, 10, 400, 10, 0)] public int MaxHistReq = 90;

        [InputParameter("MAX live strikes (0 = no cap)", 92, 0, 400, 1, 0)]
        public int MaxLiveStrikes = 0;


        // ============================================================== state
        readonly OptionsEngine _eng = new OptionsEngine();

        // Drag the panel's LEFT EDGE to widen it into the chart or shrink it right.
        float _pwPx;                 // 0 = use the setting
        float _panelL = float.NaN, _panelR = float.NaN;
        bool _edgeDrag; int _dragX; float _dragW0;
        float _grabX = float.NaN, _mouseX = float.NaN;
        IChart _mouseChart;
        int _winNum = -1;

        sealed class Shot { public DateTime T; public double[] K; public double[] A; public double[] B; }
        readonly List<Shot> _roll = new List<Shot>();
        readonly object _sync = new object();
        DateTime _lastShot = DateTime.MinValue;

        // cached GDI - keyed by argb so themes/opacity changes rebuild lazily
        readonly Dictionary<int, SolidBrush> _brush = new Dictionary<int, SolidBrush>();
        readonly Dictionary<long, Pen> _pen = new Dictionary<long, Pen>();
        Font _f, _fB, _fS, _fLbl, _fLblB;
        int _lblSize = -1;
        bool _started;

        public TNXOptionsProfile() : base()
        {
            this.Name = "TNX Options Profile";
            this.Description = "DeepCharts-style option profile docked to the chart edge: GEX / DEX / volume / OI " +
                               "split from a centre line, peak lines, rolling outlines, zero gamma, max pain, expected move.";
            this.SeparateWindow = false;
        }

        // ------------------------------------------------------------- setup
        protected override void OnInit()
        {
            try
            {
                Push();
                lock (_sync) { _roll.Clear(); _lastShot = DateTime.MinValue; }
                _eng.Start(this.Symbol);
                _started = true;
            }
            catch { }
        }

        protected override void OnSettingsUpdated() { try { Push(); _eng.Invalidate(); } catch { } }

        protected override void OnClear()
        {
            try { _eng.Stop(); _started = false; } catch { }
            UnhookMouse();
            lock (_sync) _roll.Clear();
            foreach (var b in _brush.Values) { try { b.Dispose(); } catch { } }
            foreach (var p in _pen.Values) { try { p.Dispose(); } catch { } }
            _brush.Clear(); _pen.Clear();
            try { _f?.Dispose(); _fB?.Dispose(); _fS?.Dispose(); _fLbl?.Dispose(); _fLblB?.Dispose(); } catch { }
            _f = _fB = _fS = _fLbl = _fLblB = null; _lblSize = -1;
        }

        void Push()
        {
            _eng.StrikesPerSide = StrikesPerSide;
            _eng.ExpirationIndex = ExpirationIndex;
            _eng.Filter = Filter;
            _eng.MaxSeries = MaxSeries;
            _eng.Source = Weight;
            _eng.Dealer = Dealer;
            _eng.RiskFreeRate = RatePct / 100.0;
            _eng.RefreshSeconds = RefreshSeconds;
            _eng.Mode = Mode2;
            _eng.HistoryCacheMinutes = HistCacheMin;
            _eng.MaxHistoryRequests = MaxHistReq;
            _eng.MaxLiveStrikes = MaxLiveStrikes;

        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (!_started) { try { _eng.Start(this.Symbol); _started = true; } catch { } }
            Capture();
        }

        /// <summary>Keep one profile shape per minute; that feeds the rolling outlines.</summary>
        void Capture()
        {
            try
            {
                var s = _eng.Current;
                if (s == null || !s.Ok || s.Rows.Count == 0) return;
                var now = DateTime.UtcNow;
                if ((now - _lastShot).TotalSeconds < 60) return;
                _lastShot = now;

                int n = s.Rows.Count;
                var shot = new Shot { T = now, K = new double[n], A = new double[n], B = new double[n] };
                for (int i = 0; i < n; i++)
                {
                    shot.K[i] = s.Rows[i].Strike;
                    shot.A[i] = s.Rows[i].SideA(Source);
                    shot.B[i] = s.Rows[i].SideB(Source);
                }
                lock (_sync)
                {
                    _roll.Add(shot);
                    // 31 minutes of history is all M30 can need
                    while (_roll.Count > 0 && (now - _roll[0].T).TotalMinutes > 32) _roll.RemoveAt(0);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ helpers
        SolidBrush Br(Color c, int a)
        {
            int argb = Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B).ToArgb();
            if (!_brush.TryGetValue(argb, out var b)) { b = new SolidBrush(Color.FromArgb(argb)); _brush[argb] = b; }
            return b;
        }

        Pen Pn(Color c, int a, float w, DashStyle d = DashStyle.Solid)
        {
            int argb = Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B).ToArgb();
            long key = ((long)argb << 16) ^ ((long)(w * 10) << 4) ^ (long)d;
            if (!_pen.TryGetValue(key, out var p))
            {
                p = new Pen(Color.FromArgb(argb), Math.Max(0.1f, w)) { DashStyle = d };
                _pen[key] = p;
            }
            return p;
        }

        static DashStyle Dash(PeakLineStyle s)
        {
            switch (s)
            {
                case PeakLineStyle.Solid: return DashStyle.Solid;
                case PeakLineStyle.Dot: return DashStyle.Dot;
                case PeakLineStyle.DashDot: return DashStyle.DashDot;
                case PeakLineStyle.DashDotDot: return DashStyle.DashDotDot;
                default: return DashStyle.Dash;
            }
        }

        void Fonts()
        {
            if (_f == null) _f = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
            if (_fB == null) _fB = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
            if (_fS == null) _fS = new Font("Segoe UI", 7.25f, FontStyle.Regular, GraphicsUnit.Point);
            if (_fLbl == null || _fLblB == null || _lblSize != LabelSize)
            {
                try { _fLbl?.Dispose(); _fLblB?.Dispose(); } catch { }
                _lblSize = LabelSize;
                float pt = Math.Max(6, LabelSize) * 0.75f;
                _fLbl = new Font("Segoe UI", pt, FontStyle.Regular, GraphicsUnit.Point);
                _fLblB = new Font("Segoe UI", pt, FontStyle.Bold, GraphicsUnit.Point);
            }
        }

        /// <summary>
        /// Panel fill. Quantower exposes no chart-background colour to read, so the
        /// next best thing is to follow the SAME theme the bars are drawn from - pick
        /// "Ocean" and the panel goes deep blue, "DeepCharts" and it goes near-black.
        /// It tracks the look of the chart instead of being pinned to one colour.
        /// </summary>
        Color BackColour(Palette pal) => AutoBackground ? pal.Panel : PanelColour;

        Palette Pal()
        {
            var p = Palette.Get(Theme);
            if (Theme == OptTheme.Custom)
            {
                p.Call = CustomCall; p.Put = CustomPut; p.Panel = CustomPanel;
                p.PosGex = CustomCall; p.NegGex = CustomPut;
                p.CallWall = CustomCall; p.PutWall = CustomPut;
            }
            return p;
        }

        bool IsSplit => Source == ProfileSource.GexSplit || Source == ProfileSource.CallPutVolume ||
                        Source == ProfileSource.CallPutOI;

        // ---------------------------------------------------------- bucketing
        struct Agg { public double A, B, N, PeakW, PeakK; }
        readonly Dictionary<long, Agg> _bk = new Dictionary<long, Agg>();

        /// <summary>Round-half-up. Math.Round is banker's rounding and drifts on .5 seams.</summary>
        static int Snap(double v) => (int)Math.Floor(v + 0.5);

        /// <summary>
        /// Snap the requested merge factor up to a "nice" number so zooming steps
        /// cleanly between levels instead of oscillating every pixel.
        /// </summary>
        static int NiceCeil(double want)
        {
            if (want <= 1) return 1;
            int[] nice = { 1, 2, 4, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 };
            for (int i = 0; i < nice.Length; i++) if (nice[i] >= want) return nice[i];
            return nice[nice.Length - 1];
        }

        // -------------------------------------------------------------- mouse
        void HookMouse()
        {
            var c = CurrentChart;
            if (c == null || ReferenceEquals(c, _mouseChart)) return;
            UnhookMouse();
            try { c.MouseDown += OnDown; c.MouseMove += OnMove; c.MouseUp += OnUp; _mouseChart = c; } catch { }
        }

        void UnhookMouse()
        {
            var c = _mouseChart;
            if (c == null) return;
            try { c.MouseDown -= OnDown; c.MouseMove -= OnMove; c.MouseUp -= OnUp; } catch { }
            _mouseChart = null;
        }

        /// <summary>Within grab distance of the panel's left edge.</summary>
        bool OnEdge(ChartMouseNativeEventArgs e)
        {
            try
            {
                if (e.Window == null || e.Window.WindowNumber != _winNum) return false;
                if (float.IsNaN(_panelL)) return false;
                float target = float.IsNaN(_grabX) ? _panelL : _grabX;
                if (float.IsNaN(target)) return false;
                return Math.Abs(e.X - target) <= 5;
            }
            catch { return false; }
        }

        void OnDown(object sender, ChartMouseNativeEventArgs e)
        {
            try
            {
                if (!OnEdge(e)) return;
                if (e.Button == NativeMouseButtons.Right)
                { _pwPx = 0; e.Handled = true; e.NeedRedraw = true; return; }   // reset to the setting
                if (e.Button != NativeMouseButtons.Left) return;
                _edgeDrag = true; _dragX = e.X; _dragW0 = _panelR - _panelL;
                e.Handled = true; e.NeedMouseCapture = true;
            }
            catch { }
        }

        void OnMove(object sender, ChartMouseNativeEventArgs e)
        {
            try
            {
                if (e.Window != null && e.Window.WindowNumber == _winNum) _mouseX = e.X;
                else _mouseX = float.NaN;
            }
            catch { }
            if (!_edgeDrag) return;
            try
            {
                // dragging the left edge LEFT grows the panel into the chart
                // drag the line LEFT to widen the panel into the chart
                _pwPx = Math.Max(50f, _dragW0 - (e.X - _dragX));
                e.Handled = true; e.NeedRedraw = true;
            }
            catch { }
        }

        void OnUp(object sender, ChartMouseNativeEventArgs e)
        {
            if (!_edgeDrag) return;
            _edgeDrag = false;
            try { e.Handled = true; } catch { }
        }

        // -------------------------------------------------------------- paint
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (CurrentChart == null || args?.Graphics == null) return;

            IChartWindow win = null;
            foreach (var w in CurrentChart.Windows)
                if (w.WindowNumber == args.WindowIndex) { win = w; break; }
            var conv = win?.CoordinatesConverter;
            if (conv == null) return;

            var g = args.Graphics;
            var st = g.Save();
            try
            {
                var rect = args.Rectangle;
                g.SetClip(rect);
                g.SmoothingMode = SmoothingMode.None;      // crisp bar edges, cheaper
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Fonts();

                var pal = Pal();
                var snap = _eng.Current;

                // Width in pixels wins when set - either typed in, or produced by
                // dragging the panel's left edge. Otherwise fall back to the percentage.
                float pw = _pwPx > 0 ? _pwPx
                         : (PanelWidthPx > 0 ? PanelWidthPx : rect.Width * WidthPct / 100f);
                pw = Math.Max(50f, Math.Min(rect.Width * 0.85f, pw));
                float px = Side == Dock.Left ? rect.Left : rect.Right - pw;
                // slide the whole column toward or away from the price scale
                px += PanelOffset;
                if (px < rect.Left - pw) px = rect.Left - pw;
                if (px > rect.Right) px = rect.Right;
                _panelL = px; _panelR = px + pw;
                _winNum = win.WindowNumber;
                HookMouse();

                if (snap == null || !snap.Ok || snap.Rows.Count == 0)
                {
                    if (PanelAlpha > 0) g.FillRectangle(Br(BackColour(pal), PanelAlpha), px, rect.Top, pw, rect.Height);
                    return;
                }

                Panel(g, conv, rect, pal, snap, px, pw);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                Levels(g, conv, rect, pal, snap, px, pw);
            }
            catch { }
            finally { g.Restore(st); }
        }

        // -------------------------------------------------------------- panel
        void Panel(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect,
                   Palette pal, Snapshot snap, float px, float pw)
        {
            // reserved column
            if (PanelAlpha > 0) g.FillRectangle(Br(BackColour(pal), PanelAlpha), px, rect.Top, pw, rect.Height);
            if (ShowDivider)
            {
                float dx = Side == Dock.Left ? px + pw : px;
                g.DrawLine(Pn(pal.PanelEdge, 150, 1f), dx, rect.Top, dx, rect.Bottom);
            }

            var rows = snap.Rows;
            bool split = IsSplit;
            int nR = rows.Count;
            if (nR == 0) return;

            // The ruler owns a strip on the CHART side of the panel, so the strike
            // scale sits between the candles and the bars - the same relationship the
            // price scale has to the profile in DeepCharts.
            float ruler = ShowRuler ? Math.Max(24, RulerWidth) : 0f;
            float barsX = Side == Dock.Right ? px + ruler : px;
            float barsW = Math.Max(20f, pw - ruler);
            float rulerX = Side == Dock.Right ? px : px + barsW;

            float inset = 5f;
            float usable = Math.Max(8f, (barsW - inset * 2f) * Math.Max(20, Math.Min(100, BarLenPct)) / 100f);
            float centre = split ? barsX + inset + usable * 0.5f
                                 : (Side == Dock.Left ? barsX + inset : barsX + barsW - inset);
            float span = split ? usable * 0.5f : usable;

            // =================================================================
            //  ANCHORED PRICE-GRID BUCKETING
            //
            //  The previous greedy pixel scan ("merge while |y - curY| < pitch")
            //  made bucket membership depend on where the scan started, and sized
            //  each bar from its NEIGHBOURS' centres. Two independent roundings
            //  never tile: 1px gaps at one zoom, 1px overlaps at another. That was
            //  the bars-on-top-of-each-other bug.
            //
            //  Every reference platform (TradingView "ticks per row", Sierra, ATAS)
            //  buckets in PRICE space on a FIXED grid instead. The anchor is derived
            //  from the strikes, never from the viewport, so panning cannot change
            //  which strikes share a bucket. Each row then takes its pixel bounds
            //  from its OWN price edges, so row k's bottom IS row k+1's top.
            // =================================================================

            // step = smallest positive gap between adjacent strikes
            double step = 0;
            for (int i = 1; i < nR; i++)
            {
                double d = rows[i].Strike - rows[i - 1].Strike;
                if (d > 1e-9 && (step <= 0 || d < step)) step = d;
            }
            if (step <= 0) step = Math.Max(Symbol != null ? Symbol.TickSize : 0.01, 1e-9);
            double anchor = Math.Floor(rows[0].Strike / step) * step;   // FIXED, viewport-independent

            // pixel density probed at the viewport centre (safe on log scales too)
            double pMid = conv.GetPrice(rect.Top + rect.Height * 0.5f);
            double yA0 = conv.GetChartY(pMid), yB0 = conv.GetChartY(pMid + step);
            if (double.IsNaN(yA0) || double.IsNaN(yB0) || double.IsInfinity(yA0) || double.IsInfinity(yB0)) return;
            double pxPerStep = Math.Abs(yA0 - yB0);
            if (pxPerStep <= 1e-6) return;

            int merge = NiceCeil(Math.Max(1, MinPitch) / pxPerStep);
            double bucketPrice = merge * step;
            if (bucketPrice <= 0) return;

            // visible price window (axis is inverted, so min/max it)
            double pv1 = conv.GetPrice(rect.Top), pv2 = conv.GetPrice(rect.Bottom);
            double visLo = Math.Min(pv1, pv2) - bucketPrice;
            double visHi = Math.Max(pv1, pv2) + bucketPrice;

            // ---- accumulate into buckets (EXTENSIVE values -> SUM) ----------
            _bk.Clear();
            for (int i = 0; i < nR; i++)
            {
                var r = rows[i];
                if (r.Strike < visLo || r.Strike > visHi) continue;      // cull before bucketing
                long k = (long)Math.Floor((r.Strike - anchor) / bucketPrice + 1e-9);
                if (!_bk.TryGetValue(k, out var agg)) agg = new Agg();
                double a = r.SideA(Source), b = r.SideB(Source), nv = r.Net(Source);
                agg.A += a; agg.B += b; agg.N += nv;
                double w = Math.Abs(split ? Math.Max(Math.Abs(a), Math.Abs(b)) : nv);
                if (w > agg.PeakW) { agg.PeakW = w; agg.PeakK = r.Strike; }
                _bk[k] = agg;
            }
            if (_bk.Count == 0) return;

            // ---- normalise over the VISIBLE merged buckets ------------------
            double bmax = 0;
            foreach (var kv in _bk)
            {
                double m = split ? Math.Max(Math.Abs(kv.Value.A), Math.Abs(kv.Value.B))
                                 : Math.Abs(kv.Value.N);
                if (m > bmax) bmax = m;
            }
            if (bmax <= 0) return;

            int alpha = (int)(255 * Math.Max(10, Math.Min(100, BarOpacity)) / 100.0);
            snap.Peaks(Source, split, out double callPeakK, out double putPeakK);

            // crisp fills: AA on an axis-aligned rect only blurs the seam
            var smPrev = g.SmoothingMode; var poPrev = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (var kv in _bk)
            {
                long k = kv.Key; var agg = kv.Value;

                // EDGE-SHARED geometry: bounds come from this row's own price edges,
                // so bot[k] == top[k+1] exactly. No gaps, no overlap, at any zoom.
                double pLo = anchor + k * bucketPrice, pHi = pLo + bucketPrice;
                double yLo = conv.GetChartY(pLo), yHi = conv.GetChartY(pHi);
                if (double.IsNaN(yLo) || double.IsNaN(yHi) ||
                    double.IsInfinity(yLo) || double.IsInfinity(yHi)) continue;

                int top = Snap(Math.Min(yLo, yHi));
                int bot = Snap(Math.Max(yLo, yHi));
                int rowH = Math.Max(1, bot - top);                    // half-open [top, bot)
                if (bot < rect.Top - rowH || top > rect.Bottom + rowH) continue;

                int h = (int)(rowH * Math.Max(20, Math.Min(100, TickHeightPct)) / 100.0) - BarGap;
                if (h < 1) h = 1;
                if (h > 48) h = 48;
                int barTop = top + (rowH - h) / 2;                    // integer centring
                float yMid = barTop + h * 0.5f;

                if (split)
                {
                    double a = agg.A, b = agg.B;
                    Color ca = ColourFor(pal, a, true), cb = ColourFor(pal, b, false);
                    float la = Len(a, bmax, span), lb = Len(b, bmax, span);

                    if (Disputed && Math.Abs(a) > 1e-9 && Math.Abs(b) > 1e-9)
                    {
                        float lo2 = Math.Min(la, lb);
                        g.FillRectangle(Br(pal.Text, 26), centre - lo2, barTop, lo2 * 2f, h);
                    }

                    bool hiA = CallPeak && CallPeakBar && !double.IsNaN(callPeakK) && agg.PeakK == callPeakK;
                    bool hiB = PutPeak && PutPeakBar && !double.IsNaN(putPeakK) && agg.PeakK == putPeakK;

                    Bar(g, centre, yMid, la, h, ca, Side == Dock.Left, alpha, hiA);
                    Bar(g, centre, yMid, lb, h, cb, Side != Dock.Left, alpha, hiB);
                    if (ValueLabels)
                    {
                        BarLabel(g, pal, centre, yMid, la, h, a, ca, Side == Dock.Left);
                        BarLabel(g, pal, centre, yMid, lb, h, b, cb, Side != Dock.Left);
                    }
                }
                else
                {
                    double v = agg.N;
                    Color c = ColourFor(pal, v, v >= 0);
                    float len = Len(v, bmax, span);
                    bool hi = (CallPeak && CallPeakBar && agg.PeakK == callPeakK) ||
                              (PutPeak && PutPeakBar && agg.PeakK == putPeakK);
                    Bar(g, centre, yMid, len, h, c, Side == Dock.Left, alpha, hi);
                    if (ValueLabels) BarLabel(g, pal, centre, yMid, len, h, v, c, Side == Dock.Left);
                }
            }

            g.SmoothingMode = smPrev; g.PixelOffsetMode = poPrev;

            // ---- strike ruler ------------------------------------------------
            // Built from the SAME bucket rows as the bars, so a label can never point
            // at a different level than the bar beside it.
            if (ShowRuler && ruler > 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (RulerLine)
                {
                    // This line sits between the strike ruler and the bars. It is the
                    // grab handle for resizing the panel, so it is drawn a little
                    // stronger than a plain divider and remembered for hit-testing.
                    float ax = Side == Dock.Right ? rulerX + ruler - 1f : rulerX + 1f;
                    _grabX = ax;
                    bool hot = _edgeDrag || (!float.IsNaN(_mouseX) && Math.Abs(_mouseX - ax) <= 5);
                    g.DrawLine(Pn(pal.Axis, hot ? 220 : 130, hot ? 2f : 1.2f), ax, rect.Top, ax, rect.Bottom);
                    if (hot)
                    {
                        // grip marks, so it is obvious the line can be dragged
                        float cy = (rect.Top + rect.Bottom) * 0.5f;
                        for (int gi = -2; gi <= 2; gi++)
                            g.DrawLine(Pn(pal.Text, 200, 1f), ax - 2f, cy + gi * 4f, ax + 2f, cy + gi * 4f);
                    }
                }

                float lastY = float.NaN;
                float need = _fS.GetHeight(g) + 2f;
                var keys = new List<long>(_bk.Keys);
                keys.Sort();
                for (int ki = keys.Count - 1; ki >= 0; ki--)
                {
                    long k = keys[ki];
                    double pLo = anchor + k * bucketPrice, pHi = pLo + bucketPrice;
                    double yLo = conv.GetChartY(pLo), yHi = conv.GetChartY(pHi);
                    if (double.IsNaN(yLo) || double.IsNaN(yHi)) continue;
                    float yc = (float)((yLo + yHi) * 0.5);
                    if (yc < rect.Top + 2 || yc > rect.Bottom - 2) continue;
                    if (!float.IsNaN(lastY) && Math.Abs(yc - lastY) < need) continue;   // never overprint
                    lastY = yc;

                    // label the bucket's representative strike, not the row centre,
                    // so the number is a strike that actually exists
                    double kk = _bk[k].PeakK > 0 ? _bk[k].PeakK : (pLo + pHi) * 0.5;
                    string t = kk.ToString("0.##", CultureInfo.InvariantCulture);
                    var sz = g.MeasureString(t, _fS);
                    float tx = Side == Dock.Right ? rulerX + ruler - sz.Width - 5f : rulerX + 4f;
                    g.DrawString(t, _fS, Br(pal.TextDim, 215), tx, yc - sz.Height / 2f);

                    float tickA = Side == Dock.Right ? rulerX + ruler - 3f : rulerX + 1f;
                    g.DrawLine(Pn(pal.Axis, 140, 1f), tickA, yc, tickA + 3f, yc);
                }
                g.SmoothingMode = SmoothingMode.None;
            }

            if (split && ShowCentre)
                g.DrawLine(Pn(pal.Axis, 130, 1f), centre, rect.Top, centre, rect.Bottom);

            if (ShowCurve && snap.CurveLevels.Length > 1) Curve(g, conv, rect, pal, snap, centre, span, split);
            if (ShowCumulative) Cumulative(g, conv, rect, pal, snap, centre, span);
            Rolling(g, conv, rect, pal, centre, span, bmax, split);

            if (ShowCaption) Caption(g, rect, pal, barsX, barsW);
            if (ShowSummary) Summary(g, rect, pal, snap, barsX, barsW);
        }

        Color ColourFor(Palette pal, double v, bool callSide)
        {
            switch (Source)
            {
                case ProfileSource.GexCallMinusPut:
                case ProfileSource.DexCallMinusPut:
                case ProfileSource.CallMinusPutVolume:
                case ProfileSource.CallMinusPutOI:
                    return v >= 0 ? pal.Call : pal.Put;      // signed -> sign decides
                default:
                    return callSide ? pal.Call : pal.Put;    // split -> side decides
            }
        }

        /// <summary>
        /// Bar length for a value. One huge strike would otherwise flatten every other
        /// bar to nothing; sqrt/log compression keeps the smaller strikes readable while
        /// preserving the ordering.
        /// </summary>
        float Len(double v, double max, float span)
        {
            if (max <= 0) return 0f;
            double t = Math.Abs(v) / max;
            if (t > 1) t = 1;
            switch (Scale)
            {
                case BarScale.Sqrt: t = Math.Sqrt(t); break;
                case BarScale.Log: t = Math.Log(1.0 + 9.0 * t) / Math.Log(10.0); break;
            }
            return (float)(t * span);
        }

        void Bar(Graphics g, float centre, float y, float len, float thick, Color c,
                 bool toRight, int alpha, bool highlight)
        {
            // 1px stub rather than dropping a non-zero bucket entirely
            int x = Snap(toRight ? centre : centre - len);
            int top = Snap(y - thick / 2f);
            int w = Math.Max(1, Snap(len));
            int h = Math.Max(1, Snap(thick));
            var rc = new Rectangle(x, top, w, h);

            if (BarFill == Fill.Hollow)
            {
                g.DrawRectangle(Pn(c, alpha, (float)Math.Max(0.5, BorderWidth)), rc.X, rc.Y, rc.Width, rc.Height);
            }
            else if (BarFill == Fill.Gradient && rc.Width > 2f)
            {
                try
                {
                    using (var lb = new LinearGradientBrush(
                        new RectangleF(rc.X - 0.5f, rc.Y, rc.Width + 1f, rc.Height),
                        Color.FromArgb(alpha, c), Color.FromArgb(Math.Max(20, alpha / 4), c),
                        toRight ? LinearGradientMode.Horizontal : LinearGradientMode.Horizontal))
                        g.FillRectangle(lb, rc);
                }
                catch { g.FillRectangle(Br(c, alpha), rc); }
            }
            else g.FillRectangle(Br(c, alpha), rc);

            if (highlight)
                g.DrawRectangle(Pn(Color.White, 210, 1.2f), rc.X, rc.Y, rc.Width, rc.Height);
            else if (BorderWidth > 0 && BarFill == Fill.Solid && thick >= 3f)
                g.DrawRectangle(Pn(c, Math.Min(255, alpha + 40), (float)BorderWidth), rc.X, rc.Y, rc.Width, rc.Height);
        }

        /// <summary>
        /// Draw a bucket's value INSIDE its own bar, tinted to match that bar. Sits
        /// inside when the bar is long enough to hold the text, otherwise just past
        /// the tip so a short bar still shows its number. Skipped when the row is too
        /// short to draw text without colliding with its neighbours.
        /// </summary>
        void BarLabel(Graphics g, Palette pal, float centre, float yMid, float len, int h,
                      double v, Color barColour, bool toRight)
        {
            if (h < 7 || Math.Abs(v) < 1e-9) return;
            string t = StrikeRow.IsMoney(Source) ? OptionsEngine.Money(v) : OptionsEngine.Num(v);
            var sz = g.MeasureString(t, _fLblB);
            if (sz.Height > h + 4f) return;                 // row too short to read

            // Always OUTSIDE the bar tip, bold, in the bar's own colour - the value
            // stays legible whatever the bar length and never sits on the fill.
            float x = toRight ? centre + len + 4f : centre - len - sz.Width - 4f;
            g.DrawString(t, _fLblB, Br(barColour, 250), x, yMid - sz.Height / 2f);
        }

        void Curve(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                   Snapshot snap, float centre, float span, bool split)
        {
            var lv = snap.CurveLevels; var gx = snap.CurveGex;
            double m = 0;
            for (int i = 0; i < gx.Length; i++) m = Math.Max(m, Math.Abs(gx[i]));
            if (m <= 0) return;

            var pts = new List<PointF>(lv.Length);
            for (int i = 0; i < lv.Length; i++)
            {
                float y = (float)conv.GetChartY(lv[i]);
                if (float.IsNaN(y) || float.IsInfinity(y)) continue;
                float off = (float)(gx[i] / m * span);
                pts.Add(new PointF(split ? centre + (Side == Dock.Left ? off : -off)
                                         : centre + (Side == Dock.Left ? Math.Abs(off) : -Math.Abs(off)), y));
            }
            if (pts.Count < 2) return;
            g.DrawLines(Pn(pal.ZeroGamma, 200, 1.5f), pts.ToArray());

            for (int i = 1; i < gx.Length && i < pts.Count; i++)
                if ((gx[i - 1] <= 0 && gx[i] > 0) || (gx[i - 1] >= 0 && gx[i] < 0))
                    g.FillEllipse(Br(pal.ZeroGamma, 235), pts[i].X - 3f, pts[i].Y - 3f, 6f, 6f);
        }

        void Cumulative(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                        Snapshot snap, float centre, float span)
        {
            var rows = snap.Rows;
            if (rows.Count < 2) return;
            double run = 0, m = 0;
            var cum = new double[rows.Count];
            for (int i = rows.Count - 1; i >= 0; i--) { run += rows[i].GexDollars; cum[i] = run; m = Math.Max(m, Math.Abs(run)); }
            if (m <= 0) return;

            var pts = new List<PointF>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                float y = (float)conv.GetChartY(rows[i].Strike);
                float off = (float)(Math.Abs(cum[i]) / m * span);
                pts.Add(new PointF(centre + (Side == Dock.Left ? off : -off), y));
            }
            g.DrawLines(Pn(pal.Accent, 165, 1.3f, DashStyle.Dash), pts.ToArray());
        }

        /// <summary>Step outlines of the profile as it stood N minutes ago.</summary>
        void Rolling(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                     float centre, float span, double max, bool split)
        {
            if (!RollM1 && !RollM5 && !RollM15 && !RollM30) return;
            Shot[] hist;
            lock (_sync) hist = _roll.ToArray();
            if (hist.Length == 0) return;

            var now = DateTime.UtcNow;
            if (RollM1) Outline(g, conv, rect, pal, hist, now, 1, centre, span, max, split, DashStyle.Dot);
            if (RollM5) Outline(g, conv, rect, pal, hist, now, 5, centre, span, max, split, DashStyle.Dot);
            if (RollM15) Outline(g, conv, rect, pal, hist, now, 15, centre, span, max, split, DashStyle.Dash);
            if (RollM30) Outline(g, conv, rect, pal, hist, now, 30, centre, span, max, split, DashStyle.Solid);
        }

        void Outline(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                     Shot[] hist, DateTime now, int minutes, float centre, float span, double max,
                     bool split, DashStyle dash)
        {
            Shot best = null; double bd = double.MaxValue;
            for (int i = 0; i < hist.Length; i++)
            {
                double d = Math.Abs((now - hist[i].T).TotalMinutes - minutes);
                if (d < bd) { bd = d; best = hist[i]; }
            }
            if (best == null || bd > minutes * 0.6 + 1.5 || max <= 0) return;

            int alpha = 100 + Math.Max(0, 60 - minutes);
            var pen = Pn(pal.Text, alpha, RollWidth, dash);
            var pts = new List<PointF>(best.K.Length);
            for (int i = 0; i < best.K.Length; i++)
            {
                float y = (float)conv.GetChartY(best.K[i]);
                if (float.IsNaN(y)) continue;
                double v = split ? best.A[i] : (best.A[i] + best.B[i]);
                float off = (float)(Math.Abs(v) / max * span);
                pts.Add(new PointF(centre + (Side == Dock.Left ? off : -off), y));
            }
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
        }

        void Tags(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                  Snapshot snap, float centre, float span, double max, bool split)
        {
            bool money = StrikeRow.IsMoney(Source);
            var top = snap.Rows.OrderByDescending(r => Math.Abs(split ? r.SideA(Source) : r.Net(Source))).Take(6);
            foreach (var r in top)
            {
                double v = split ? r.SideA(Source) : r.Net(Source);
                if (Math.Abs(v) < 1e-9) continue;
                float y = (float)conv.GetChartY(r.Strike);
                if (y < rect.Top + 4 || y > rect.Bottom - 4) continue;
                string s = money ? OptionsEngine.Money(v) : OptionsEngine.Num(v);
                var sz = g.MeasureString(s, _fLbl);
                float len = Len(v, max, span);
                float x = Side == Dock.Left ? centre + len + 3f : centre - len - sz.Width - 3f;
                g.DrawString(s, _fLbl, Br(pal.Text, 225), x, y - sz.Height / 2f);
            }
        }

        void Caption(Graphics g, Rectangle rect, Palette pal, float px, float pw)
        {
            string cap = StrikeRow.Caption(Source) + "   " + OptionsEngine.FilterLabel(Filter);
            var sz = g.MeasureString(cap, _fB);
            g.DrawString(cap, _fB, Br(pal.Text, 235), px + (pw - sz.Width) / 2f, rect.Bottom - sz.Height - 4f);
        }

        void Summary(Graphics g, Rectangle rect, Palette pal, Snapshot snap, float px, float pw)
        {
            double call, put;
            bool money = StrikeRow.IsMoney(Source);
            switch (Source)
            {
                case ProfileSource.CallPutVolume:
                case ProfileSource.CallMinusPutVolume: call = snap.CallVolTotal; put = snap.PutVolTotal; break;
                case ProfileSource.CallPutOI:
                case ProfileSource.CallMinusPutOI: call = snap.CallTotal; put = snap.PutTotal; break;
                default: call = snap.CallGexTotal; put = snap.PutGexTotal; break;
            }
            double net = call + (money ? put : -put);

            string sn = (money ? "$" : "") + OptionsEngine.Money(net);
            string sc = (money ? "$" : "") + OptionsEngine.Money(call);
            string sp = (money ? "$" : "") + OptionsEngine.Money(money ? put : -put);

            float y = rect.Top + 4f;
            if (Side == Dock.Left && TitleAreaWidth > 0 && px < rect.Left + TitleAreaWidth) y = rect.Top + 24f;

            Line(g, "NET", sn, net >= 0 ? pal.Call : pal.Put, px, pw, ref y, pal);
            Line(g, "CALL", sc, pal.Call, px, pw, ref y, pal);
            Line(g, "PUT", sp, pal.Put, px, pw, ref y, pal);
        }

        void Line(Graphics g, string k, string v, Color c, float px, float pw, ref float y, Palette pal)
        {
            var szk = g.MeasureString(k, _fS);
            var szv = g.MeasureString(v, _fB);
            g.DrawString(k, _fS, Br(pal.TextDim, 220), px + 6f, y);
            g.DrawString(v, _fB, Br(c, 240), px + pw - szv.Width - 6f, y - 1f);
            y += Math.Max(szk.Height, szv.Height) + 1f;
        }

        // ------------------------------------------------------------- levels
        void Levels(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                    Snapshot snap, float px, float pw)
        {
            // levels live on the CHART side of the divider, like DeepCharts peak lines
            float lx = Side == Dock.Left ? px + pw : rect.Left;
            float rx = Side == Dock.Left ? rect.Right : px;
            if (rx - lx < 20) { lx = rect.Left; rx = rect.Right; }

            if (ShowExpMove && !double.IsNaN(snap.ExpectedMove) && snap.ExpectedMove > 0)
            {
                Band(g, conv, rect, pal, snap.Underlying, snap.ExpectedMove, lx, rx, 1.0, "1s");
                if (Sigma2 > 1.0)
                    Band(g, conv, rect, pal, snap.Underlying, snap.ExpectedMove, lx, rx, Sigma2,
                         Sigma2.ToString("0.#", CultureInfo.InvariantCulture) + "s");
            }

            snap.Peaks(Source, IsSplit, out double ck, out double pk);
            var dash = Dash(PeakStyle);

            if (CallPeak && CallPeakLine && !double.IsNaN(ck))
                Level(g, conv, rect, pal.Call, ck, lx, rx, PeakWidth, dash,
                      "CALL PEAK  " + ck.ToString("0.##", CultureInfo.InvariantCulture));

            if (PutPeak && PutPeakLine && !double.IsNaN(pk))
                Level(g, conv, rect, pal.Put, pk, lx, rx, PeakWidth, dash,
                      "PUT PEAK  " + pk.ToString("0.##", CultureInfo.InvariantCulture));

            if (ShowZeroGamma && !double.IsNaN(snap.ZeroGamma))
                Level(g, conv, rect, pal.ZeroGamma, snap.ZeroGamma, lx, rx, 2f, DashStyle.Solid,
                      "ZERO GAMMA  " + snap.ZeroGamma.ToString("0.##", CultureInfo.InvariantCulture));

            if (ShowMaxPain && !double.IsNaN(snap.MaxPain))
                Level(g, conv, rect, pal.MaxPain, snap.MaxPain, lx, rx, 1.6f, DashStyle.Dash,
                      "MAX PAIN  " + snap.MaxPain.ToString("0.##", CultureInfo.InvariantCulture));
        }

        void Level(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Color c,
                   double price, float lx, float rx, float width, DashStyle dash, string label)
        {
            float y = (float)conv.GetChartY(price);
            if (float.IsNaN(y) || y < rect.Top - 2 || y > rect.Bottom + 2) return;
            g.DrawLine(Pn(c, 215, width, dash), lx, y, rx, y);
            if (!LevelLabels) return;

            var sz = g.MeasureString(label, _fS);
            float bx = rx - sz.Width - 8f;
            if (bx < lx) bx = lx + 2f;
            g.FillRectangle(Br(Color.Black, 140), bx - 3f, y - sz.Height / 2f - 1f, sz.Width + 6f, sz.Height + 2f);
            g.DrawString(label, _fS, Br(c, 245), bx, y - sz.Height / 2f);
        }

        void Band(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                  double centre, double em, float lx, float rx, double mult, string tag)
        {
            // LINES ONLY - no shaded rectangles. A filled band tints the candles behind
            // it and reads as chart noise; two clean lines carry the same information.
            double up = centre + em * mult, dn = centre - em * mult;
            EmLine(g, conv, rect, pal, up, lx, rx, tag);
            EmLine(g, conv, rect, pal, dn, lx, rx, tag);
        }

        void EmLine(Graphics g, IChartWindowCoordinatesConverter conv, Rectangle rect, Palette pal,
                    double price, float lx, float rx, string tag)
        {
            float y = (float)conv.GetChartY(price);
            if (float.IsNaN(y) || y < rect.Top - 2 || y > rect.Bottom + 2) return;
            g.DrawLine(Pn(pal.Band, 190, ExpMoveWidth, DashStyle.Dash), lx, y, rx, y);
            if (!LevelLabels) return;
            string txt = tag + "  " + price.ToString("0.##", CultureInfo.InvariantCulture);
            var sz = g.MeasureString(txt, _fS);
            float bx = Side == Dock.Right ? lx + 4f : rx - sz.Width - 8f;
            g.DrawString(txt, _fS, Br(pal.Band, 225), bx, y - sz.Height - 1f);
        }

        // -------------------------------------------------------------- info
        void Info(Graphics g, Rectangle rect, Palette pal, Snapshot snap)
        {
            var lines = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Source", StrikeRow.Caption(Source)),
                new KeyValuePair<string, string>("Expiration", snap.ExpirationLabel),
                new KeyValuePair<string, string>("Series", snap.SeriesUsed.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Underlying", snap.Underlying.ToString("0.##", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Point value", "$" + snap.PointValue.ToString("0.##", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Total GEX / 1%", "$" + OptionsEngine.Money(snap.TotalGexD)),
                new KeyValuePair<string, string>("Regime", snap.TotalGexD >= 0 ? "positive - damping" : "negative - amplifying"),
                new KeyValuePair<string, string>("Zero gamma", Fmt(snap.ZeroGamma)),
                new KeyValuePair<string, string>("Max pain", Fmt(snap.MaxPain)),
                new KeyValuePair<string, string>("ATM IV", double.IsNaN(snap.AtmIV) ? "-" : (snap.AtmIV * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"),
                new KeyValuePair<string, string>("Expected move", double.IsNaN(snap.ExpectedMove) ? "-" : "+/-" + snap.ExpectedMove.ToString("0.##", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Strikes", snap.Rows.Count.ToString(CultureInfo.InvariantCulture))
            };

            float pad = 7f, lh = _f.GetHeight(g) + 2f, wk = 0, wv = 0;
            foreach (var kv in lines)
            {
                wk = Math.Max(wk, g.MeasureString(kv.Key, _f).Width);
                wv = Math.Max(wv, g.MeasureString(kv.Value, _fB).Width);
            }
            float w = wk + wv + pad * 3f, h = lines.Count * lh + pad * 2f;
            float x = Table == Corner.TopLeft || Table == Corner.BottomLeft ? rect.Left + 8f : rect.Right - w - 8f;
            float y = Table == Corner.TopLeft || Table == Corner.TopRight ? rect.Top + 8f : rect.Bottom - h - 8f;
            if (Table == Corner.TopRight) y = Math.Max(y, rect.Top + 26f);

            g.FillRectangle(Br(pal.Panel, 238), x, y, w, h);
            g.DrawRectangle(Pn(pal.PanelEdge, 110, 1f), x, y, w, h);

            float yy = y + pad;
            foreach (var kv in lines)
            {
                g.DrawString(kv.Key, _f, Br(pal.TextDim, 235), x + pad, yy);
                Color c = pal.Text;
                if (kv.Key == "Regime" || kv.Key == "Total GEX / 1%") c = snap.TotalGexD >= 0 ? pal.Call : pal.Put;
                g.DrawString(kv.Value, _fB, Br(c, 255), x + pad * 2f + wk, yy);
                yy += lh;
            }
        }

        static string Fmt(double v) => double.IsNaN(v) ? "-" : v.ToString("0.##", CultureInfo.InvariantCulture);

    }
}
