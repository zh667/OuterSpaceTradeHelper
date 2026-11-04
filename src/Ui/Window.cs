using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TradeHelper.Ui
{
    public static class Fmt
    {
        public const double AuPerLy = 63241.077;

        public static string Money(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "?";
            double a = Math.Abs(v);
            if (a >= 1e8) return (v / 1e8).ToString("0.##") + "亿";
            if (a >= 1e4) return (v / 1e4).ToString("0.##") + "万";
            return v.ToString("0");
        }

        public static int Unit; // Config.distanceUnit

        // Route distance between galaxies. AU by default; unit 1 = the game's route panels (UIUntils.distanceAu,
        // which switches to light years above 63,241 AU).
        public static string Dist(double au)
        {
            if (au < 0) return "?";
            if (au == 0) return "同星系";
            if (Unit == 1)
            {
                try { return UIUntils.distanceAu(au); } catch { }
            }
            return au >= 1000 ? au.ToString("#,0") + " AU" : au.ToString("0.##") + " AU";
        }

        public static Color FreshColor(MarketInfo m) => !m.initialized ? Theme.Text2 : m.pendingDays == 0 ? Theme.Good : Theme.Warn;
    }

    // The panel: game-styled window with tabs. Data comes from Snapshot objects built in the background.
    public class Window
    {
        readonly TradeHelperBehaviour host;
        readonly Config cfg;
        public GameObject canvasGo;
        RectTransform win, body, popupLayer;
        TextMeshProUGUI status, title;
        readonly List<Kit.Tab> tabButtons = new List<Kit.Tab>();
        readonly Dictionary<int, IView> views = new Dictionary<int, IView>();
        int tab;
        public Snapshot snap;
        static readonly string[] TabNames = { "跑商建议", "跑商计划", "卖库存", "各站市场", "我的库存", "后台补算", "设置" };
        public const float W = 1560, H = 860, BodyY = 112, Pad = 18;
        public float BodyW => W - Pad * 2;
        public float BodyH => H - BodyY - Pad;

        public interface IView
        {
            void Show(bool on);
            void OnSnapshot(Snapshot s);
            void Tick();
        }

        public Window(TradeHelperBehaviour host, Config cfg) { this.host = host; this.cfg = cfg; }

        public bool Visible => canvasGo != null && canvasGo.activeSelf;

        public void Build()
        {
            Fmt.Unit = cfg.distanceUnit;
            Theme.Load();
            canvasGo = new GameObject("TradeHelperCanvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            var rootRt = (RectTransform)canvasGo.transform;
            win = Kit.New("Window", rootRt);
            win.anchorMin = win.anchorMax = new Vector2(0.5f, 0.5f);
            win.pivot = new Vector2(0.5f, 0.5f);
            win.sizeDelta = new Vector2(W, H);
            win.anchoredPosition = Vector2.zero;
            Kit.Img(win, Theme.Window, Theme.GameSprites ? Color.white : Theme.Bg, true);
            var tint = Kit.Stretch(Kit.New("Tint", win), 2, 2, 2, 2);
            Kit.Img(tint, Theme.White, Theme.Hex(0x1C1B19, 0.55f));

            var titleBar = Kit.Place(Kit.New("TitleBar", win), 0, 0, W, 52);
            Kit.Img(titleBar, Theme.White, new Color(0, 0, 0, 0.0f), true);
            var drag = titleBar.gameObject.AddComponent<DragHandle>();
            drag.target = win; drag.canvas = canvas;
            var accent = Kit.Place(Kit.New("Accent", win), Pad, 16, 6, 22);
            Kit.Img(accent, Theme.White, Theme.Accent);
            title = Kit.TxtAt(win, "跑商助手  <size=15><color=#A4A5A6>" + TradeHelperBehaviour.Version + "  ·  " + cfg.hotkey + " 开关  ·  拖动标题栏移动</color></size>", Pad + 16, 10, 900, 34, 24, Theme.Text);
            Kit.Button(win, "刷新", W - Pad - 46 - 12 - 110, 12, 110, 32, () => host.RequestRefresh(true), true);
            Kit.Button(win, "截图反馈", W - Pad - 46 - 12 - 110 - 8 - 110, 12, 110, 32, () => host.Screenshot());
            var close = Kit.Place(Kit.New("Close", win), W - Pad - 40, 10, 38, 38);
            var cimg = Kit.Img(close, Theme.Close ?? Theme.White, Theme.Close != null ? Color.white : Theme.Bad, true);
            var cb = close.gameObject.AddComponent<Button>();
            cb.targetGraphic = cimg;
            cb.onClick.AddListener(() => host.SetVisible(false));

            for (int i = 0; i < TabNames.Length; i++)
            {
                int ti = i;
                tabButtons.Add(Kit.TabButton(win, TabNames[i], Pad + i * 122, 58, 116, 36, () => SelectTab(ti)));
            }
            status = Kit.TxtAt(win, "", Pad + TabNames.Length * 122 + 12, 56, W - Pad * 2 - TabNames.Length * 122 - 12, 34, 15, Theme.Text2, TextAlignmentOptions.MidlineRight);
            var sep = Kit.Place(Kit.New("Sep", win), Pad, 98, W - Pad * 2, 1);
            Kit.Img(sep, Theme.White, Theme.Line);

            body = Kit.Place(Kit.New("Body", win), Pad, BodyY, BodyW, BodyH);
            popupLayer = Kit.Stretch(Kit.New("PopupLayer", win));
            Kit.Drop.CloseAll = () => { foreach (var v in views.Values) (v as ViewBase)?.CloseDrops(); };
            SelectTab(0);
        }

        public RectTransform PopupLayer => popupLayer;

        void SelectTab(int t)
        {
            Kit.Drop.CloseAll?.Invoke();
            tab = t;
            for (int i = 0; i < tabButtons.Count; i++) tabButtons[i].Selected = i == t;
            if (!views.ContainsKey(t))
            {
                IView v = null;
                switch (t)
                {
                    case 0: v = new TradesView(this, body, cfg, host); break;
                    case 1: v = new PlansView(this, body, cfg, host); break;
                    case 2: v = new SellsView(this, body, cfg, host); break;
                    case 3: v = new MarketsView(this, body, cfg, host); break;
                    case 4: v = new InventoryView(this, body, cfg, host); break;
                    case 5: v = new SettlementView(this, body, cfg, host); break;
                    case 6: v = new SettingsView(this, body, cfg, host); break;
                }
                views[t] = v;
                if (snap != null) v.OnSnapshot(snap);
            }
            foreach (var kv in views) kv.Value.Show(kv.Key == t);
        }

        public void OnSnapshot(Snapshot s)
        {
            snap = s;
            foreach (var v in views.Values) v.OnSnapshot(s);
        }

        // Plans were added/removed outside the plans page: refresh tables that show tracking state.
        public void RefreshViews()
        {
            if (snap != null) foreach (var v in views.Values) v.OnSnapshot(snap);
        }

        public void SetStatus(string s) { if (status != null) status.text = s; }

        public void Tick()
        {
            IView v;
            if (views.TryGetValue(tab, out v)) v.Tick();
        }
    }

    public abstract class ViewBase : Window.IView
    {
        protected readonly Window w;
        protected readonly Config cfg;
        protected readonly TradeHelperBehaviour host;
        protected readonly RectTransform root;
        protected readonly List<Kit.Drop> drops = new List<Kit.Drop>();
        protected Snapshot snap;

        protected ViewBase(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host)
        {
            this.w = w; this.cfg = cfg; this.host = host;
            root = Kit.Stretch(Kit.New(GetType().Name, body));
        }

        protected Kit.Drop Drop(string prefix, float x, float y, float width, List<string> opts, int sel, Action<int> changed)
        {
            var d = Kit.Dropdown(root, w.PopupLayer, prefix, x, y, width, opts, sel, changed);
            drops.Add(d);
            return d;
        }

        public void CloseDrops() { foreach (var d in drops) d.Close(); }
        public virtual void Show(bool on) { root.gameObject.SetActive(on); if (!on) CloseDrops(); }
        public virtual void OnSnapshot(Snapshot s) { snap = s; }
        public virtual void Tick() { }

        protected static List<string> Categories(Snapshot s, IEnumerable<string> present)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var c in present) if (!string.IsNullOrEmpty(c)) set.Add(c);
            var l = new List<string> { "全部类别" };
            l.AddRange(set);
            return l;
        }

        protected static bool Match(string search, params string[] fields)
        {
            if (string.IsNullOrEmpty(search)) return true;
            foreach (var f in fields) if (f != null && f.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        protected static readonly float[] LyLimits = { 0, -1, 1, 3, 5, 10, 20, 40 };
        protected static readonly List<string> LyOptions = new List<string> { "不限", "同星系", "≤ 1 ly", "≤ 3 ly", "≤ 5 ly", "≤ 10 ly", "≤ 20 ly", "≤ 40 ly" };
        protected static int LyIndex(float v) { for (int i = 0; i < LyLimits.Length; i++) if (Math.Abs(LyLimits[i] - v) < 0.001f) return i; return 0; }

        protected static bool WithinLy(double au, float limit)
        {
            if (limit == 0) return true;
            if (au < 0) return false;
            if (limit < 0) return au == 0;
            return au / Fmt.AuPerLy <= limit;
        }
    }
}
