// =============================================================================
//  TNX OPTIONS PRO - shared options engine
// -----------------------------------------------------------------------------
//  DATA  Quantower's native options API. Nothing external, no web calls:
//          Core.Instance.GetOptionSeries(underlier)  -> IList<OptionSerie>
//          Core.Instance.GetStrikes(serie)           -> IList<Symbol>
//        Each strike Symbol carries StrikePrice, OptionType, ExpirationDate,
//        OpenInterest, Volume, Bid/Ask/Last. Live updates arrive by attaching to
//        Symbol.NewQuote / NewDayBar - Quantower auto-subscribes when a handler
//        is attached and auto-unsubscribes when it is removed.
//
//  MODEL  ES / NQ options are options ON FUTURES, so the correct closed form is
//        BLACK-76, not plain Black-Scholes on spot:
//              d1    = ( ln(F/K) + 0.5*s^2*T ) / ( s*sqrt(T) )
//              gamma = exp(-rT) * phi(d1) / ( F * s * sqrt(T) )
//        IV is solved from the mid price with Newton plus a bisection fallback.
//        Quantower's own PriceModel is tried first, and we only fall back to our
//        solver when it returns NaN, so we stay consistent with the platform.
//
//  GEX   SqueezeMetrics, "Gamma Exposure" (Dec 2017), p.5 - dealers are assumed
//        LONG calls and SHORT puts, so per strike, in shares of the underlying:
//              GEX_call = gamma * OI * multiplier
//              GEX_put  = gamma * OI * multiplier * (-1)
//        Dollar-normalised to the standard "$ per 1% move" convention:
//              GEX$ = gamma * OI * multiplier * F^2 * 0.01
//        Positive total GEX means dealer hedging DAMPENS moves (sell rallies,
//        buy dips). Negative AMPLIFIES them. The flip point is ZERO GAMMA, found
//        by repricing the whole chain at each candidate level and locating the
//        sign change. See Barbon and Buraschi, "Gamma Fragility" (Univ. St.
//        Gallen, 2020) on negative gamma driving intraday momentum.
//
//        The call-vs-put SIGN is an INVENTORY ASSUMPTION, not a property of the
//        greek - Black-Scholes gamma is positive for calls and puts alike. The
//        DealerModel setting exposes that choice instead of hiding it.
//
//  MAX PAIN  the strike minimising total intrinsic payout to option holders:
//              pain(K) = SUM callOI_i * max(0, K - K_i)
//                      + SUM putOI_i  * max(0, K_i - K)
//
//  EXPECTED MOVE  EM = F * IV_atm * sqrt(T). The cone widens with sqrt(elapsed)
//        and reaches the full move at expiration.
//
//  THREADING  the chain is fetched on a background task and published as an
//        immutable Snapshot reference. Painters read that reference once, so
//        there are no locks anywhere on the paint path.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Options;

namespace TNXIndicators
{
    // ----------------------------------------------------------------- enums
    public enum OptTheme
    {
        ProDark, Neon, Classic, Ocean, Monochrome, Ember, Ice, Aurora,
        Terminal, Sunset, HighContrast, AtasX, DeepCharts, DeepChartsHeat,
        Bookmap, TradingView,
        // extra palettes
        Volcano, Matrix, Nord, Dracula, Solarized, Cyberpunk, Gold, Arctic,
        Sakura, Forest, Midnight, Copper,
        Custom
    }

    /// <summary>Which side of the book dealers are assumed to hold.</summary>
    public enum DealerModel
    {
        LongCallShortPut,   // SqueezeMetrics convention (default)
        ShortCallShortPut,  // dealers short everything (customer-buys-all)
        AbsoluteGamma       // unsigned - pure gamma concentration
    }

    public enum GexSource { OpenInterest, DayVolume }

    /// <summary>What the profile bars measure. Mirrors the DeepCharts data-source list.</summary>
    public enum ProfileSource
    {
        GexCallMinusPut,      // gamma-weighted, dealer-signed  (GEX)
        GexSplit,             // call GEX / put GEX side by side
        CallPutVolume,        // raw traded contracts           (C/P Sigma)
        CallMinusPutVolume,   // net traded contracts
        CallPutOI,            // open interest, split
        CallMinusPutOI,       // net open interest
        DexCallMinusPut       // delta exposure
    }

    /// <summary>Expiration window to aggregate, DeepCharts style.</summary>
    public enum ExpFilter { ZeroDTE, Max1D, Max7D, Max31D, Max91D, All }

    public enum PeakLineStyle { Solid, Dash, Dot, DashDot, DashDotDot }

    /// <summary>Heatmap intensity normalisation.</summary>
    public enum IntensityMode { ProportionalSession, ProportionalTimeAverage, ProportionalCombined }

    /// <summary>
    /// Where per-strike numbers come from.
    ///
    /// HistoryOnly (default) asks for DAILY BARS per option symbol and reads
    /// OpenInterest / Volume / Close off them. It never opens a real-time
    /// subscription, so it cannot trigger "Real-time data subscription for
    /// instrument ... is not enabled" on a futures-only market-data plan.
    ///
    /// LiveQuotes additionally subscribes to Level-1 on each strike. Only choose
    /// it if your data plan actually includes OPTIONS - on a futures-only plan
    /// every strike is refused by the feed.
    /// </summary>
    public enum DataMode { HistoryOnly, LiveQuotes }

    // --------------------------------------------------------------- palette
    /// <summary>Resolved colour set. Built once per repaint, cheap.</summary>
    public sealed class Palette
    {
        public Color Call, Put, PosGex, NegGex, ZeroGamma, MaxPain, CallWall, PutWall;
        public Color Text, TextDim, Panel, PanelEdge, Axis, Band, BandEdge, Accent;

        public static Palette Get(OptTheme t)
        {
            var p = new Palette();
            switch (t)
            {
                case OptTheme.Neon:
                    p.Call = C(0, 255, 170); p.Put = C(255, 45, 120);
                    p.PosGex = C(0, 229, 255); p.NegGex = C(255, 0, 128);
                    p.ZeroGamma = C(255, 238, 88); p.MaxPain = C(186, 104, 200);
                    p.Text = C(233, 245, 255); p.Panel = C(10, 12, 24); p.Accent = C(0, 229, 255);
                    break;
                case OptTheme.Classic:
                    p.Call = C(0, 150, 60); p.Put = C(200, 30, 45);
                    p.PosGex = C(30, 110, 200); p.NegGex = C(220, 120, 20);
                    p.ZeroGamma = C(20, 20, 20); p.MaxPain = C(120, 60, 160);
                    p.Text = C(20, 20, 20); p.Panel = C(248, 248, 248); p.Accent = C(30, 110, 200);
                    break;
                case OptTheme.Ocean:
                    p.Call = C(38, 198, 190); p.Put = C(239, 108, 120);
                    p.PosGex = C(72, 160, 230); p.NegGex = C(244, 143, 70);
                    p.ZeroGamma = C(255, 213, 79); p.MaxPain = C(149, 117, 205);
                    p.Text = C(222, 238, 246); p.Panel = C(11, 26, 38); p.Accent = C(72, 160, 230);
                    break;
                case OptTheme.Monochrome:
                    p.Call = C(224, 224, 224); p.Put = C(128, 128, 128);
                    p.PosGex = C(200, 200, 200); p.NegGex = C(110, 110, 110);
                    p.ZeroGamma = C(255, 255, 255); p.MaxPain = C(170, 170, 170);
                    p.Text = C(238, 238, 238); p.Panel = C(18, 18, 18); p.Accent = C(220, 220, 220);
                    break;
                case OptTheme.Ember:
                    p.Call = C(255, 167, 38); p.Put = C(198, 40, 40);
                    p.PosGex = C(255, 202, 40); p.NegGex = C(183, 28, 28);
                    p.ZeroGamma = C(255, 245, 157); p.MaxPain = C(255, 112, 67);
                    p.Text = C(255, 236, 210); p.Panel = C(26, 13, 8); p.Accent = C(255, 167, 38);
                    break;
                case OptTheme.Ice:
                    p.Call = C(129, 212, 250); p.Put = C(144, 164, 214);
                    p.PosGex = C(79, 195, 247); p.NegGex = C(92, 107, 192);
                    p.ZeroGamma = C(255, 255, 255); p.MaxPain = C(179, 157, 219);
                    p.Text = C(232, 245, 253); p.Panel = C(13, 23, 38); p.Accent = C(79, 195, 247);
                    break;
                case OptTheme.Aurora:
                    p.Call = C(80, 250, 190); p.Put = C(255, 121, 198);
                    p.PosGex = C(139, 233, 253); p.NegGex = C(255, 85, 155);
                    p.ZeroGamma = C(241, 250, 140); p.MaxPain = C(189, 147, 249);
                    p.Text = C(240, 245, 255); p.Panel = C(18, 18, 32); p.Accent = C(139, 233, 253);
                    break;
                case OptTheme.Terminal:
                    p.Call = C(0, 230, 118); p.Put = C(255, 82, 82);
                    p.PosGex = C(0, 230, 118); p.NegGex = C(255, 82, 82);
                    p.ZeroGamma = C(255, 235, 59); p.MaxPain = C(0, 188, 212);
                    p.Text = C(0, 255, 128); p.Panel = C(0, 8, 4); p.Accent = C(0, 230, 118);
                    break;
                case OptTheme.Sunset:
                    p.Call = C(255, 183, 77); p.Put = C(229, 57, 120);
                    p.PosGex = C(255, 138, 101); p.NegGex = C(156, 39, 176);
                    p.ZeroGamma = C(255, 241, 118); p.MaxPain = C(255, 87, 34);
                    p.Text = C(255, 240, 235); p.Panel = C(32, 14, 32); p.Accent = C(255, 138, 101);
                    break;
                case OptTheme.HighContrast:
                    p.Call = C(0, 255, 0); p.Put = C(255, 0, 0);
                    p.PosGex = C(0, 255, 255); p.NegGex = C(255, 0, 255);
                    p.ZeroGamma = C(255, 255, 0); p.MaxPain = C(255, 255, 255);
                    p.Text = C(255, 255, 255); p.Panel = C(0, 0, 0); p.Accent = C(255, 255, 0);
                    break;
                case OptTheme.AtasX:
                    p.Call = C(76, 175, 175); p.Put = C(233, 105, 120);
                    p.PosGex = C(102, 187, 106); p.NegGex = C(239, 83, 80);
                    p.ZeroGamma = C(255, 213, 79); p.MaxPain = C(171, 71, 188);
                    p.Text = C(226, 232, 240); p.Panel = C(24, 28, 36); p.Accent = C(102, 187, 106);
                    break;
                case OptTheme.DeepCharts:
                    // sampled from the DeepCharts option profile: bright green calls,
                    // violet puts, near-black panel
                    p.Call = C(34, 197, 94); p.Put = C(139, 92, 246);
                    p.PosGex = C(34, 197, 94); p.NegGex = C(139, 92, 246);
                    p.ZeroGamma = C(250, 204, 21); p.MaxPain = C(56, 189, 248);
                    p.Text = C(228, 232, 236); p.Panel = C(13, 13, 15); p.Accent = C(34, 197, 94);
                    break;
                case OptTheme.DeepChartsHeat:
                    // heatmap palette: teal positive, salmon negative
                    p.Call = C(45, 212, 191); p.Put = C(251, 146, 110);
                    p.PosGex = C(45, 212, 191); p.NegGex = C(251, 146, 110);
                    p.ZeroGamma = C(250, 204, 21); p.MaxPain = C(167, 139, 250);
                    p.Text = C(228, 232, 236); p.Panel = C(11, 12, 14); p.Accent = C(45, 212, 191);
                    break;
                case OptTheme.Bookmap:
                    p.Call = C(120, 220, 120); p.Put = C(235, 110, 110);
                    p.PosGex = C(255, 210, 60); p.NegGex = C(200, 60, 200);
                    p.ZeroGamma = C(255, 255, 190); p.MaxPain = C(120, 200, 255);
                    p.Text = C(230, 230, 230); p.Panel = C(8, 8, 8); p.Accent = C(255, 210, 60);
                    break;
                case OptTheme.TradingView:
                    p.Call = C(38, 166, 154); p.Put = C(239, 83, 80);
                    p.PosGex = C(41, 98, 255); p.NegGex = C(255, 152, 0);
                    p.ZeroGamma = C(255, 235, 59); p.MaxPain = C(156, 39, 176);
                    p.Text = C(209, 212, 220); p.Panel = C(19, 23, 34); p.Accent = C(41, 98, 255);
                    break;
                case OptTheme.Volcano:
                    p.Call = C(255, 138, 0); p.Put = C(214, 40, 40);
                    p.PosGex = C(255, 186, 8); p.NegGex = C(157, 2, 8);
                    p.ZeroGamma = C(255, 234, 167); p.MaxPain = C(255, 107, 107);
                    p.Text = C(255, 236, 217); p.Panel = C(20, 8, 6); p.Accent = C(255, 138, 0);
                    break;
                case OptTheme.Matrix:
                    p.Call = C(0, 255, 65); p.Put = C(0, 143, 17);
                    p.PosGex = C(0, 255, 65); p.NegGex = C(0, 100, 20);
                    p.ZeroGamma = C(190, 255, 190); p.MaxPain = C(0, 200, 120);
                    p.Text = C(0, 255, 65); p.Panel = C(0, 8, 0); p.Accent = C(0, 255, 65);
                    break;
                case OptTheme.Nord:
                    p.Call = C(163, 190, 140); p.Put = C(191, 97, 106);
                    p.PosGex = C(136, 192, 208); p.NegGex = C(208, 135, 112);
                    p.ZeroGamma = C(235, 203, 139); p.MaxPain = C(180, 142, 173);
                    p.Text = C(236, 239, 244); p.Panel = C(46, 52, 64); p.Accent = C(136, 192, 208);
                    break;
                case OptTheme.Dracula:
                    p.Call = C(80, 250, 123); p.Put = C(255, 85, 85);
                    p.PosGex = C(139, 233, 253); p.NegGex = C(255, 121, 198);
                    p.ZeroGamma = C(241, 250, 140); p.MaxPain = C(189, 147, 249);
                    p.Text = C(248, 248, 242); p.Panel = C(40, 42, 54); p.Accent = C(189, 147, 249);
                    break;
                case OptTheme.Solarized:
                    p.Call = C(133, 153, 0); p.Put = C(220, 50, 47);
                    p.PosGex = C(38, 139, 210); p.NegGex = C(203, 75, 22);
                    p.ZeroGamma = C(181, 137, 0); p.MaxPain = C(211, 54, 130);
                    p.Text = C(238, 232, 213); p.Panel = C(0, 43, 54); p.Accent = C(42, 161, 152);
                    break;
                case OptTheme.Cyberpunk:
                    p.Call = C(0, 255, 213); p.Put = C(255, 0, 110);
                    p.PosGex = C(58, 134, 255); p.NegGex = C(251, 86, 7);
                    p.ZeroGamma = C(255, 238, 50); p.MaxPain = C(190, 49, 255);
                    p.Text = C(236, 240, 255); p.Panel = C(8, 5, 20); p.Accent = C(0, 255, 213);
                    break;
                case OptTheme.Gold:
                    p.Call = C(212, 175, 55); p.Put = C(120, 90, 30);
                    p.PosGex = C(255, 215, 100); p.NegGex = C(140, 70, 20);
                    p.ZeroGamma = C(255, 245, 200); p.MaxPain = C(200, 160, 90);
                    p.Text = C(245, 236, 210); p.Panel = C(18, 15, 8); p.Accent = C(212, 175, 55);
                    break;
                case OptTheme.Arctic:
                    p.Call = C(72, 202, 228); p.Put = C(144, 129, 216);
                    p.PosGex = C(0, 180, 216); p.NegGex = C(108, 117, 180);
                    p.ZeroGamma = C(202, 240, 248); p.MaxPain = C(173, 181, 189);
                    p.Text = C(240, 248, 255); p.Panel = C(10, 20, 30); p.Accent = C(0, 180, 216);
                    break;
                case OptTheme.Sakura:
                    p.Call = C(255, 175, 204); p.Put = C(206, 66, 87);
                    p.PosGex = C(255, 143, 177); p.NegGex = C(155, 93, 229);
                    p.ZeroGamma = C(255, 236, 179); p.MaxPain = C(199, 125, 255);
                    p.Text = C(255, 240, 245); p.Panel = C(28, 16, 24); p.Accent = C(255, 143, 177);
                    break;
                case OptTheme.Forest:
                    p.Call = C(106, 168, 79); p.Put = C(166, 77, 62);
                    p.PosGex = C(126, 200, 80); p.NegGex = C(140, 90, 50);
                    p.ZeroGamma = C(230, 220, 120); p.MaxPain = C(140, 170, 200);
                    p.Text = C(232, 240, 226); p.Panel = C(14, 22, 16); p.Accent = C(126, 200, 80);
                    break;
                case OptTheme.Midnight:
                    p.Call = C(92, 225, 230); p.Put = C(244, 91, 105);
                    p.PosGex = C(69, 123, 255); p.NegGex = C(255, 108, 92);
                    p.ZeroGamma = C(255, 221, 89); p.MaxPain = C(146, 118, 255);
                    p.Text = C(224, 231, 255); p.Panel = C(6, 8, 20); p.Accent = C(69, 123, 255);
                    break;
                case OptTheme.Copper:
                    p.Call = C(224, 122, 95); p.Put = C(129, 84, 74);
                    p.PosGex = C(242, 155, 122); p.NegGex = C(107, 68, 62);
                    p.ZeroGamma = C(244, 211, 158); p.MaxPain = C(160, 130, 170);
                    p.Text = C(245, 232, 222); p.Panel = C(24, 16, 14); p.Accent = C(224, 122, 95);
                    break;
                default: // ProDark
                    p.Call = C(38, 198, 145); p.Put = C(235, 64, 92);
                    p.PosGex = C(64, 160, 255); p.NegGex = C(255, 140, 60);
                    p.ZeroGamma = C(255, 214, 80); p.MaxPain = C(180, 120, 255);
                    p.Text = C(235, 240, 250); p.Panel = C(16, 19, 26); p.Accent = C(64, 160, 255);
                    break;
            }
            p.TextDim = Blend(p.Text, p.Panel, 0.45);
            p.PanelEdge = Blend(p.Text, p.Panel, 0.80);
            p.Axis = Blend(p.Text, p.Panel, 0.72);
            p.CallWall = p.Call; p.PutWall = p.Put;
            p.Band = p.Accent; p.BandEdge = Blend(p.Accent, p.Panel, 0.35);
            return p;
        }

        static Color C(int r, int g, int b) => Color.FromArgb(255, r, g, b);

        public static Color A(Color c, int a) =>
            Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);

        public static Color Blend(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(255,
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    // ------------------------------------------------------------ black-76
    public static class B76
    {
        const double INV_SQRT_2PI = 0.3989422804014327;

        /// <summary>Standard normal PDF.</summary>
        public static double Phi(double x) => INV_SQRT_2PI * Math.Exp(-0.5 * x * x);

        /// <summary>Standard normal CDF, Abramowitz and Stegun 7.1.26 (err &lt; 7.5e-8).</summary>
        public static double N(double x)
        {
            if (double.IsNaN(x)) return double.NaN;
            bool neg = x < 0; if (neg) x = -x;
            double k = 1.0 / (1.0 + 0.2316419 * x);
            double poly = k * (0.319381530 + k * (-0.356563782 + k * (1.781477937 +
                          k * (-1.821255978 + k * 1.330274429))));
            double v = 1.0 - Phi(x) * poly;
            return neg ? 1.0 - v : v;
        }

        /// <summary>Black-76 gamma per 1 point of the future, per single contract.</summary>
        public static double Gamma(double F, double K, double T, double s, double r)
        {
            if (F <= 0 || K <= 0 || T <= 0 || s <= 0) return 0;
            double sq = s * Math.Sqrt(T);
            if (sq <= 1e-12) return 0;
            double d1 = (Math.Log(F / K) + 0.5 * s * s * T) / sq;
            return Math.Exp(-r * T) * Phi(d1) / (F * sq);
        }

        public static double Delta(double F, double K, double T, double s, double r, bool call)
        {
            if (F <= 0 || K <= 0 || T <= 0 || s <= 0)
                return call ? (F > K ? 1 : 0) : (F < K ? -1 : 0);
            double sq = s * Math.Sqrt(T);
            double d1 = (Math.Log(F / K) + 0.5 * s * s * T) / sq;
            double df = Math.Exp(-r * T);
            return call ? df * N(d1) : df * (N(d1) - 1.0);
        }

        /// <summary>Vega per 1.00 (=100 vol points) of vol, per contract.</summary>
        public static double Vega(double F, double K, double T, double s, double r)
        {
            if (F <= 0 || K <= 0 || T <= 0 || s <= 0) return 0;
            double sq = s * Math.Sqrt(T);
            double d1 = (Math.Log(F / K) + 0.5 * s * s * T) / sq;
            return Math.Exp(-r * T) * F * Phi(d1) * Math.Sqrt(T);
        }

        public static double Price(double F, double K, double T, double s, double r, bool call)
        {
            double df = Math.Exp(-r * T);
            if (T <= 0 || s <= 0) return df * Math.Max(0, call ? F - K : K - F);
            double sq = s * Math.Sqrt(T);
            double d1 = (Math.Log(F / K) + 0.5 * s * s * T) / sq;
            double d2 = d1 - sq;
            return call ? df * (F * N(d1) - K * N(d2))
                        : df * (K * N(-d2) - F * N(-d1));
        }

        /// <summary>
        /// Implied vol from a premium. Newton first (quadratic), bisection fallback
        /// when vega collapses - which it does for deep OTM strikes, exactly where a
        /// naive Newton solver diverges.
        /// </summary>
        public static double IV(double price, double F, double K, double T, double r, bool call)
        {
            if (price <= 0 || F <= 0 || K <= 0 || T <= 0) return double.NaN;
            double intrinsic = Math.Exp(-r * T) * Math.Max(0, call ? F - K : K - F);
            if (price <= intrinsic + 1e-10) return double.NaN;   // no time value

            double s = 0.25;
            for (int i = 0; i < 12; i++)
            {
                double diff = Price(F, K, T, s, r, call) - price;
                if (Math.Abs(diff) < 1e-8) return s;
                double v = Vega(F, K, T, s, r);
                if (v < 1e-8) break;
                double step = diff / v;
                if (step > 1.0) step = 1.0; else if (step < -1.0) step = -1.0;
                double ns = s - step;
                if (ns <= 1e-4 || ns > 10.0) break;
                s = ns;
            }
            double lo = 1e-4, hi = 10.0;
            if (Price(F, K, T, hi, r, call) < price) return double.NaN;
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Price(F, K, T, mid, r, call) < price) lo = mid; else hi = mid;
                if (hi - lo < 1e-6) break;
            }
            double res = 0.5 * (lo + hi);
            return (res > 1e-3 && res < 9.99) ? res : double.NaN;
        }
    }

    // ------------------------------------------------------------ strike row
    public sealed class StrikeRow
    {
        public double Strike;
        public double CallOI, PutOI, CallVol, PutVol;
        public double CallIV, PutIV;          // NaN when unsolvable
        public double CallGamma, PutGamma;    // per contract, per 1 point of F
        public double GexDollars;             // signed, $ per 1% move
        public double CallGexD, PutGexD;      // signed components
        public double CallDexD, PutDexD;      // delta exposure, $ per 1 point
        public double NetOI => CallOI - PutOI;
        public double TotalOI => CallOI + PutOI;
        public double TotalVol => CallVol + PutVol;

        /// <summary>Call-side magnitude for the chosen source (drawn one way).</summary>
        public double SideA(ProfileSource s)
        {
            switch (s)
            {
                case ProfileSource.CallPutVolume:
                case ProfileSource.CallMinusPutVolume: return CallVol;
                case ProfileSource.CallPutOI:
                case ProfileSource.CallMinusPutOI: return CallOI;
                case ProfileSource.DexCallMinusPut: return CallDexD;
                default: return CallGexD;
            }
        }

        /// <summary>Put-side magnitude for the chosen source (drawn the other way).</summary>
        public double SideB(ProfileSource s)
        {
            switch (s)
            {
                case ProfileSource.CallPutVolume:
                case ProfileSource.CallMinusPutVolume: return PutVol;
                case ProfileSource.CallPutOI:
                case ProfileSource.CallMinusPutOI: return PutOI;
                case ProfileSource.DexCallMinusPut: return PutDexD;
                default: return PutGexD;
            }
        }

        /// <summary>Single net value for the chosen source.</summary>
        public double Net(ProfileSource s)
        {
            switch (s)
            {
                case ProfileSource.CallPutVolume:
                case ProfileSource.CallMinusPutVolume: return CallVol - PutVol;
                case ProfileSource.CallPutOI:
                case ProfileSource.CallMinusPutOI: return CallOI - PutOI;
                case ProfileSource.DexCallMinusPut: return CallDexD + PutDexD;
                default: return GexDollars;
            }
        }

        /// <summary>True when the source is money-denominated (formats as $).</summary>
        public static bool IsMoney(ProfileSource s) =>
            s == ProfileSource.GexCallMinusPut || s == ProfileSource.GexSplit ||
            s == ProfileSource.DexCallMinusPut;

        /// <summary>Short caption shown under the profile, DeepCharts style.</summary>
        public static string Caption(ProfileSource s)
        {
            switch (s)
            {
                case ProfileSource.GexSplit: return "GEX C/P";
                case ProfileSource.CallPutVolume: return "C/P Σ";
                case ProfileSource.CallMinusPutVolume: return "CΣ - PΣ";
                case ProfileSource.CallPutOI: return "C/P OI";
                case ProfileSource.CallMinusPutOI: return "C OI - P OI";
                case ProfileSource.DexCallMinusPut: return "DEX";
                default: return "GEX";
            }
        }
    }

    // -------------------------------------------------------------- snapshot
    /// <summary>Immutable computed view of the chain. Published by reference.</summary>
    public sealed class Snapshot
    {
        public List<StrikeRow> Rows = new List<StrikeRow>();
        public double Underlying;             // F used for the computation
        public double PointValue = 1;         // $ per index point per contract
        public DateTime Expiration;
        public string ExpirationLabel = "";
        public int SeriesUsed;
        public double TotalGexD;              // sum of GexDollars
        public double ZeroGamma = double.NaN;
        public double MaxPain = double.NaN;
        public double CallWall = double.NaN, PutWall = double.NaN;
        public double AtmIV = double.NaN;
        public double ExpectedMove = double.NaN;   // 1 sigma to expiry, in points
        public double T;                            // years to expiry
        public double CallTotal, PutTotal;          // summed OI
        public double CallVolTotal, PutVolTotal;    // summed traded volume
        public double CallGexTotal, PutGexTotal;    // summed signed GEX
        public double DexTotal;                     // summed delta exposure
        public bool HasWeights;                     // feed gave OI or volume
        public string WeightNote = "";              // set when we fell back to volume
        public string FieldNote = "";               // what the feed actually served
        public DateTime StampUtc = DateTime.UtcNow;
        public string Status = "";
        public bool Ok;

        // gamma curve: total GEX$ repriced at each level (for the zero-gamma plot)
        public double[] CurveLevels = Array.Empty<double>();
        public double[] CurveGex = Array.Empty<double>();

        /// <summary>
        /// The same repricing curve, split by side. Dealer exposure is a FUNCTION OF
        /// PRICE: as F moves through the strike ladder, call gamma and put gamma really
        /// do change. Sampling these at each chart bar's own price is what makes a
        /// per-bar reading genuinely differ bar to bar - open interest is static
        /// intraday, so totals alone would print a flat line.
        /// </summary>
        public double[] CurveCallGex = Array.Empty<double>();
        public double[] CurvePutGex = Array.Empty<double>();
        public double[] CurveCallDex = Array.Empty<double>();
        public double[] CurvePutDex = Array.Empty<double>();

        /// <summary>Linear interpolation of a curve at price L. NaN outside the grid.</summary>
        public double SampleCurve(double[] curve, double L)
        {
            var lv = CurveLevels;
            if (curve == null || lv == null || lv.Length < 2 || curve.Length != lv.Length) return double.NaN;
            if (L <= lv[0]) return curve[0];
            if (L >= lv[lv.Length - 1]) return curve[curve.Length - 1];
            int lo = 0, hi = lv.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (lv[mid] <= L) lo = mid; else hi = mid;
            }
            double span = lv[hi] - lv[lo];
            if (span <= 0) return curve[lo];
            double t = (L - lv[lo]) / span;
            return curve[lo] + (curve[hi] - curve[lo]) * t;
        }

        /// <summary>
        /// Every level where the repriced gamma curve crosses zero - not just the one
        /// we report. ATAS marks all of them with dots for exactly this reason: a chain
        /// with clustered OI genuinely has more than one flip.
        /// </summary>
        public double[] Crossings = Array.Empty<double>();

        /// <summary>
        /// The band where the curve is TRUSTWORTHY. Near the edge of the strike window
        /// the sum is missing the strikes beyond it, so the curve there is wrong by an
        /// unknown amount. Outside this band we dim the curve and refuse to report a
        /// zero-gamma level as if it were solid.
        /// </summary>
        public double ReliableLo = double.NaN, ReliableHi = double.NaN;

        /// <summary>False when the reported zero gamma sits in the unreliable edge zone.</summary>
        public bool ZeroGammaReliable;

        /// <summary>Raw (unsmoothed) crossing, before the stabiliser. For diagnostics.</summary>
        public double ZeroGammaRaw = double.NaN;

        /// <summary>
        /// The futures level where TOTAL dealer delta crosses zero - neither net long
        /// nor net short. Above it dealers hold net long inventory (selling overhead);
        /// below it net short (structural buying support). D(F) is monotone increasing
        /// in F because every delta rises with F, so the root is unique and bisection
        /// is safe - unlike the gamma curve, which can cross several times.
        /// </summary>
        public double ZeroDelta = double.NaN;
        public bool ZeroDeltaReliable;

        /// <summary>Net dealer delta expressed in underlying-equivalent contracts.</summary>
        public double DexContracts;

        public double MaxAbsGex
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Rows.Count; i++)
                {
                    double a = Math.Abs(Rows[i].GexDollars);
                    if (a > m) m = a;
                }
                return m;
            }
        }

        public double MaxOI
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].CallOI > m) m = Rows[i].CallOI;
                    if (Rows[i].PutOI > m) m = Rows[i].PutOI;
                }
                return m;
            }
        }

        public double MaxVol
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].CallVol > m) m = Rows[i].CallVol;
                    if (Rows[i].PutVol > m) m = Rows[i].PutVol;
                }
                return m;
            }
        }

        /// <summary>Largest magnitude for the chosen source - the profile scale.</summary>
        public double MaxFor(ProfileSource s, bool split)
        {
            double m = 0;
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                if (split)
                {
                    double a = Math.Abs(r.SideA(s)), b = Math.Abs(r.SideB(s));
                    if (a > m) m = a;
                    if (b > m) m = b;
                }
                else
                {
                    double v = Math.Abs(r.Net(s));
                    if (v > m) m = v;
                }
            }
            return m;
        }

        /// <summary>Strike carrying the largest call-side value, and the put-side one.</summary>
        public void Peaks(ProfileSource s, bool split, out double callPeak, out double putPeak)
        {
            callPeak = putPeak = double.NaN;
            double ba = double.NegativeInfinity, bb = double.NegativeInfinity;
            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                if (split)
                {
                    double a = Math.Abs(r.SideA(s)), b = Math.Abs(r.SideB(s));
                    if (a > ba) { ba = a; callPeak = r.Strike; }
                    if (b > bb) { bb = b; putPeak = r.Strike; }
                }
                else
                {
                    double v = r.Net(s);
                    if (v > ba) { ba = v; callPeak = r.Strike; }
                    if (-v > bb) { bb = -v; putPeak = r.Strike; }
                }
            }
        }
    }

    // ---------------------------------------------------------------- engine
    /// <summary>
    /// Owns the option-chain lifecycle: resolve series, pull strikes, keep a live
    /// subscription on the strikes we actually use, and recompute a Snapshot on a
    /// background cadence. Every public read is a single volatile reference read.
    /// </summary>
    public sealed class OptionsEngine : IDisposable
    {
        public const string AUTO = "Auto (nearest)";

        // ---- configuration (set by the owning indicator before Start) -------
        public int StrikesPerSide = 20;
        public int AggregateSeries = 1;          // 1..3 nearest when Auto
        public string ExpirationChoice = AUTO;
        /// <summary>0 = use ExpFilter. 1 = nearest, 2 = next, ...</summary>
        public int ExpirationIndex = 0;
        /// <summary>Aggregate every series inside this window when ExpirationIndex is 0.</summary>
        public ExpFilter Filter = ExpFilter.ZeroDTE;
        /// <summary>Cap on how many series the filter may aggregate.</summary>
        public int MaxSeries = 6;
        public double RiskFreeRate = 0.05;
        public GexSource Source = GexSource.OpenInterest;
        public DealerModel Dealer = DealerModel.LongCallShortPut;
        public int RefreshSeconds = 15;
        public int MaxSubscriptions = 40;       // hard ceiling on live symbols

        /// <summary>
        /// LiveQuotes is the default because that is what actually works on Rithmic:
        /// its Option Analytics serves live bid/ask/IV for MESU6 / MNQU6 / ESU6, while
        /// option BAR history returns "no data". HistoryOnly is for the opposite kind
        /// of feed. On a feed with no options at all, the probe stops either one after
        /// a couple of requests.
        /// </summary>
        public DataMode Mode = DataMode.LiveQuotes;

        /// <summary>
        /// Hard ceiling on how many STRIKES may be live at once. Rithmic micro-option
        /// entitlements are commonly limited (4 strikes here), and blowing past the
        /// limit is what gets requests refused. Call+put doubles the symbol count.
        /// </summary>
        public int MaxLiveStrikes = 4;

        /// <summary>Write one chain dump to the desktop so the real symbol roots are visible.</summary>
        public bool Diagnostics = true;
        public string UnderlierNote { get; private set; } = "";
        bool _dumped;
        int _liveNoQuote;

        /// <summary>
        /// SLOW-TIER cache. Enumerating the chain (GetOptionSeries + GetStrikes) is the
        /// expensive part and it barely changes; the per-strike VALUES change on every
        /// tick. Caching the resolved Symbol list lets the fast tier just re-read live
        /// fields off those Symbol objects, which is what makes tick-by-tick possible
        /// without hammering the platform.
        /// </summary>
        sealed class Chain
        {
            public string Key = "";
            public List<Symbol> Options = new List<Symbol>();
            public DateTime Stamp;
            public double AnchorF;   // underlying price when resolved
            public double Reach;     // how far price may drift before we re-resolve
        }
        Chain _chain;

        // Zero gamma is a STRUCTURAL level - it must not chase price. IV solved from
        // wide micro-option spreads is noisy, so the raw crossing jitters. We keep a
        // short ring of raw values and report the MEDIAN, which rejects spikes without
        // lagging a genuine regime shift the way an average would.
        readonly List<double> _zgRing = new List<double>();
        double _zgLast = double.NaN;

        /// <summary>Seconds of raw crossings to hold for the median filter. 0 disables.</summary>
        public int ZeroGammaSmoothSec = 20;

        /// <summary>Fast recompute interval. The chain still refreshes on RefreshSeconds.</summary>
        public int FastMs = 250;   // consecutive rebuilds with zero quotes in live mode
        /// <summary>Minutes before a cached daily bar is refetched. Daily data barely moves.</summary>
        public int HistoryCacheMinutes = 20;
        /// <summary>Cap on how many history requests one rebuild may issue.</summary>
        public int MaxHistoryRequests = 90;

        // ---- state -----------------------------------------------------------
        Symbol _under;
        volatile Snapshot _snap = new Snapshot { Status = "not started" };
        CancellationTokenSource _cts;
        Task _loop;
        readonly List<Symbol> _subscribed = new List<Symbol>();
        readonly object _subLock = new object();
        volatile bool _dirty = true;
        string[] _expirationCache = new[] { AUTO };

        // ---- daily-bar cache + circuit breaker -------------------------------
        sealed class Hist { public double OI, Vol, Px; public DateTime When; public bool Ok; }
        readonly Dictionary<string, Hist> _hist = new Dictionary<string, Hist>();
        readonly HashSet<string> _dead = new HashSet<string>();   // symbols that failed: never retried
        int _histFail, _histOk;
        /// <summary>Latched once the feed makes clear options data is not licensed.</summary>
        public bool OptionDataDenied { get; private set; }
        public string DataNote { get; private set; } = "";

        public Snapshot Current => _snap;
        public string[] Expirations => _expirationCache;

        public void Start(Symbol underlier)
        {
            Stop();
            _under = underlier;
            if (_under == null) { _snap = new Snapshot { Status = "no symbol" }; return; }
            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            _loop = Task.Run(() => Loop(tok), tok);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _loop?.Wait(1500); } catch { }
            _cts = null; _loop = null;
            Unsubscribe();
        }

        public void Invalidate() => _dirty = true;

        public void Dispose() => Stop();

        // --------------------------------------------------------------- loop
        void Loop(CancellationToken tok)
        {
            var last = DateTime.MinValue;
            while (!tok.IsCancellationRequested)
            {
                try
                {
                    int wait = Math.Max(50, FastMs);
                    bool due = _dirty || (DateTime.UtcNow - last).TotalMilliseconds >= wait;
                    if (due)
                    {
                        _dirty = false;
                        last = DateTime.UtcNow;
                        // Build() re-reads live values every pass; the chain enumeration
                        // inside it is gated by its own RefreshSeconds cache.
                        var s = Build();
                        if (s != null) _snap = s;
                    }
                }
                catch (Exception ex)
                {
                    _snap = new Snapshot { Status = "error: " + ex.Message };
                }
                int slice = Math.Max(20, Math.Min(100, FastMs / 4));
                for (int i = 0; i < 4 && !tok.IsCancellationRequested; i++)
                    Thread.Sleep(slice);
            }
        }

        // -------------------------------------------------------------- build
        Snapshot Build()
        {
            var snap = new Snapshot();
            var under = _under;
            if (under == null) { snap.Status = "no symbol"; return snap; }

            // HARD STOP: the feed has already refused option data this session.
            // Return immediately - no chain call, no history call, nothing.
            if (OptionDataDenied)
            {
                snap.Status = "options data not available on this connection" +
                              (DataNote.Length > 0 ? " - " + DataNote : "") +
                              "  |  requests stopped (nothing further will be sent)";
                return snap;
            }

            double F = FuturePrice(under);
            snap.Underlying = F;
            snap.PointValue = PointValue(under);
            if (F <= 0) { snap.Status = "waiting for underlying price"; return snap; }

            IList<OptionSerie> series;
            try { series = Core.Instance.GetOptionSeries(under); }
            catch (Exception ex) { snap.Status = "chain error: " + ex.Message; return snap; }

            if (series == null || series.Count == 0)
            {
                snap.Status = "no option series for " + under.Name +
                              " (feed may not carry options on this symbol)";
                return snap;
            }

            var now = Core.Instance.TimeUtils.DateTimeUtcNow;

            if (Diagnostics && !_dumped) { _dumped = true; Dump(under, series); }

            var alive = series.Where(s => s != null && s.ExpirationDate > now.AddHours(-6)).ToList();

            // CRITICAL: GetOptionSeries can hand back chains for a RELATED product.
            // Charting MESU26 was returning the full-size ES chain (root "EP"), which
            // a micro-only entitlement refuses on every strike. Keep only the series
            // that actually belong to this instrument.
            var mine = alive.Where(s => Belongs(s, under)).ToList();
            if (mine.Count == 0)
            {
                // HARD STOP. Falling back to a non-matching chain is what subscribed
                // the ES weeklies (EP12/EP22/EP32/EP42/EW2) while the chart was on a
                // micro - every one of them refused. Better to draw nothing.
                OptionDataDenied = true;
                var roots = string.Join(", ", alive.Where(x => x != null)
                                                   .Select(x => x.UnderlierId ?? x.Name ?? "?")
                                                   .Distinct().Take(6));
                DataNote = "chain belongs to " + roots + ", not " +
                           (under.Root ?? under.Id ?? under.Name);
                snap.Status = "STOPPED - no option series belongs to " +
                              (under.Name ?? under.Id) + ". Feed offered: " + roots +
                              ". Nothing subscribed. See Desktop/TNXOptions_chain_dump.txt";
                return snap;
            }
            UnderlierNote = "";
            alive = mine;

            var live = alive.OrderBy(s => s.ExpirationDate).ToList();
            if (live.Count == 0) { snap.Status = "all series expired"; return snap; }

            // publish the picker list
            var labels = new List<string> { AUTO };
            foreach (var s in live.Take(40))
                labels.Add(Label(s));
            _expirationCache = labels.ToArray();

            // choose series
            var chosen = new List<OptionSerie>();
            if (ExpirationIndex > 0)
                chosen.Add(live[Math.Min(ExpirationIndex - 1, live.Count - 1)]);
            else
            {
                double days = DaysFor(Filter);
                var inWindow = live.Where(s => (s.ExpirationDate - now).TotalDays <= days)
                                   .Take(Math.Max(1, MaxSeries)).ToList();
                // 0DTE with nothing expiring today still needs something to show
                if (inWindow.Count == 0) inWindow.Add(live[0]);
                chosen.AddRange(inWindow);
            }
            if (chosen.Count == 0) { snap.Status = "no series selected"; return snap; }

            snap.SeriesUsed = chosen.Count;
            snap.Expiration = chosen[0].ExpirationDate;
            snap.ExpirationLabel = chosen.Count == 1
                ? Label(chosen[0])
                : Label(chosen[0]) + " +" + (chosen.Count - 1);

            double T = Math.Max((snap.Expiration - now).TotalDays, 0.0) / 365.0;
            if (T <= 0) T = 1.0 / (365.0 * 24.0);      // expiry day: 1 hour floor
            snap.T = T;

            // ---- gather strikes -------------------------------------------
            var map = new Dictionary<double, StrikeRow>();
            var keep = new List<Symbol>();
            // field-availability diagnostics: tells us exactly what the feed served
            int nSym = 0, nQuote = 0, nOI = 0, nVol = 0;

            // ---- SLOW TIER: resolve which option Symbols we care about ------
            // Cached. Re-resolved only when settings change, the cache ages past
            // RefreshSeconds, or price drifts far enough that the strike window moved.
            string chainKey = Filter + "|" + ExpirationIndex + "|" + StrikesPerSide + "|" +
                              MaxLiveStrikes + "|" + MaxSeries + "|" + Mode + "|" + chosen.Count;

            bool chainFresh = _chain != null && _chain.Key == chainKey &&
                              (DateTime.UtcNow - _chain.Stamp).TotalSeconds < Math.Max(2, RefreshSeconds) &&
                              Math.Abs(F - _chain.AnchorF) <= _chain.Reach &&
                              _chain.Options.Count > 0;

            List<Symbol> optionSyms;
            if (chainFresh) optionSyms = _chain.Options;
            else
            {
                optionSyms = new List<Symbol>();
                foreach (var serie in chosen)
                {
                    IList<Symbol> strikes;
                    try { strikes = Core.Instance.GetStrikes(serie); }
                    catch { continue; }
                    if (strikes == null) continue;

                    int wantStrikes = Math.Max(1, StrikesPerSide * 2 + 1);
                    if (Mode == DataMode.LiveQuotes && MaxLiveStrikes > 0)
                        wantStrikes = Math.Min(wantStrikes, MaxLiveStrikes);

                    var allowed = new HashSet<double>(
                        strikes.Where(s => s != null && s.StrikePrice > 0)
                               .Select(s => s.StrikePrice)
                               .Distinct()
                               .OrderBy(k => Math.Abs(k - F))
                               .Take(wantStrikes));

                    var near = strikes
                        .Where(s => s != null && s.StrikePrice > 0 && allowed.Contains(s.StrikePrice)
                                    && StrikeBelongs(s, under))
                        .OrderBy(s => Math.Abs(s.StrikePrice - F))
                        .ToList();

                    if (Mode == DataMode.HistoryOnly && !Probe(near))
                    {
                        snap.Status = "options data refused by this connection" +
                                      (DataNote.Length > 0 ? " - " + DataNote : "") +
                                      "  |  stopped after 1 probe";
                        return snap;
                    }

                    foreach (var os in near)
                    {
                        if (optionSyms.Count >= MaxSubscriptions) break;
                        optionSyms.Add(os);
                    }
                }

                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var o in optionSyms) { lo = Math.Min(lo, o.StrikePrice); hi = Math.Max(hi, o.StrikePrice); }
                double reach = optionSyms.Count > 1 ? Math.Max(1e-9, (hi - lo) * 0.25) : 1e9;

                _chain = new Chain { Key = chainKey, Options = optionSyms,
                                     Stamp = DateTime.UtcNow, AnchorF = F, Reach = reach };

                // subscriptions are managed on the SLOW tier only
                Resubscribe(Mode == DataMode.LiveQuotes ? optionSyms : new List<Symbol>());
            }

            int reqBudget = MaxHistoryRequests;
            {
                    foreach (var os in optionSyms)
                    {

                    double k = os.StrikePrice;
                    if (!map.TryGetValue(k, out var row))
                    {
                        row = new StrikeRow { Strike = k };
                        map[k] = row;
                    }

                    bool call = os.OptionType == OptionType.Call;
                    double oi, vol, px;

                    if (Mode == DataMode.LiveQuotes)
                    {
                        oi = Safe(os.OpenInterest);
                        vol = Safe(os.Volume);
                        px = Mid(os);
                        nSym++;
                        if (px > 0) nQuote++;
                        if (oi > 0) nOI++;
                        if (vol > 0) nVol++;
                    }
                    else
                    {
                        // never subscribes; daily bars only
                        if (!TryHistory(os, ref reqBudget, out oi, out vol, out px)) continue;
                    }

                    double iv = double.NaN;
                    if (px > 0) iv = B76.IV(px, F, k, T, RiskFreeRate, call);

                    if (call) { row.CallOI += oi; row.CallVol += vol; if (!double.IsNaN(iv)) row.CallIV = iv; }
                    else { row.PutOI += oi; row.PutVol += vol; if (!double.IsNaN(iv)) row.PutIV = iv; }
                }
            }

            if (map.Count == 0)
            {
                snap.Status = OptionDataDenied
                    ? "options data not available on this feed" + (DataNote.Length > 0 ? " - " + DataNote : "")
                    : "no strikes returned yet (loading daily bars...)";
                return snap;
            }

            // subscriptions are managed when the chain is resolved (slow tier)

            // ---- trim to the requested window ------------------------------
            var rows = map.Values.OrderBy(r => Math.Abs(r.Strike - F))
                                 .Take(Math.Max(2, StrikesPerSide * 2 + 1))
                                 .OrderBy(r => r.Strike)
                                 .ToList();

            // ---- ATM IV: nearest strike with a solved vol on either side ----
            double atmIv = double.NaN;
            foreach (var r in rows.OrderBy(r => Math.Abs(r.Strike - F)))
            {
                double a = r.CallIV, b = r.PutIV;
                if (!double.IsNaN(a) && !double.IsNaN(b)) { atmIv = 0.5 * (a + b); break; }
                if (!double.IsNaN(a)) { atmIv = a; break; }
                if (!double.IsNaN(b)) { atmIv = b; break; }
            }
            // fall back to a chain median so gamma is still computable
            if (double.IsNaN(atmIv))
            {
                var all = rows.SelectMany(r => new[] { r.CallIV, r.PutIV })
                              .Where(v => !double.IsNaN(v) && v > 0).OrderBy(v => v).ToList();
                if (all.Count > 0) atmIv = all[all.Count / 2];
            }
            snap.AtmIV = atmIv;
            if (!double.IsNaN(atmIv)) snap.ExpectedMove = F * atmIv * Math.Sqrt(T);

            // ---- what did the feed actually give us? -------------------------
            double oiSum = 0, volSum = 0;
            for (int i = 0; i < rows.Count; i++) { oiSum += rows[i].TotalOI; volSum += rows[i].TotalVol; }

            // A feed can serve live quotes without serving open interest. Fall back
            // to traded volume rather than silently drawing an empty profile.
            var effSource = Source;
            if (effSource == GexSource.OpenInterest && oiSum <= 0 && volSum > 0)
                effSource = GexSource.DayVolume;
            snap.WeightNote = effSource == Source ? "" : "OI empty - weighting by volume";
            snap.HasWeights = (oiSum > 0 || volSum > 0);

            // ---- gamma + GEX ------------------------------------------------
            double mult = snap.PointValue;
            double sign = 0.01 * F * F;      // $ per 1% move scaling
            double total = 0;

            foreach (var r in rows)
            {
                double ivC = Pick(r.CallIV, atmIv);
                double ivP = Pick(r.PutIV, atmIv);
                r.CallGamma = B76.Gamma(F, r.Strike, T, ivC, RiskFreeRate);
                r.PutGamma = B76.Gamma(F, r.Strike, T, ivP, RiskFreeRate);

                double wC = effSource == GexSource.DayVolume ? r.CallVol : r.CallOI;
                double wP = effSource == GexSource.DayVolume ? r.PutVol : r.PutOI;

                double gc = r.CallGamma * wC * mult * sign;
                double gp = r.PutGamma * wP * mult * sign;

                switch (Dealer)
                {
                    case DealerModel.ShortCallShortPut: r.CallGexD = -gc; r.PutGexD = -gp; break;
                    case DealerModel.AbsoluteGamma: r.CallGexD = gc; r.PutGexD = gp; break;
                    default: r.CallGexD = gc; r.PutGexD = -gp; break;   // long call / short put
                }
                r.GexDollars = r.CallGexD + r.PutGexD;
                total += r.GexDollars;

                // ---- DELTA EXPOSURE -------------------------------------
                // Black-76 delta is ALREADY signed: calls in (0,+1), puts in (-1,0).
                // So NO call+/put- flip here. Applying the gamma convention to delta
                // would make put delta add to call delta instead of cancelling it,
                // which pins net DEX hugely positive and hides the zero-delta level.
                // Dollar notional delta: Delta * OI * multiplier * F.
                double dSign = Dealer == DealerModel.ShortCallShortPut ? -1.0 : 1.0;
                r.CallDexD = B76.Delta(F, r.Strike, T, ivC, RiskFreeRate, true) * wC * mult * F * dSign;
                r.PutDexD = B76.Delta(F, r.Strike, T, ivP, RiskFreeRate, false) * wP * mult * F * dSign;
            }
            snap.TotalGexD = total;
            snap.Rows = rows;

            foreach (var r in rows)
            {
                snap.CallTotal += r.CallOI; snap.PutTotal += r.PutOI;
                snap.CallVolTotal += r.CallVol; snap.PutVolTotal += r.PutVol;
                snap.CallGexTotal += r.CallGexD; snap.PutGexTotal += r.PutGexD;
                snap.DexTotal += r.CallDexD + r.PutDexD;
            }

            // ---- max pain ----------------------------------------------------
            snap.MaxPain = MaxPain(rows);

            // ---- walls: largest positive / most negative GEX ------------------
            // Restrict each wall to its own side of price. Searching the whole chain
            // picks deep-ITM strikes whose large OI has nothing to do with resistance
            // above or support below.
            double best = double.NegativeInfinity, worst = double.PositiveInfinity;
            foreach (var r in rows)
            {
                if (r.Strike > F && r.GexDollars > best) { best = r.GexDollars; snap.CallWall = r.Strike; }
                if (r.Strike < F && r.GexDollars < worst) { worst = r.GexDollars; snap.PutWall = r.Strike; }
            }
            // when the sign convention makes everything one-signed, fall back to OI
            if (double.IsNaN(snap.CallWall) || best <= 0)
                snap.CallWall = rows.OrderByDescending(r => r.CallOI).FirstOrDefault()?.Strike ?? double.NaN;
            if (double.IsNaN(snap.PutWall) || worst >= 0)
                snap.PutWall = rows.OrderByDescending(r => r.PutOI).FirstOrDefault()?.Strike ?? double.NaN;

            // ---- gamma curve + zero gamma ------------------------------------
            BuildCurve(snap, rows, T, atmIv, mult);
            BuildZeroDelta(snap, rows, T, atmIv, mult, F);

            snap.FieldNote = "sym " + nSym + " / quote " + nQuote + " / OI " + nOI + " / vol " + nVol;

            // Live subscriptions are asynchronous: the first pass only subscribes,
            // quotes land a moment later. Say so instead of reporting a failure.
            if (Mode == DataMode.LiveQuotes && nQuote == 0)
            {
                _liveNoQuote++;
                snap.Rows = rows;

                // Six rebuilds (~90s) with not one quote means the feed is refusing
                // these symbols. Drop every subscription and stop - otherwise we sit
                // here re-subscribing dozens of refused symbols forever.
                if (_liveNoQuote >= 2)
                {
                    Unsubscribe();
                    OptionDataDenied = true;
                    DataNote = "no quotes for " + nSym + " subscribed symbols" +
                               (UnderlierNote.Length > 0 ? "; " + UnderlierNote : "");
                    snap.Status = "STOPPED - the feed served no quotes for these option symbols. " +
                                  "Check the chain dump on your Desktop (TNXOptions_chain_dump.txt) " +
                                  "to see which root was requested.";
                    return snap;
                }

                snap.Status = "subscribed to " + nSym + " symbols, waiting for quotes " +
                              _liveNoQuote + "/2... (" + snap.FieldNote + ")" +
                              (UnderlierNote.Length > 0 ? "  !! " + UnderlierNote : "");
                return snap;
            }
            _liveNoQuote = 0;

            // Quotes but no OI and no volume: gamma is still solvable from IV, so ATM
            // IV and the expected-move bands remain valid - only GEX needs weights.
            if (!snap.HasWeights)
            {
                snap.Ok = true;
                snap.Rows = rows;
                snap.Status = "live  " + rows.Count + " strikes  |  IV + expected move OK, " +
                              "GEX unavailable: feed served no open interest or volume  (" +
                              snap.FieldNote + ")";
                snap.StampUtc = DateTime.UtcNow;
                return snap;
            }

            snap.Ok = true;
            snap.Status = (Mode == DataMode.LiveQuotes ? "live" : "daily") + "  " +
                          rows.Count + " strikes / " + snap.SeriesUsed + " serie(s)  OI " + Num(oiSum) +
                          "  vol " + Num(volSum) +
                          (snap.WeightNote.Length > 0 ? "  [" + snap.WeightNote + "]" : "");
            snap.StampUtc = DateTime.UtcNow;
            return snap;
        }

        static double Pick(double iv, double fallback) =>
            (!double.IsNaN(iv) && iv > 0) ? iv : (double.IsNaN(fallback) ? 0.20 : fallback);

        /// <summary>
        /// Reprice total GEX at a grid of candidate spot levels and find the sign
        /// change. That crossing is the zero-gamma level: below it dealers are
        /// short gamma (moves amplify), above it long gamma (moves damp).
        /// </summary>
        void BuildCurve(Snapshot snap, List<StrikeRow> rows, double T, double atmIv, double mult)
        {
            if (rows.Count < 2) return;
            double lo = rows[0].Strike, hi = rows[rows.Count - 1].Strike;
            if (hi <= lo) return;

            const int N = 160;
            var lv = new double[N];
            var gx = new double[N];
            var cg = new double[N];
            var pg = new double[N];
            var cd = new double[N];
            var pd = new double[N];
            double step = (hi - lo) / (N - 1);
            double dSign = Dealer == DealerModel.ShortCallShortPut ? -1.0 : 1.0;

            for (int i = 0; i < N; i++)
            {
                double L = lo + step * i;
                double sum = 0, sumCg = 0, sumPg = 0, sumCd = 0, sumPd = 0;
                double sc = 0.01 * L * L;
                for (int j = 0; j < rows.Count; j++)
                {
                    var r = rows[j];
                    double ivC = Pick(r.CallIV, atmIv), ivP = Pick(r.PutIV, atmIv);
                    double wC = Source == GexSource.DayVolume ? r.CallVol : r.CallOI;
                    double wP = Source == GexSource.DayVolume ? r.PutVol : r.PutOI;

                    double gc = B76.Gamma(L, r.Strike, T, ivC, RiskFreeRate) * wC * mult * sc;
                    double gp = B76.Gamma(L, r.Strike, T, ivP, RiskFreeRate) * wP * mult * sc;

                    switch (Dealer)
                    {
                        case DealerModel.ShortCallShortPut: sumCg += -gc; sumPg += -gp; break;
                        case DealerModel.AbsoluteGamma: sumCg += gc; sumPg += gp; break;
                        default: sumCg += gc; sumPg += -gp; break;   // gamma: sign imposed
                    }

                    // delta is ALREADY signed - no second flip (see the DEX trap)
                    sumCd += B76.Delta(L, r.Strike, T, ivC, RiskFreeRate, true) * wC * mult * L * dSign;
                    sumPd += B76.Delta(L, r.Strike, T, ivP, RiskFreeRate, false) * wP * mult * L * dSign;
                }
                sum = sumCg + sumPg;
                lv[i] = L; gx[i] = sum;
                cg[i] = sumCg; pg[i] = sumPg; cd[i] = sumCd; pd[i] = sumPd;
            }
            snap.CurveLevels = lv; snap.CurveGex = gx;
            snap.CurveCallGex = cg; snap.CurvePutGex = pg;
            snap.CurveCallDex = cd; snap.CurvePutDex = pd;

            // ---- reliability band -------------------------------------------
            // The curve at level L sums only the strikes inside our window. Near the
            // window edge a large share of the real gamma is missing, so the curve is
            // wrong there by an unknown amount. Trust only the interior.
            int edge = Math.Max(1, (int)Math.Round(rows.Count * 0.15));
            snap.ReliableLo = rows[Math.Min(edge, rows.Count - 1)].Strike;
            snap.ReliableHi = rows[Math.Max(0, rows.Count - 1 - edge)].Strike;

            // ---- every crossing, then pick the STRUCTURAL one ----------------
            var cross = new List<double>();
            double bestSlope = -1, zg = double.NaN;
            for (int i = 1; i < N; i++)
            {
                bool up = gx[i - 1] <= 0 && gx[i] > 0;
                bool dn = gx[i - 1] >= 0 && gx[i] < 0;
                if (!up && !dn) continue;

                double t = gx[i] - gx[i - 1];
                double x = Math.Abs(t) < 1e-12
                    ? lv[i]
                    : lv[i - 1] + (lv[i] - lv[i - 1]) * (-gx[i - 1] / t);
                cross.Add(x);

                // Selecting the crossing NEAREST PRICE makes the line teleport between
                // crossings as price drifts - an artefact of the rule, not the market.
                // The meaningful flip is the STEEPEST one: where dealer gamma actually
                // changes sign hardest. Ignore crossings in the unreliable edge zone.
                if (x < snap.ReliableLo || x > snap.ReliableHi) continue;
                double slope = Math.Abs(t) / Math.Max(1e-9, lv[i] - lv[i - 1]);
                if (slope > bestSlope) { bestSlope = slope; zg = x; }
            }
            snap.Crossings = cross.ToArray();
            snap.ZeroGammaRaw = zg;

            // if nothing survived inside the reliable band, fall back to the steepest
            // crossing anywhere but mark it as NOT reliable rather than pretend
            bool reliable = !double.IsNaN(zg);
            if (!reliable && cross.Count > 0)
            {
                double bs = -1;
                for (int i = 1; i < N; i++)
                {
                    bool up = gx[i - 1] <= 0 && gx[i] > 0;
                    bool dn = gx[i - 1] >= 0 && gx[i] < 0;
                    if (!up && !dn) continue;
                    double t = gx[i] - gx[i - 1];
                    double x = Math.Abs(t) < 1e-12
                        ? lv[i] : lv[i - 1] + (lv[i] - lv[i - 1]) * (-gx[i - 1] / t);
                    double slope = Math.Abs(t) / Math.Max(1e-9, lv[i] - lv[i - 1]);
                    if (slope > bs) { bs = slope; zg = x; }
                }
            }
            snap.ZeroGammaReliable = reliable;
            snap.ZeroGamma = Stabilise(zg);
        }

        /// <summary>
        /// Median-of-recent filter for the zero-gamma level. A mean would drag on a real
        /// regime shift; a median holds the level rock-steady against IV spikes yet snaps
        /// once a majority of samples have genuinely moved.
        /// </summary>
        double Stabilise(double raw)
        {
            if (double.IsNaN(raw)) { _zgRing.Clear(); return double.NaN; }
            if (ZeroGammaSmoothSec <= 0) { _zgLast = raw; return raw; }

            int cap = Math.Max(3, (ZeroGammaSmoothSec * 1000) / Math.Max(50, FastMs));
            _zgRing.Add(raw);
            if (_zgRing.Count > cap) _zgRing.RemoveRange(0, _zgRing.Count - cap);

            var tmp = new List<double>(_zgRing);
            tmp.Sort();
            double med = tmp.Count % 2 == 1
                ? tmp[tmp.Count / 2]
                : 0.5 * (tmp[tmp.Count / 2 - 1] + tmp[tmp.Count / 2]);
            _zgLast = med;
            return med;
        }

        /// <summary>
        /// Zero delta by repricing the chain, not by reading where bars change sign.
        /// Only Delta is recomputed at each candidate level; OI and per-strike sigma
        /// are held fixed. Widens the bracket until the endpoints straddle zero, then
        /// bisects. Returns NaN when no root exists in a sane range rather than
        /// reporting a fabricated level.
        /// </summary>
        void BuildZeroDelta(Snapshot snap, List<StrikeRow> rows, double T, double atmIv, double mult, double F)
        {
            if (rows.Count == 0 || F <= 0) return;
            double dSign = Dealer == DealerModel.ShortCallShortPut ? -1.0 : 1.0;

            Func<double, double> D = (double L) =>
            {
                double sum = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    double ivC = Pick(r.CallIV, atmIv), ivP = Pick(r.PutIV, atmIv);
                    double wC = Source == GexSource.DayVolume ? r.CallVol : r.CallOI;
                    double wP = Source == GexSource.DayVolume ? r.PutVol : r.PutOI;
                    sum += B76.Delta(L, r.Strike, T, ivC, RiskFreeRate, true) * wC * mult * L * dSign;
                    sum += B76.Delta(L, r.Strike, T, ivP, RiskFreeRate, false) * wP * mult * L * dSign;
                }
                return sum;
            };

            snap.DexContracts = mult > 0 && F > 0 ? snap.DexTotal / (mult * F) : 0;

            double w = 0.06, lo = F * (1 - w), hi = F * (1 + w);
            double flo = D(lo), fhi = D(hi);
            int grow = 0;
            while (flo * fhi > 0 && grow++ < 5)
            {
                w *= 2.0;
                lo = F * (1 - w); hi = F * (1 + w);
                if (lo <= 0) lo = F * 0.05;
                flo = D(lo); fhi = D(hi);
            }
            if (flo * fhi > 0) { snap.ZeroDelta = double.NaN; snap.ZeroDeltaReliable = false; return; }

            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                double fm = D(mid);
                if (flo * fm <= 0) { hi = mid; fhi = fm; } else { lo = mid; flo = fm; }
                if (hi - lo < 1e-4 * F) break;
            }
            double z = 0.5 * (lo + hi);
            snap.ZeroDelta = z;
            // trustworthy only inside the strike window we actually summed
            snap.ZeroDeltaReliable = !double.IsNaN(snap.ReliableLo) &&
                                     z >= snap.ReliableLo && z <= snap.ReliableHi;
        }

        /// <summary>Strike minimising total intrinsic payout to option holders.</summary>
        public static double MaxPain(List<StrikeRow> rows)
        {
            if (rows == null || rows.Count == 0) return double.NaN;
            double bestK = double.NaN, bestPain = double.MaxValue;
            for (int i = 0; i < rows.Count; i++)
            {
                double K = rows[i].Strike, pain = 0;
                for (int j = 0; j < rows.Count; j++)
                {
                    var r = rows[j];
                    if (K > r.Strike) pain += r.CallOI * (K - r.Strike);
                    if (K < r.Strike) pain += r.PutOI * (r.Strike - K);
                }
                if (pain < bestPain) { bestPain = pain; bestK = K; }
            }
            return bestK;
        }

        // ----------------------------------------------------- daily-bar path
        /// <summary>
        /// Pull OpenInterest / Volume / settlement Close from the option's DAILY bar.
        /// No real-time subscription is opened, so a futures-only market-data plan
        /// cannot refuse it.
        ///
        /// Results are cached per symbol (daily data barely moves) and any symbol
        /// that fails once goes on a dead-list and is NEVER retried - that is what
        /// stops the request storm that produced hundreds of feed refusals.
        /// </summary>
        bool TryHistory(Symbol os, ref int budget, out double oi, out double vol, out double px)
        {
            oi = vol = px = 0;
            string id = os.Id ?? os.Name ?? "";
            if (id.Length == 0) return false;

            if (_hist.TryGetValue(id, out var c) &&
                (DateTime.UtcNow - c.When).TotalMinutes < Math.Max(1, HistoryCacheMinutes))
            {
                if (!c.Ok) return false;
                oi = c.OI; vol = c.Vol; px = c.Px;
                return true;
            }

            if (_dead.Contains(id)) return false;    // known bad: never ask again
            if (budget <= 0) return false;           // spread the load across rebuilds
            budget--;

            try
            {
                // IMPORTANT: use the DIRECT from/to overload on Symbol.
                // The SymbolExtensions.GetHistory(period, type, itemsCount) helper
                // loops up to TEN times per symbol, widening the window each pass,
                // and routes through symbol mapping - on CQG that becomes a
                // "Continuation bar request is not allowed" error, once per attempt.
                // One symbol therefore produced ~10 feed errors. This is one request.
                var to = Core.Instance.TimeUtils.DateTimeUtcNow;
                var from = to.AddDays(-8);
                using (var hd = os.GetHistory(Period.DAY1, HistoryType.Last, from, to))
                {
                    if (hd != null && hd.Count > 0)
                    {
                        var bar = hd[0, SeekOriginHistory.End] as HistoryItemBar;
                        if (bar != null && (bar.OpenInterest > 0 || bar.Volume > 0 || bar.Close > 0))
                        {
                            oi = Safe(bar.OpenInterest);
                            vol = Safe(bar.Volume);
                            px = Safe(bar.Close);
                            _hist[id] = new Hist { OI = oi, Vol = vol, Px = px, When = DateTime.UtcNow, Ok = true };
                            _histOk++;
                            return true;
                        }
                    }
                }
                // nothing usable came back: blacklist so it is never asked again
                _dead.Add(id);
                _histFail++;
            }
            catch (Exception ex)
            {
                _dead.Add(id);
                _histFail++;
                if (DataNote.Length == 0) DataNote = Short(ex.Message);
            }

            Latch();
            return false;
        }

        /// <summary>
        /// Hard stop. Once the feed has shown it will not serve option data, latch
        /// OFF for the whole session so not one further request is issued. Build()
        /// checks this before it touches the chain at all.
        /// </summary>
        void Latch()
        {
            if (OptionDataDenied) return;
            if (_histOk == 0 && _histFail >= 4)
            {
                OptionDataDenied = true;
                if (DataNote.Length == 0) DataNote = "feed returned no option data";
            }
        }

        /// <summary>
        /// Ask for ONE strike before asking for the rest. If the feed refuses that
        /// single probe, everything stops there - so a denied entitlement costs one
        /// or two log lines instead of hundreds.
        /// </summary>
        bool Probe(List<Symbol> candidates)
        {
            if (_histOk > 0) return true;          // already proven working
            if (OptionDataDenied) return false;
            int budget = 2;
            for (int i = 0; i < candidates.Count && budget > 0; i++)
            {
                if (TryHistory(candidates[i], ref budget, out _, out _, out _)) return true;
            }
            Latch();
            return _histOk > 0;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 110 ? s.Substring(0, 110) + "..." : s;
        }

        // ------------------------------------------------------- subscriptions
        void Resubscribe(List<Symbol> want)
        {
            lock (_subLock)
            {
                var wantSet = new HashSet<string>(want.Select(s => s.Id ?? s.Name ?? ""));
                for (int i = _subscribed.Count - 1; i >= 0; i--)
                {
                    var s = _subscribed[i];
                    string id = s.Id ?? s.Name ?? "";
                    if (!wantSet.Contains(id))
                    {
                        Detach(s);
                        _subscribed.RemoveAt(i);
                    }
                }
                var haveSet = new HashSet<string>(_subscribed.Select(s => s.Id ?? s.Name ?? ""));
                foreach (var s in want)
                {
                    string id = s.Id ?? s.Name ?? "";
                    if (haveSet.Contains(id)) continue;
                    Attach(s);
                    _subscribed.Add(s);
                }
            }
        }

        void Attach(Symbol s)
        {
            try { s.NewQuote += OnQuote; } catch { }
            try { s.NewDayBar += OnDayBar; } catch { }
        }

        void Detach(Symbol s)
        {
            try { s.NewQuote -= OnQuote; } catch { }
            try { s.NewDayBar -= OnDayBar; } catch { }
        }

        void Unsubscribe()
        {
            lock (_subLock)
            {
                foreach (var s in _subscribed) Detach(s);
                _subscribed.Clear();
            }
        }

        void OnQuote(Symbol s, Quote q) { }        // presence drives the subscription
        void OnDayBar(Symbol s, DayBar b) { }      // OI arrives here; next rebuild picks it up

        // -------------------------------------------------------------- helpers
        public static string Label(OptionSerie s)
        {
            if (s == null) return "";
            string d = s.ExpirationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(s.Name) ? d : d + "  " + s.Name;
        }

        /// <summary>Does this option series belong to the instrument on the chart?</summary>
        static bool Belongs(OptionSerie s, Symbol under)
        {
            if (s == null || under == null) return false;
            string uid = under.Id ?? "", root = under.Root ?? "", name = under.Name ?? "";
            string sid = s.UnderlierId ?? "";
            if (sid.Length > 0)
            {
                if (Same(sid, uid) || Same(sid, root) || Same(sid, name)) return true;
                if (root.Length > 1 && sid.IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            // no underlier id on the series: fall back to the root appearing in its name
            string sn = (s.Name ?? "") + " " + (s.Id ?? "");
            return root.Length > 1 && sn.IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Last line of defence before a subscription is opened: the strike symbol
        /// itself must relate to the charted instrument. Blocks the case where a
        /// series slips through but its strikes carry a different product root.
        /// </summary>
        static bool StrikeBelongs(Symbol opt, Symbol under)
        {
            if (opt == null || under == null) return false;
            string root = under.Root ?? "";
            if (root.Length < 2) return true;                 // nothing to test against
            string hay = (opt.Name ?? "") + " " + (opt.Id ?? "") + " " + (opt.UnderlierId ?? "");
            if (hay.IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var u = opt.Underlier;
            if (u != null && Same(u.Root ?? "", root)) return true;
            return false;
        }

        static bool Same(string a, string b) =>
            a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// One-shot dump of what the platform actually returns, so the real roots and
        /// symbol names are visible instead of guessed at.
        /// </summary>
        void Dump(Symbol under, IList<OptionSerie> series)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "TNXOptions_chain_dump.txt");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("TNX OPTIONS - chain dump  " + DateTime.Now);
                sb.AppendLine("UNDERLIER  Id=" + under.Id + "  Name=" + under.Name +
                              "  Root=" + under.Root + "  Exch=" + under.ExchangeId +
                              "  Type=" + under.SymbolType + "  Last=" + under.Last);
                sb.AppendLine("SERIES returned: " + (series?.Count ?? 0));
                if (series != null)
                    foreach (var se in series)
                    {
                        if (se == null) continue;
                        bool ok = Belongs(se, under);
                        sb.AppendLine("  [" + (ok ? "MINE" : "other") + "] exp=" +
                                      se.ExpirationDate.ToString("yyyy-MM-dd") +
                                      "  Name=" + se.Name + "  Id=" + se.Id +
                                      "  UnderlierId=" + se.UnderlierId + "  Type=" + se.SerieType);
                    }
                // sample the strikes of the first matching series
                var first = series?.FirstOrDefault(x => x != null && Belongs(x, under))
                            ?? series?.FirstOrDefault(x => x != null);
                if (first != null)
                {
                    sb.AppendLine("STRIKES of " + first.Name + " (" + first.UnderlierId + "):");
                    var st = Core.Instance.GetStrikes(first);
                    sb.AppendLine("  count=" + (st?.Count ?? 0));
                    if (st != null)
                        foreach (var o in st.Take(12))
                            sb.AppendLine("   " + o.Name + "  Id=" + o.Id + "  K=" + o.StrikePrice +
                                          "  " + o.OptionType + "  bid=" + o.Bid + " ask=" + o.Ask +
                                          " OI=" + o.OpenInterest + " vol=" + o.Volume);
                }
                System.IO.File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        public static double DaysFor(ExpFilter f)
        {
            switch (f)
            {
                case ExpFilter.ZeroDTE: return 1.0;
                case ExpFilter.Max1D: return 1.5;
                case ExpFilter.Max7D: return 7.0;
                case ExpFilter.Max31D: return 31.0;
                case ExpFilter.Max91D: return 91.0;
                default: return 100000.0;
            }
        }

        public static string FilterLabel(ExpFilter f)
        {
            switch (f)
            {
                case ExpFilter.ZeroDTE: return "0DTE";
                case ExpFilter.Max1D: return "<=1d";
                case ExpFilter.Max7D: return "<=7d";
                case ExpFilter.Max31D: return "<=31d";
                case ExpFilter.Max91D: return "<=91d";
                default: return "all";
            }
        }

        static double Safe(double v) => (double.IsNaN(v) || double.IsInfinity(v) || v < 0) ? 0 : v;

        static double Mid(Symbol s)
        {
            double b = s.Bid, a = s.Ask;
            if (!double.IsNaN(b) && !double.IsNaN(a) && b > 0 && a > 0) return 0.5 * (a + b);
            double l = s.Last;
            if (!double.IsNaN(l) && l > 0) return l;
            return 0;
        }

        /// <summary>Current underlying price, preferring last trade then mid.</summary>
        public static double FuturePrice(Symbol u)
        {
            if (u == null) return 0;
            double l = u.Last;
            if (!double.IsNaN(l) && l > 0) return l;
            double b = u.Bid, a = u.Ask;
            if (!double.IsNaN(b) && !double.IsNaN(a) && b > 0 && a > 0) return 0.5 * (a + b);
            return 0;
        }

        /// <summary>
        /// Dollars per index point per contract, derived from the instrument itself:
        /// tickCost / tickSize. ES -> 12.50/0.25 = 50, NQ -> 5.00/0.25 = 20,
        /// MES -> 5, MNQ -> 2. No hardcoded symbol table.
        /// </summary>
        public static double PointValue(Symbol u)
        {
            if (u == null) return 1;
            try
            {
                double ts = u.TickSize;
                double tc = u.GetTickCost(u.Last > 0 ? u.Last : 1.0);
                if (ts > 0 && tc > 0)
                {
                    double pv = tc / ts;
                    if (pv > 0 && pv < 100000) return pv;
                }
            }
            catch { }
            try { if (u.LotSize > 0) return u.LotSize; } catch { }
            return 1;
        }

        // --------------------------------------------------------- formatting
        public static string Money(double v)
        {
            double a = Math.Abs(v);
            string sign = v < 0 ? "-" : "";
            if (a >= 1e12) return sign + (a / 1e12).ToString("0.##", CultureInfo.InvariantCulture) + "T";
            if (a >= 1e9) return sign + (a / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (a >= 1e6) return sign + (a / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (a >= 1e3) return sign + (a / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return sign + a.ToString("0.#", CultureInfo.InvariantCulture);
        }

        public static string Num(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1e6) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (a >= 1e3) return (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return v.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
