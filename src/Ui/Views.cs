using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace TradeHelper.Ui
{
    public class TradesView : ViewBase
    {
        readonly Table<TradeIdea> table;
        readonly Kit.Drop cat, sort;
        string search = "";
        string category;
        static readonly int[] StaleOpts = { 0, 3, 10, 99999 };
        static readonly float[] ProfitOpts = { 0, 1e4f, 1e5f, 5e5f, 1e6f, 5e6f };
        // (column, descending) presets shown in the 排序 dropdown, like the game's sort menus.
        static readonly int[] SortCol = { 7, 11, 8, 6, 4, 10, 9 };
        static readonly bool[] SortDesc = { true, true, true, true, false, false, false };

        static double PerLy(TradeIdea t)
        {
            double ly = t.TotalAu < 0 ? -1 : t.TotalAu / Fmt.AuPerLy;
            if (ly < 0) return double.NaN;
            return t.Profit / Math.Max(ly, 0.1);
        }

        public TradesView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            float x = 0;
            Drop("数据：", x, 0, 210, new List<string> { "只用实时站", "允许欠 ≤3 天（估）", "允许欠 ≤10 天（估）", "全部（含估算）" },
                Array.IndexOf(StaleOpts, cfg.maxStaleDays) < 0 ? 0 : Array.IndexOf(StaleOpts, cfg.maxStaleDays),
                i => { cfg.maxStaleDays = StaleOpts[i]; cfg.Save(); host.RequestRefresh(true); });
            x += 218;
            Drop("舰队到买入站：", x, 0, 230, LyOptions, LyIndex(cfg.maxFleetLy), i => { cfg.maxFleetLy = LyLimits[i]; cfg.Save(); table.Resort(); });
            x += 238;
            var pIdx = Array.IndexOf(ProfitOpts, cfg.minProfit);
            Drop("最低净赚：", x, 0, 190, new List<string> { "不限", "1 万", "10 万", "50 万", "100 万", "500 万" }, pIdx < 0 ? 0 : pIdx, i => { cfg.minProfit = ProfitOpts[i]; cfg.Save(); table.Resort(); });
            x += 198;
            cat = Drop("", x, 0, 170, new List<string> { "全部类别" }, 0, i => { category = i == 0 ? null : cat.options[i]; table.Resort(); });
            x += 178;
            Kit.Input(root, "搜索商品或站名", x, 0, 220, 30, s => { search = s; table.Resort(); });
            x += 228;
            sort = Drop("排序：", x, 0, w.BodyW - x, new List<string> { "净赚从高到低", "每光年净赚从高到低", "利润率从高到低", "到手从高到低", "成本从低到高", "舰队距离从近到远", "航程从近到远" }, 0,
                i => { table.sortCol = SortCol[i]; table.desc = SortDesc[i]; table.Resort(); SaveSort(); });
            Kit.TxtAt(root, "按两边的税和买卖时的价格变化算到“再多买就开始亏”为止。违禁品不出现（游戏不让买卖）。“估”= 那个站还欠结算天数，到站一打开价格会变，可在“后台补算”页先补。", 0, 34, w.BodyW, 22, 14, Theme.Text2);

            var cols = new List<Column<TradeIdea>>
            {
                new Column<TradeIdea> { title = "商品", width = 165, text = t => t.name, key = t => t.name },
                new Column<TradeIdea> { title = "类别", width = 80, text = t => t.category, key = t => t.category, color = _ => Theme.Text2 },
                new Column<TradeIdea> { title = "在哪买", width = 205, text = t => t.buy.market.name, key = t => t.buy.market.name, color = t => Fmt.FreshColor(t.buy.market) },
                new Column<TradeIdea> { title = "买几个", width = 64, text = t => t.qty.ToString(), key = t => t.qty, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "成本(含税)", width = 105, text = t => Fmt.Money(t.cost), key = t => t.cost, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "去哪卖", width = 205, text = t => t.sell.market.name, key = t => t.sell.market.name, color = t => Fmt.FreshColor(t.sell.market) },
                new Column<TradeIdea> { title = "到手(扣税)", width = 105, text = t => Fmt.Money(t.revenue), key = t => t.revenue, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "净赚", width = 105, text = t => Fmt.Money(t.Profit), key = t => t.Profit, numeric = true, align = TextAlignmentOptions.MidlineRight, color = _ => Theme.Accent },
                new Column<TradeIdea> { title = "利润率", width = 76, text = t => (t.Margin * 100).ToString("0") + "%", key = t => t.Margin, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "买→卖 航程", width = 112, text = t => Fmt.Dist(t.legAu), key = t => t.legAu < 0 ? (IComparable)null : t.legAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "舰队→买入站", width = 112, text = t => Fmt.Dist(t.fleetAu), key = t => t.fleetAu < 0 ? (IComparable)null : t.fleetAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "每光年净赚", width = 105, text = t => { var v = PerLy(t); return double.IsNaN(v) ? "?" : Fmt.Money(v); }, key = t => { var v = PerLy(t); return double.IsNaN(v) ? (IComparable)null : v; }, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "数据", width = 72, text = t => t.Stale ? "估·欠" + t.MaxPending + "天" : "实时", key = t => t.MaxPending, numeric = true, color = t => t.Stale ? Theme.Warn : Theme.Good },
            };
            table = new Table<TradeIdea>(root, 0, 60, w.BodyW, w.BodyH - 60, cols, cfg.tradesSort, cfg.tradesDesc);
            table.emptyText = "没有扣税后能赚钱的组合（或都被筛掉了）。可以放宽“数据”或先在“后台补算”页补算更多站。";
            table.filter = t => WithinLy(t.fleetAu, cfg.maxFleetLy) && t.Profit >= cfg.minProfit && (category == null || t.category == category) && Match(search, t.name, t.buy.market.name, t.sell.market.name);
            table.sortChanged = (c, d) => SaveSort();
        }

        void SaveSort() { cfg.tradesSort = table.sortCol; cfg.tradesDesc = table.desc; cfg.Save(); }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            var cats = new List<string>();
            foreach (var t in s.trades) cats.Add(t.category);
            var l = Categories(s, cats);
            cat.SetOptions(l, category == null ? 0 : Math.Max(0, l.IndexOf(category)));
            if (cat.index == 0) category = null;
            table.SetData(s.trades);
        }
    }

    public class SellsView : ViewBase
    {
        readonly Table<SellIdea> table;
        readonly Kit.Drop cat;
        string search = "", category;
        static readonly int[] SortCol = { 6, 7, 8, 3 };
        static readonly bool[] SortDesc = { true, true, false, true };

        public SellsView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            float x = 0;
            Drop("舰队到卖出站：", x, 0, 230, LyOptions, LyIndex(cfg.maxFleetLy), i => { cfg.maxFleetLy = LyLimits[i]; cfg.Save(); table.Resort(); });
            x += 238;
            cat = Drop("", x, 0, 170, new List<string> { "全部类别" }, 0, i => { category = i == 0 ? null : cat.options[i]; table.Resort(); });
            x += 178;
            Kit.Input(root, "搜索物品、地点或站名", x, 0, 240, 30, s => { search = s; table.Resort(); });
            x += 248;
            Drop("排序：", x, 0, 260, new List<string> { "到手从高到低", "首件到手从高到低", "舰队距离从近到远", "持有从多到少" }, 0,
                i => { table.sortCol = SortCol[i]; table.desc = SortDesc[i]; table.Resort(); SaveSort(); });
            Kit.TxtAt(root, "你的母舰、基地、舰队货仓和 DNA 库的东西卖到哪最值钱（只用“跑商建议”页选定的数据范围）。每种卖到单价跌到首件一半为止；货要先运到能卖的地方。", 0, 34, w.BodyW, 22, 14, Theme.Text2);
            var cols = new List<Column<SellIdea>>
            {
                new Column<SellIdea> { title = "物品", width = 170, text = s => s.name, key = s => s.name },
                new Column<SellIdea> { title = "类别", width = 80, text = s => s.category, key = s => s.category, color = _ => Theme.Text2 },
                new Column<SellIdea> { title = "现在在哪", width = 270, text = s => s.location, key = s => s.location, color = _ => Theme.Text2 },
                new Column<SellIdea> { title = "持有", width = 90, text = s => s.have.ToString(), key = s => s.have, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<SellIdea> { title = "建议卖", width = 80, text = s => s.qty.ToString(), key = s => s.qty, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<SellIdea> { title = "卖到", width = 230, text = s => s.market.market.name, key = s => s.market.market.name, color = s => Fmt.FreshColor(s.market.market) },
                new Column<SellIdea> { title = "到手(扣税)", width = 120, text = s => Fmt.Money(s.revenue), key = s => s.revenue, numeric = true, align = TextAlignmentOptions.MidlineRight, color = _ => Theme.Accent },
                new Column<SellIdea> { title = "首件到手", width = 110, text = s => Fmt.Money(s.firstUnitNet), key = s => s.firstUnitNet, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<SellIdea> { title = "舰队→卖出站", width = 130, text = s => Fmt.Dist(s.fleetAu), key = s => s.fleetAu < 0 ? (IComparable)null : s.fleetAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<SellIdea> { title = "数据", width = 90, text = s => s.market.market.Exact ? "实时" : "估·欠" + s.market.market.pendingDays + "天", key = s => s.market.market.pendingDays, numeric = true, color = s => s.market.market.Exact ? Theme.Good : Theme.Warn },
            };
            table = new Table<SellIdea>(root, 0, 60, w.BodyW, w.BodyH - 60, cols, cfg.sellsSort, cfg.sellsDesc);
            table.filter = s => WithinLy(s.fleetAu, cfg.maxFleetLy) && (category == null || s.category == category) && Match(search, s.name, s.location, s.market.market.name);
            table.sortChanged = (c, d) => SaveSort();
        }

        void SaveSort() { cfg.sellsSort = table.sortCol; cfg.sellsDesc = table.desc; cfg.Save(); }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            var cats = new List<string>();
            foreach (var t in s.sells) cats.Add(t.category);
            var l = Categories(s, cats);
            cat.SetOptions(l, category == null ? 0 : Math.Max(0, l.IndexOf(category)));
            if (cat.index == 0) category = null;
            table.SetData(s.sells);
        }
    }

    public class MarketsView : ViewBase
    {
        readonly Table<MarketInfo> markets;
        readonly Table<GoodsQuote> goods;
        readonly Kit.Drop cat;
        readonly TextMeshProUGUI head;
        MarketInfo selected;
        long selectedUid = -1;
        string search = "", category;
        static readonly int[] SortCol = { 0, 2, 3, 4, 5 };
        static readonly bool[] SortDesc = { false, true, false, true, false };

        public MarketsView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            const float LW = 500;
            var mcols = new List<Column<MarketInfo>>
            {
                new Column<MarketInfo> { title = "站", width = 220, text = m => m.name, key = m => m.name },
                new Column<MarketInfo> { title = "数据", width = 78, text = m => m.Freshness, key = m => m.initialized ? m.pendingDays : 100000, numeric = true, color = Fmt.FreshColor },
                new Column<MarketInfo> { title = "税", width = 56, text = m => (m.tax * 100).ToString("0") + "%", key = m => m.tax, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<MarketInfo> { title = "距舰队", width = 130, text = m => Fmt.Dist(m.fleetAu), key = m => m.fleetAu < 0 ? (IComparable)null : m.fleetAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
            };
            markets = new Table<MarketInfo>(root, 0, 40, LW, w.BodyH - 40, mcols, cfg.marketsSort, cfg.marketsDesc);
            markets.onRowClick = m => { selected = m; selectedUid = m.uid; ShowGoods(); };
            markets.isSelected = m => m.uid == selectedUid;
            markets.sortChanged = (c, d) => { cfg.marketsSort = c; cfg.marketsDesc = d; cfg.Save(); };
            Kit.TxtAt(root, "站点（点击查看货物）", 0, 0, LW, 30, 17, Theme.Text);

            float gx = LW + 16, gw = w.BodyW - gx;
            float x = gx;
            cat = Drop("", x, 0, 150, new List<string> { "全部类别" }, 0, i => { category = i == 0 ? null : cat.options[i]; goods.Resort(); });
            x += 158;
            Kit.Input(root, "搜索货物", x, 0, 170, 30, s => { search = s; goods.Resort(); });
            x += 178;
            Kit.Toggle(root, "只看能买", x, 1, 100, cfg.goodsBuyableOnly, v => { cfg.goodsBuyableOnly = v; cfg.Save(); goods.Resort(); });
            x += 104;
            Kit.Toggle(root, "隐藏违禁", x, 1, 100, cfg.hideContraband, v => { cfg.hideContraband = v; cfg.Save(); goods.Resort(); });
            x += 104;
            Drop("排序：", x, 0, gx + gw - x, new List<string> { "名称", "库存从多到少", "买价从低到高", "卖价从高到低", "税从低到高" }, 0,
                i => { goods.sortCol = SortCol[i]; goods.desc = SortDesc[i]; goods.Resort(); cfg.goodsSort = goods.sortCol; cfg.goodsDesc = goods.desc; cfg.Save(); });
            head = Kit.TxtAt(root, "", gx, 34, gw, 26, 16, Theme.Accent);
            var gcols = new List<Column<GoodsQuote>>
            {
                new Column<GoodsQuote> { title = "货物", width = 190, text = q => q.name, key = q => q.name },
                new Column<GoodsQuote> { title = "类别", width = 90, text = q => q.category, key = q => q.category, color = _ => Theme.Text2 },
                new Column<GoodsQuote> { title = "库存", width = 100, text = q => q.stock.ToString(), key = q => q.stock, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<GoodsQuote> { title = "买 1 个(含税)", width = 130, text = q => q.buyable ? Fmt.Money(q.buy1) : "-", key = q => q.buyable ? (IComparable)q.buy1 : null, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<GoodsQuote> { title = "卖 1 个(到手)", width = 130, text = q => Fmt.Money(q.sell1), key = q => q.sell1, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<GoodsQuote> { title = "税", width = 60, text = q => (q.tax * 100).ToString("0.#") + "%", key = q => q.tax, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<GoodsQuote> { title = "能买", width = 56, text = q => q.buyable ? "是" : "-", key = q => q.buyable ? 1 : 0, numeric = true },
                new Column<GoodsQuote> { title = "违禁", width = 56, text = q => q.contraband ? "违禁" : "", key = q => q.contraband ? 1 : 0, numeric = true, color = _ => Theme.Bad },
                new Column<GoodsQuote> { title = "价格", width = 60, text = q => q.exact ? "实时" : "估", key = q => q.exact ? 0 : 1, numeric = true, color = q => q.exact ? Theme.Good : Theme.Warn },
            };
            goods = new Table<GoodsQuote>(root, gx, 64, gw, w.BodyH - 64, gcols, cfg.goodsSort, cfg.goodsDesc);
            goods.emptyText = "先在左边选一个站；“未开过”的站可以在“后台补算”页初始化。";
            goods.filter = q => (!cfg.goodsBuyableOnly || q.buyable) && (!cfg.hideContraband || !q.contraband) && (category == null || q.category == category) && Match(search, q.name);
        }

        void ShowGoods()
        {
            if (selected == null) { head.text = ""; goods.SetData(new List<GoodsQuote>()); return; }
            head.text = selected.name + "    " + selected.Freshness + "    税 " + (selected.tax * 100).ToString("0") + "%    距舰队 " + Fmt.Dist(selected.fleetAu);
            var cats = new List<string>();
            foreach (var q in selected.goods) cats.Add(q.category);
            var l = Categories(snap, cats);
            cat.SetOptions(l, category == null ? 0 : Math.Max(0, l.IndexOf(category)));
            if (cat.index == 0) category = null;
            goods.SetData(selected.goods);
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            markets.SetData(s.markets);
            selected = s.markets.Find(m => m.uid == selectedUid);
            if (selected == null && s.markets.Count > 0)
            {
                var near = new List<MarketInfo>(s.markets);
                near.Sort((a, b) => (a.fleetAu < 0 ? double.MaxValue : a.fleetAu).CompareTo(b.fleetAu < 0 ? double.MaxValue : b.fleetAu));
                selected = near.Find(m => m.initialized) ?? near[0];
                selectedUid = selected.uid;
            }
            ShowGoods();
            markets.Bind();
        }
    }

    public class InventoryView : ViewBase
    {
        public class Row
        {
            public string key, name, category, location;
            public int qty;
            public SellIdea best;
        }

        readonly Table<Row> table;
        readonly Kit.Drop loc, cat;
        string search = "", location, category;

        public InventoryView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            float x = 0;
            loc = Drop("位置：", x, 0, 280, new List<string> { "全部" }, 0, i => { location = i == 0 ? null : loc.options[i]; table.Resort(); });
            x += 288;
            cat = Drop("", x, 0, 170, new List<string> { "全部类别" }, 0, i => { category = i == 0 ? null : cat.options[i]; table.Resort(); });
            x += 178;
            Kit.Input(root, "搜索物品", x, 0, 220, 30, s => { search = s; table.Resort(); });
            var cols = new List<Column<Row>>
            {
                new Column<Row> { title = "物品", width = 220, text = r => r.name, key = r => r.name },
                new Column<Row> { title = "类别", width = 100, text = r => r.category, key = r => r.category, color = _ => Theme.Text2 },
                new Column<Row> { title = "位置", width = 300, text = r => r.location, key = r => r.location, color = _ => Theme.Text2 },
                new Column<Row> { title = "数量", width = 120, text = r => r.qty.ToString(), key = r => r.qty, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Row> { title = "最好卖到", width = 260, text = r => r.best != null ? r.best.market.market.name : "-", key = r => r.best?.market.market.name },
                new Column<Row> { title = "首件到手", width = 130, text = r => r.best != null ? Fmt.Money(r.best.firstUnitNet) : "-", key = r => r.best != null ? (IComparable)r.best.firstUnitNet : null, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Row> { title = "首件 × 数量", width = 140, text = r => r.best != null ? Fmt.Money(r.best.firstUnitNet * r.qty) : "-", key = r => r.best != null ? (IComparable)(r.best.firstUnitNet * r.qty) : null, numeric = true, align = TextAlignmentOptions.MidlineRight },
            };
            table = new Table<Row>(root, 0, 40, w.BodyW, w.BodyH - 40, cols, cfg.invSort, cfg.invDesc);
            table.filter = r => (location == null || r.location == location) && (category == null || r.category == category) && Match(search, r.name);
            table.sortChanged = (c, d) => { cfg.invSort = c; cfg.invDesc = d; cfg.Save(); };
            Kit.TxtAt(root, "“首件 × 数量”只是上限参考：卖多了价格会跌，实际可得看“卖库存”页。", x + 228, 0, w.BodyW - x - 228, 30, 14, Theme.Text2);
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            var best = new Dictionary<string, SellIdea>();
            foreach (var si in s.sells) best[si.key] = si;
            var rows = new List<Row>();
            var locs = new List<string> { "全部" };
            var cats = new List<string>();
            foreach (var l in s.inventory)
            {
                locs.Add(l.name);
                foreach (var kv in l.items)
                {
                    string gid, c = "其他";
                    if (s.itemKeyToGid.TryGetValue(kv.Key, out gid)) s.gidCategory.TryGetValue(gid, out c);
                    SellIdea b; best.TryGetValue(kv.Key, out b);
                    rows.Add(new Row { key = kv.Key, name = MarketScanner.ItemName(s, kv.Key), category = c ?? "其他", location = l.name, qty = kv.Value, best = b });
                    cats.Add(c);
                }
            }
            loc.SetOptions(locs, location == null ? 0 : Math.Max(0, locs.IndexOf(location)));
            if (loc.index == 0) location = null;
            var cl = Categories(s, cats);
            cat.SetOptions(cl, category == null ? 0 : Math.Max(0, cl.IndexOf(category)));
            if (cat.index == 0) category = null;
            table.SetData(rows);
        }
    }

    public class SettlementView : ViewBase
    {
        public class Station
        {
            public FWExchangeSpace ex;
            public long uid;
            public string name;
            public double fleetAu;
            public bool Initialized { get { try { return ex.storeInitialized; } catch { return false; } } }
            public int Pending { get { try { return ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0; } catch { return 0; } } }
        }

        readonly Table<Station> table;
        readonly TextMeshProUGUI info;
        readonly Kit.Btn pauseBtn, initAll, catchAll;
        int show;
        float nextBind;

        string State(Station s)
        {
            if (host.settlement.currentUid == s.uid) return "补算中 · 剩" + host.settlement.currentLeft + "天";
            if (host.settlement.IsQueued(s.uid)) return "排队中";
            if (!s.Initialized) return s.Pending > 0 ? "未开过 · 欠" + s.Pending + "天" : "未开过";
            return s.Pending == 0 ? "实时" : "欠" + s.Pending + "天";
        }

        Color StateColor(Station s)
        {
            if (host.settlement.IsQueued(s.uid)) return Theme.Accent;
            if (!s.Initialized) return Theme.Text2;
            return s.Pending == 0 ? Theme.Good : Theme.Warn;
        }

        public SettlementView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            Kit.TxtAt(root, "补算 = 让游戏把这个站欠着的市场天数算完；初始化 = 第一次生成从没开过的站的货架。效果和你亲自打开那个站的交易所一样，会改变它的库存和价格。每帧只算一点，不会卡住游戏。", 0, 0, w.BodyW, 24, 14, Theme.Text2);
            initAll = Kit.Button(root, "一键初始化所有未开过的站", 0, 30, 250, 32, () =>
            {
                foreach (var s in table.all) if (!s.Initialized) host.settlement.Enqueue(s.uid);
            }, true);
            catchAll = Kit.Button(root, "补算所有欠天的站", 258, 30, 200, 32, () =>
            {
                foreach (var s in table.all) if (s.Initialized && s.Pending > 0) host.settlement.Enqueue(s.uid);
            });
            pauseBtn = Kit.Button(root, "暂停", 466, 30, 110, 32, () => { host.settlement.paused = !host.settlement.paused; });
            Kit.Button(root, "清空队列", 584, 30, 110, 32, () => host.settlement.Clear());
            Drop("显示：", 704, 30, 200, new List<string> { "全部", "未开过", "欠天", "实时", "保持实时的" }, 0, i => { show = i; table.Resort(); });
            info = Kit.TxtAt(root, "", 914, 30, w.BodyW - 914, 32, 15, Theme.Text);
            var cols = new List<Column<Station>>
            {
                new Column<Station> { title = "站", width = 330, text = s => s.name, key = s => s.name },
                new Column<Station> { title = "状态", width = 200, text = State, key = s => s.Initialized ? s.Pending : 1000000 + s.Pending, numeric = true, color = StateColor },
                new Column<Station> { title = "距舰队", width = 160, text = s => Fmt.Dist(s.fleetAu), key = s => s.fleetAu < 0 ? (IComparable)null : s.fleetAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Station> { title = "操作", width = 150, buttonText = s => host.settlement.IsQueued(s.uid) ? "排队中" : (!s.Initialized ? "初始化" : (s.Pending > 0 ? "补算" : "已实时")),
                    buttonEnabled = s => !host.settlement.IsQueued(s.uid) && (!s.Initialized || s.Pending > 0), buttonPrimary = s => !s.Initialized || s.Pending > 0,
                    onButton = s => host.settlement.Enqueue(s.uid) },
                new Column<Station> { title = "保持实时", width = 120, checkGet = s => cfg.keepFresh.Contains(s.uid),
                    checkSet = (s, v) => { if (v) { if (!cfg.keepFresh.Contains(s.uid)) cfg.keepFresh.Add(s.uid); host.settlement.Enqueue(s.uid); } else cfg.keepFresh.Remove(s.uid); cfg.Save(); } },
            };
            table = new Table<Station>(root, 0, 72, w.BodyW, w.BodyH - 72, cols, cfg.settleSort, cfg.settleDesc);
            table.sortChanged = (c, d) => { cfg.settleSort = c; cfg.settleDesc = d; cfg.Save(); };
            table.filter = s => show == 0 || (show == 1 && !s.Initialized) || (show == 2 && s.Initialized && s.Pending > 0) || (show == 3 && s.Initialized && s.Pending == 0) || (show == 4 && cfg.keepFresh.Contains(s.uid));
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            var rows = new List<Station>();
            foreach (var m in s.markets) rows.Add(new Station { ex = m.ex, uid = m.uid, name = m.name, fleetAu = m.fleetAu });
            table.SetData(rows);
        }

        public override void Tick()
        {
            if (Time.unscaledTime < nextBind) return;
            nextBind = Time.unscaledTime + 0.5f;
            var st = host.settlement;
            int un = 0, owe = 0;
            foreach (var s in table.all) { if (!s.Initialized) un++; else if (s.Pending > 0) owe++; }
            initAll.Text = "一键初始化所有未开过的站（" + un + "）";
            catchAll.Text = "补算所有欠天的站（" + owe + "）";
            pauseBtn.Text = st.paused ? "继续" : "暂停";
            info.text = (st.Pending > 0 ? "队列 " + st.Pending + " 个站" + (st.paused ? "（已暂停）" : "") : "队列空") +
                        "   本次已补 " + st.daysDoneTotal + " 天 / 完成 " + st.marketsDone + " 站" +
                        (string.IsNullOrEmpty(st.lastError) ? "" : "   <color=#D66079>最近错误：" + st.lastError + "</color>");
            table.Bind();
        }
    }

    public class SettingsView : ViewBase
    {
        readonly Table<string> errors;
        readonly TextMeshProUGUI path;
        static readonly int[] DayOpts = { 1, 2, 3, 5, 10 };
        static readonly float[] RefreshOpts = { 15, 30, 60, 120, 0 };
        static readonly float[] ExportOpts = { 0, 60, 300, 900 };

        public SettingsView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            float y = 0;
            Kit.TxtAt(root, "开关热键（Unity 键名，如 F8、F9、Insert）", 0, y, 380, 30, 16, Theme.Text);
            var hk = Kit.Input(root, "F8", 390, y, 120, 30, null);
            hk.text = cfg.hotkey;
            Kit.Button(root, "保存", 518, y, 80, 30, () => { if (!string.IsNullOrEmpty(hk.text)) { cfg.hotkey = hk.text.Trim(); cfg.Save(); } });
            y += 42;
            int di = Array.IndexOf(DayOpts, cfg.daysPerFrame);
            Drop("后台补算速度：", 0, y, 420, new List<string> { "每帧 1 天（最平滑）", "每帧 2 天", "每帧 3 天", "每帧 5 天", "每帧 10 天（最快，可能掉帧）" }, di < 0 ? 0 : di,
                i => { cfg.daysPerFrame = DayOpts[i]; cfg.Save(); });
            y += 42;
            int ri = Array.IndexOf(RefreshOpts, cfg.autoRefreshSeconds);
            Drop("面板打开时自动刷新：", 0, y, 420, new List<string> { "每 15 秒", "每 30 秒", "每 60 秒", "每 2 分钟", "不自动刷新" }, ri < 0 ? 1 : ri,
                i => { cfg.autoRefreshSeconds = RefreshOpts[i]; cfg.Save(); });
            y += 42;
            int ei = Array.IndexOf(ExportOpts, cfg.exportEverySeconds);
            Drop("自动导出数据文件：", 0, y, 420, new List<string> { "关", "每 1 分钟", "每 5 分钟", "每 15 分钟" }, ei < 0 ? 2 : ei,
                i => { cfg.exportEverySeconds = ExportOpts[i]; cfg.Save(); });
            Kit.Button(root, "立即导出", 430, y, 120, 30, () => host.ExportNow(), true);
            y += 42;
            path = Kit.TxtAt(root, "数据文件：" + System.IO.Path.Combine(Config.Dir, "latest.json"), 0, y, w.BodyW, 26, 14, Theme.Text2);
            y += 36;
            Kit.TxtAt(root, "距离和游戏一样按星门航线算（AU；超过 1 光年显示 ly）。“舰队→某站”= 你的舰队现在所在星系到那个站所在星系的航程。", 0, y, w.BodyW, 26, 14, Theme.Text2);
            y += 36;
            Kit.TxtAt(root, "读取问题", 0, y, 300, 30, 17, Theme.Text);
            y += 34;
            var cols = new List<Column<string>> { new Column<string> { title = "内容", width = w.BodyW - 20, text = s => s, color = _ => Theme.Text2 } };
            errors = new Table<string>(root, 0, y, w.BodyW, w.BodyH - y, cols, -1, false) { emptyText = "没有" };
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            errors.SetData(s.errors);
        }
    }
}
