using System;
using System.Collections.Generic;
using UnityEngine;

namespace TradeHelper
{
    public class TradeHelperBehaviour : MonoBehaviour
    {
        public const string Version = "0.1.0";
        public static TradeHelperBehaviour Instance;

        Config cfg;
        readonly Settlement settlement = new Settlement();
        Snapshot snap;
        List<TradeIdea> ideas = new List<TradeIdea>();
        List<SellIdea> sells = new List<SellIdea>();
        bool visible;
        int tab;
        Rect win = new Rect(40, 40, 1180, 680);
        Vector2 scroll, scroll2;
        int marketIndex;
        string filter = "";
        string status = "";
        float nextAutoRefresh, nextExport;
        int lastQueueCount;
        Font font;
        GUIStyle small, bold, cell, header;

        static readonly string[] Tabs = { "跑商建议", "卖库存", "各站市场", "我的库存", "后台补算", "设置" };

        void Awake()
        {
            Instance = this;
            cfg = Config.Load();
            nextExport = Time.unscaledTime + 30f;
        }

        void Update()
        {
            try
            {
                settlement.Tick(cfg);
                // When a background catch-up batch finishes, refresh the open panel so prices become exact.
                if (visible && lastQueueCount > 0 && settlement.Pending == 0) Refresh();
                lastQueueCount = settlement.Pending;
                if (visible && Time.unscaledTime >= nextAutoRefresh) Refresh();
                if (cfg.exportEverySeconds > 0 && Time.unscaledTime >= nextExport)
                {
                    nextExport = Time.unscaledTime + cfg.exportEverySeconds;
                    if (MarketScanner.Game != null) { if (!visible) Refresh(); SafeExport(); }
                }
            }
            catch (Exception e) { status = "出错: " + e.Message; Debug.LogWarning("[TradeHelper] update: " + e); }
        }

        void Refresh()
        {
            nextAutoRefresh = Time.unscaledTime + 15f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                snap = MarketScanner.Take();
                ideas = Advisor.Arbitrage(snap, cfg);
                sells = Advisor.SellOwnStock(snap, cfg);
                if (marketIndex >= snap.markets.Count) marketIndex = 0;
                status = "已刷新 " + snap.takenAt.ToString("HH:mm:ss") + "（" + sw.ElapsedMilliseconds + " 毫秒）" + (snap.errors.Count > 0 ? "  （" + snap.errors.Count + " 条读取问题，见设置页）" : "");
            }
            catch (Exception e) { status = "刷新失败: " + e.Message; Debug.LogWarning("[TradeHelper] refresh: " + e); }
        }

        void SafeExport()
        {
            try
            {
                if (snap == null) return;
                var p = Export.Write(snap, ideas, sells);
                status = "已导出 " + p;
            }
            catch (Exception e) { status = "导出失败: " + e.Message; }
        }

        void EnsureStyles()
        {
            if (font == null)
            {
                try { font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC", "Arial" }, 14); }
                catch { }
            }
            if (cell != null) return;
            cell = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false, clipping = TextClipping.Clip };
            small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            bold = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            header = new GUIStyle(cell) { fontStyle = FontStyle.Bold };
        }

        void OnGUI()
        {
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == cfg.HotkeyCode)
            {
                visible = !visible;
                if (visible) Refresh();
                e.Use();
            }
            if (!visible) return;
            if (font != null) GUI.skin.font = font;
            EnsureStyles();
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
            win = GUI.Window(0x7A11, win, DrawWindow, "跑商助手 " + Version + "（" + cfg.hotkey + " 开关）");
        }

        static string M(double v)
        {
            double a = Math.Abs(v);
            if (a >= 10000) return (v / 10000).ToString("0.0") + "万";
            return v.ToString("0");
        }

        static string D(float d) => d < 0 ? "?" : d.ToString("0");

        void Cell(string s, float w, GUIStyle st = null) => GUILayout.Label(s, st ?? cell, GUILayout.Width(w));

        void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            int nt = GUILayout.Toolbar(tab, Tabs, GUILayout.Height(26));
            if (nt != tab) { tab = nt; scroll = Vector2.zero; }
            if (GUILayout.Button("刷新", GUILayout.Width(60), GUILayout.Height(26))) Refresh();
            if (GUILayout.Button("关闭", GUILayout.Width(60), GUILayout.Height(26))) visible = false;
            GUILayout.EndHorizontal();
            if (snap == null) { GUILayout.Label("还没有数据，点“刷新”。"); GUI.DragWindow(); return; }
            GUILayout.Label("信用点 " + M(snap.credits) + "    舰队所在：" + Name(snap.fleetGalaxyId) + "    " + status, small);
            try
            {
                switch (tab)
                {
                    case 0: DrawTrades(); break;
                    case 1: DrawSells(); break;
                    case 2: DrawMarkets(); break;
                    case 3: DrawInventory(); break;
                    case 4: DrawSettlement(); break;
                    case 5: DrawSettings(); break;
                }
            }
            catch (Exception ex) { GUILayout.Label("界面出错: " + ex.Message); }
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        string Name(string galaxyId)
        {
            if (galaxyId == null) return "?";
            string n;
            return snap.galaxyName.TryGetValue(galaxyId, out n) ? n : galaxyId;
        }

        void DrawTrades()
        {
            GUILayout.BeginHorizontal();
            bool only = GUILayout.Toggle(cfg.onlyFreshInAdvice, "只用实时站（欠 ≤ " + cfg.freshDays + " 天）");
            if (only != cfg.onlyFreshInAdvice) { cfg.onlyFreshInAdvice = only; cfg.Save(); Refresh(); }
            GUILayout.Label("“欠N天”的站价格是估的：到站一打开就会补算，库存可能已经变了。数量按价格开始倒亏为止。违禁品已排除。", small);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Cell("商品", 150, header); Cell("在哪买", 190, header); Cell("买几个", 56, header); Cell("成本(含税)", 80, header);
            Cell("去哪卖", 190, header); Cell("到手", 80, header); Cell("净赚", 80, header); Cell("两站距离", 64, header); Cell("舰队→买站", 70, header);
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var t in ideas)
            {
                GUILayout.BeginHorizontal();
                Cell(t.name, 150);
                Cell(t.buy.market.name + " [" + t.buy.market.Freshness + "]", 190);
                Cell(t.qty.ToString(), 56);
                Cell(M(t.cost), 80);
                Cell(t.sell.market.name + " [" + t.sell.market.Freshness + "]", 190);
                Cell(M(t.revenue), 80);
                Cell(M(t.Profit), 80);
                Cell(D(t.legDistance), 64);
                Cell(D(t.fleetDistance), 70);
                GUILayout.EndHorizontal();
            }
            if (ideas.Count == 0) GUILayout.Label("没有找到扣税后能赚钱的组合。");
            GUILayout.EndScrollView();
        }

        void DrawSells()
        {
            GUILayout.Label("你的库存（基地、母舰、舰队货仓、DNA 库合计）卖到哪最值钱。每种卖到单价跌到首件一半为止；记得货要先运到能卖的地方。", small);
            GUILayout.BeginHorizontal();
            Cell("物品", 150, header); Cell("在哪", 220, header); Cell("持有", 70, header); Cell("建议卖", 64, header);
            Cell("卖到", 210, header); Cell("约得", 80, header); Cell("首件到手", 80, header); Cell("舰队→站", 64, header);
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var s in sells)
            {
                GUILayout.BeginHorizontal();
                Cell(s.name, 150); Cell(s.location, 220); Cell(s.have.ToString(), 70); Cell(s.qty.ToString(), 64);
                Cell(s.market.market.name + " [" + s.market.market.Freshness + "]", 210);
                Cell(M(s.revenue), 80); Cell(M(s.firstUnitNet), 80); Cell(D(s.fleetDistance), 64);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        void DrawMarkets()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(300));
            scroll2 = GUILayout.BeginScrollView(scroll2, GUILayout.Width(300));
            for (int i = 0; i < snap.markets.Count; i++)
            {
                var m = snap.markets[i];
                string label = m.name + "  " + m.Freshness + "  税" + (m.tax * 100).ToString("0") + "%";
                if (GUILayout.Toggle(i == marketIndex, label, "Button")) { if (marketIndex != i) { marketIndex = i; scroll = Vector2.zero; } }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            if (snap.markets.Count > 0)
            {
                var m = snap.markets[marketIndex];
                GUILayout.BeginHorizontal();
                GUILayout.Label(m.name + "  " + m.Freshness + "  距舰队 " + D(snap.Distance(snap.fleetGalaxyId, m.galaxyId)), bold);
                GUILayout.Label("搜索", GUILayout.Width(36));
                filter = GUILayout.TextField(filter ?? "", GUILayout.Width(140));
                GUILayout.EndHorizontal();
                if (!m.initialized) GUILayout.Label("这个站还没开过交易所，没有库存数据。可以在“后台补算”页做首次初始化。");
                GUILayout.BeginHorizontal();
                Cell("商品", 170, header); Cell("库存", 70, header); Cell("买 1 个(含税)", 100, header); Cell("卖 1 个(到手)", 100, header);
                Cell("税", 44, header); Cell("能买", 40, header); Cell("违禁", 40, header); Cell("价格", 50, header);
                GUILayout.EndHorizontal();
                scroll = GUILayout.BeginScrollView(scroll);
                foreach (var q in m.goods)
                {
                    if (!string.IsNullOrEmpty(filter) && (q.name == null || q.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    GUILayout.BeginHorizontal();
                    Cell(q.name, 170); Cell(q.stock.ToString(), 70);
                    Cell(M(q.BuyUnit(0) * (1 + q.tax)), 100); Cell(M(q.SellUnit(0) * (1 - q.tax)), 100);
                    Cell((q.tax * 100).ToString("0") + "%", 44); Cell(q.buyable ? "是" : "-", 40); Cell(q.contraband ? "违禁" : "", 40);
                    Cell(q.exact ? "实时" : "估", 50);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        void DrawInventory()
        {
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var loc in snap.inventory)
            {
                GUILayout.Label(loc.name + "（" + loc.items.Count + " 种）", bold);
                var keys = new List<KeyValuePair<string, int>>(loc.items);
                keys.Sort((a, b) => b.Value.CompareTo(a.Value));
                int col = 0;
                GUILayout.BeginHorizontal();
                foreach (var kv in keys)
                {
                    Cell(MarketScanner.ItemName(snap, kv.Key) + " × " + kv.Value, 220);
                    if (++col % 5 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        void DrawSettlement()
        {
            GUILayout.Label("后台补算 = 让游戏把这个站欠着的市场天数算完（和你打开那个站的交易所效果一样，会改变它的库存和价格）。" +
                            "勾“保持实时”的站以后每次欠天都会自动补；“首次初始化”用于从没开过的站，可能卡一下。", small);
            GUILayout.Label("队列 " + settlement.Pending + " 个站，已补算 " + settlement.daysDoneTotal + " 天" +
                            (string.IsNullOrEmpty(settlement.lastError) ? "" : "    最近错误：" + settlement.lastError), small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("补算所有“保持实时”的站", GUILayout.Width(200)))
                foreach (var m in snap.markets) if (cfg.keepFresh.Contains(m.uid) && m.initialized) settlement.Enqueue(m.uid);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Cell("站", 260, header); Cell("状态", 80, header); Cell("距舰队", 60, header); Cell("", 100, header); Cell("保持实时", 80, header);
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);
            var order = new List<MarketInfo>(snap.markets);
            order.Sort((a, b) => snap.Distance(snap.fleetGalaxyId, a.galaxyId).CompareTo(snap.Distance(snap.fleetGalaxyId, b.galaxyId)));
            foreach (var m in order)
            {
                GUILayout.BeginHorizontal();
                Cell(m.name, 260);
                Cell(settlement.IsQueued(m.uid) ? "补算中…" : m.Freshness, 80);
                Cell(D(snap.Distance(snap.fleetGalaxyId, m.galaxyId)), 60);
                if (m.initialized)
                {
                    GUI.enabled = m.pendingDays > 0 && !settlement.IsQueued(m.uid);
                    if (GUILayout.Button("立即补算", GUILayout.Width(100))) settlement.Enqueue(m.uid);
                    GUI.enabled = true;
                    bool keep = cfg.keepFresh.Contains(m.uid);
                    bool nk = GUILayout.Toggle(keep, "", GUILayout.Width(80));
                    if (nk != keep)
                    {
                        if (nk) { cfg.keepFresh.Add(m.uid); settlement.Enqueue(m.uid); } else cfg.keepFresh.Remove(m.uid);
                        cfg.Save();
                    }
                }
                else
                {
                    if (GUILayout.Button("首次初始化", GUILayout.Width(100))) InitializeMarket(m);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        void InitializeMarket(MarketInfo m)
        {
            try
            {
                // Same call the trade UI makes when the station's exchange is opened: settles and builds the store.
                m.ex.GetItemsForUse();
                status = m.name + " 已初始化";
            }
            catch (Exception e) { status = m.name + " 初始化失败: " + e.Message; Debug.LogWarning("[TradeHelper] init " + m.uid + ": " + e); }
            Refresh();
        }

        string hotkeyEdit;
        void DrawSettings()
        {
            if (hotkeyEdit == null) hotkeyEdit = cfg.hotkey;
            GUILayout.BeginHorizontal();
            GUILayout.Label("开关热键（Unity KeyCode 名，如 F8、F9、Insert）", GUILayout.Width(320));
            hotkeyEdit = GUILayout.TextField(hotkeyEdit, GUILayout.Width(100));
            if (GUILayout.Button("保存", GUILayout.Width(60))) { cfg.hotkey = hotkeyEdit; cfg.Save(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("“只用实时站”允许欠几天：" + cfg.freshDays, GUILayout.Width(320));
            if (GUILayout.Button("-", GUILayout.Width(30)) && cfg.freshDays > 0) { cfg.freshDays--; cfg.Save(); }
            if (GUILayout.Button("+", GUILayout.Width(30))) { cfg.freshDays++; cfg.Save(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("后台补算每帧最多 " + cfg.daysPerFrame + " 天 / " + cfg.msPerFrame.ToString("0") + " 毫秒", GUILayout.Width(320));
            if (GUILayout.Button("-", GUILayout.Width(30)) && cfg.daysPerFrame > 1) { cfg.daysPerFrame--; cfg.Save(); }
            if (GUILayout.Button("+", GUILayout.Width(30)) && cfg.daysPerFrame < 30) { cfg.daysPerFrame++; cfg.Save(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("自动导出间隔：" + (cfg.exportEverySeconds <= 0 ? "关" : cfg.exportEverySeconds.ToString("0") + " 秒"), GUILayout.Width(320));
            if (GUILayout.Button("关", GUILayout.Width(40))) { cfg.exportEverySeconds = 0; cfg.Save(); }
            if (GUILayout.Button("5 分钟", GUILayout.Width(60))) { cfg.exportEverySeconds = 300; cfg.Save(); }
            if (GUILayout.Button("立即导出", GUILayout.Width(80))) SafeExport();
            GUILayout.EndHorizontal();
            GUILayout.Label("数据文件：" + System.IO.Path.Combine(Config.Dir, "latest.json"), small);
            GUILayout.Label("读取问题：", bold);
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var err in snap.errors) GUILayout.Label(err, small);
            GUILayout.EndScrollView();
        }
    }
}
