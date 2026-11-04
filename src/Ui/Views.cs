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
            Kit.TxtAt(root, "算到“再多买就开始亏”为止，两边税都扣了。违禁品、没发现的星系、星球地面交易行（舰队用不了）不出现。* = 已扣掉你计划里同一站要买/卖的数量。“估”= 那个站欠结算天数。", 0, 34, w.BodyW - 390, 22, 14, Theme.Text2);
            Kit.Toggle(root, "含地面交易行", w.BodyW - 380, 31, 180, cfg.includeGround, v => { cfg.includeGround = v; cfg.Save(); host.RequestRefresh(true); });
            Kit.Toggle(root, "含未发现的星系", w.BodyW - 190, 31, 190, cfg.includeUndiscovered, v => { cfg.includeUndiscovered = v; cfg.Save(); host.RequestRefresh(true); });

            var cols = new List<Column<TradeIdea>>
            {
                new Column<TradeIdea> { title = "商品", width = 150, text = t => t.name + (t.planBuyOffset > 0 || t.planSellOffset > 0 ? " *" : ""), key = t => t.name },
                new Column<TradeIdea> { title = "类别", width = 70, text = t => t.category, key = t => t.category, color = _ => Theme.Text2 },
                new Column<TradeIdea> { title = "在哪买", width = 185, text = t => t.buy.market.name, key = t => t.buy.market.name, color = t => Fmt.FreshColor(t.buy.market) },
                new Column<TradeIdea> { title = "买几个", width = 64, text = t => t.qty.ToString(), key = t => t.qty, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "成本(含税)", width = 105, text = t => Fmt.Money(t.cost), key = t => t.cost, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "去哪卖", width = 185, text = t => t.sell.market.name, key = t => t.sell.market.name, color = t => Fmt.FreshColor(t.sell.market) },
                new Column<TradeIdea> { title = "到手(扣税)", width = 105, text = t => Fmt.Money(t.revenue), key = t => t.revenue, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "净赚", width = 105, text = t => Fmt.Money(t.Profit), key = t => t.Profit, numeric = true, align = TextAlignmentOptions.MidlineRight, color = _ => Theme.Accent },
                new Column<TradeIdea> { title = "利润率", width = 76, text = t => (t.Margin * 100).ToString("0") + "%", key = t => t.Margin, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "买→卖 航程", width = 112, text = t => Fmt.Dist(t.legAu), key = t => t.legAu < 0 ? (IComparable)null : t.legAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "舰队→买入站", width = 112, text = t => Fmt.Dist(t.fleetAu), key = t => t.fleetAu < 0 ? (IComparable)null : t.fleetAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "每光年净赚", width = 95, text = t => { var v = PerLy(t); return double.IsNaN(v) ? "?" : Fmt.Money(v); }, key = t => { var v = PerLy(t); return double.IsNaN(v) ? (IComparable)null : v; }, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<TradeIdea> { title = "数据", width = 66, text = t => t.Stale ? "估·欠" + t.MaxPending + "天" : "实时", key = t => t.MaxPending, numeric = true, color = t => t.Stale ? Theme.Warn : Theme.Good },
                new Column<TradeIdea> { title = "", width = 74, buttonText = t => Tracked(t) ? "追踪中" : "追踪", buttonEnabled = t => !Tracked(t),
                    onButton = t => { if (snap != null) Plans.FromTrade(t, snap, cfg, host.settlement); host.PlansChanged(); } },
            };
            table = new Table<TradeIdea>(root, 0, 60, w.BodyW, w.BodyH - 60, cols, cfg.tradesSort, cfg.tradesDesc);
            table.emptyText = "没有扣税后能赚钱的组合（或都被筛掉了）。可以放宽“数据”或先在“后台补算”页补算更多站。";
            table.filter = t => WithinLy(t.fleetAu, cfg.maxFleetLy) && t.Profit >= cfg.minProfit && (category == null || t.category == category) && Match(search, t.name, t.buy.market.name, t.sell.market.name);
            table.sortChanged = (c, d) => SaveSort();
        }

        static bool Tracked(TradeIdea t) => Plans.list.Exists(p => p.state != 2 && p.gid == t.gid && p.buyUid == t.buy.market.uid && p.sellUid == t.sell.market.uid);

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
                new Column<SellIdea> { title = "", width = 74, buttonText = s => SellTracked(s) ? "追踪中" : "追踪", buttonEnabled = s => !SellTracked(s),
                    onButton = s => { if (snap != null) Plans.FromSell(s, snap, cfg, host.settlement); host.PlansChanged(); } },
            };
            table = new Table<SellIdea>(root, 0, 60, w.BodyW, w.BodyH - 60, cols, cfg.sellsSort, cfg.sellsDesc);
            table.filter = s => WithinLy(s.fleetAu, cfg.maxFleetLy) && (category == null || s.category == category) && Match(search, s.name, s.location, s.market.market.name);
            table.sortChanged = (c, d) => SaveSort();
        }

        // Only plans made from stock already held count; a plan to buy more of the same goods is a different plan.
        static bool SellTracked(SellIdea s) => Plans.list.Exists(p => p.state != 2 && p.buyUid < 0 && p.key == s.key && p.sellUid == s.market.market.uid && (p.fromLocation == null || p.fromLocation == s.location));

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
                new Column<MarketInfo> { title = "站", width = 220, text = m => m.name + (m.Discovered ? "" : "（未发现）"), key = m => m.name, color = m => m.Discovered ? Theme.Text : Theme.Text2 },
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
            head = Kit.TxtAt(root, "", gx, 34, gw, 44, 16, Theme.Accent);
            head.enableWordWrapping = true;
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
            goods = new Table<GoodsQuote>(root, gx, 82, gw, w.BodyH - 82, gcols, cfg.goodsSort, cfg.goodsDesc);
            goods.emptyText = "先在左边选一个站；“未开过”的站可以在“后台补算”页初始化。";
            goods.filter = q => (!cfg.goodsBuyableOnly || q.buyable) && (!cfg.hideContraband || !q.contraband) && (category == null || q.category == category) && Match(search, q.name);
        }

        void ShowGoods()
        {
            if (selected == null) { head.text = ""; goods.SetData(new List<GoodsQuote>()); return; }
            head.text = selected.name + "    " + selected.Freshness + "    税 " + (selected.tax * 100).ToString("0") + "%    距舰队 " + Fmt.Dist(selected.fleetAu) + "\n<size=13><color=#A4A5A6>" + selected.where + "</color></size>";
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
            public bool discovered;
            public bool Initialized { get { try { return ex.storeInitialized; } catch { return false; } } }
            public int Pending { get { try { return ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0; } catch { return 0; } } }
        }

        readonly Table<Station> table;
        readonly TextMeshProUGUI info;
        readonly Kit.Btn pauseBtn, initAll, catchAll;
        int show;
        float nextBind;
        bool cancelInit, cancelCatch;

        bool IsUninitialized(long uid)
        {
            var s = table.all.Find(x => x.uid == uid);
            return s != null && !s.Initialized;
        }

        string State(Station s)
        {
            if (host.settlement.currentUid == s.uid) return "补算中 · 剩" + host.settlement.currentLeft + "天";
            if (host.settlement.IsQueued(s.uid)) return "排队中";
            if (host.settlement.IsCancelled(s.uid) && cfg.keepFresh.Contains(s.uid)) return "已取消（本次不自动补）";
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
            Kit.TxtAt(root, "补算 = 让游戏把这个站欠着的天数算完；初始化 = 第一次生成从没开过的站的货架。效果和你打开那个站的交易所一样，会改变它的库存和价格。随时可以取消，已经算过的天数不会退回。", 0, 0, w.BodyW, 24, 14, Theme.Text2);
            const float BW = 230, Gap = 8;
            initAll = Kit.Button(root, "", 0, 30, BW, 32, () =>
            {
                if (cancelInit) host.settlement.Cancel(uid => IsUninitialized(uid));
                else foreach (var s in table.all) if (!s.Initialized && s.discovered) host.settlement.Enqueue(s.uid);
                nextBind = 0;
            });
            catchAll = Kit.Button(root, "", BW + Gap, 30, BW, 32, () =>
            {
                if (cancelCatch) host.settlement.Cancel(uid => !IsUninitialized(uid));
                else foreach (var s in table.all) if (s.Initialized && s.Pending > 0) host.settlement.Enqueue(s.uid);
                nextBind = 0;
            });
            pauseBtn = Kit.Button(root, "暂停", (BW + Gap) * 2, 30, BW, 32, () => { host.settlement.paused = !host.settlement.paused; nextBind = 0; });
            Kit.Button(root, "全部取消", (BW + Gap) * 3, 30, BW, 32, () => { host.settlement.CancelAll(); nextBind = 0; });
            Drop("显示：", (BW + Gap) * 4, 30, BW, new List<string> { "全部", "未开过", "欠天", "实时", "保持实时的", "未发现星系" }, 0, i => { show = i; table.Resort(); });
            info = Kit.TxtAt(root, "", 0, 68, w.BodyW, 26, 15, Theme.Text);
            var cols = new List<Column<Station>>
            {
                new Column<Station> { title = "站", width = 330, text = s => s.name, key = s => s.name },
                new Column<Station> { title = "状态", width = 200, text = State, key = s => s.Initialized ? s.Pending : 1000000 + s.Pending, numeric = true, color = StateColor },
                new Column<Station> { title = "距舰队", width = 160, text = s => Fmt.Dist(s.fleetAu), key = s => s.fleetAu < 0 ? (IComparable)null : s.fleetAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Station> { title = "操作", width = 150, buttonText = s => host.settlement.IsQueued(s.uid) ? "取消" : (!s.Initialized ? "初始化" : (s.Pending > 0 ? "补算" : "已实时")),
                    buttonEnabled = s => host.settlement.IsQueued(s.uid) || !s.Initialized || s.Pending > 0,
                    onButton = s => { if (host.settlement.IsQueued(s.uid)) host.settlement.Cancel(id => id == s.uid); else host.settlement.Enqueue(s.uid); } },
                new Column<Station> { title = "保持实时", width = 120, checkGet = s => cfg.keepFresh.Contains(s.uid),
                    checkSet = (s, v) => { if (v) { if (!cfg.keepFresh.Contains(s.uid)) cfg.keepFresh.Add(s.uid); host.settlement.Enqueue(s.uid); } else cfg.keepFresh.Remove(s.uid); cfg.Save(); } },
            };
            table = new Table<Station>(root, 0, 100, w.BodyW, w.BodyH - 100, cols, cfg.settleSort, cfg.settleDesc);
            table.sortChanged = (c, d) => { cfg.settleSort = c; cfg.settleDesc = d; cfg.Save(); };
            table.filter = s => show == 0 || (show == 1 && !s.Initialized) || (show == 2 && s.Initialized && s.Pending > 0) || (show == 3 && s.Initialized && s.Pending == 0) || (show == 4 && cfg.keepFresh.Contains(s.uid)) || (show == 5 && !s.discovered);
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            var rows = new List<Station>();
            foreach (var m in s.markets) rows.Add(new Station { ex = m.ex, uid = m.uid, name = m.name + (m.Discovered ? "" : "（未发现）"), fleetAu = m.fleetAu, discovered = m.Discovered });
            table.SetData(rows);
        }

        public override void Tick()
        {
            if (Time.unscaledTime < nextBind) return;
            nextBind = Time.unscaledTime + 0.5f;
            var st = host.settlement;
            int un = 0, owe = 0, qInit = 0, qCatch = 0;
            foreach (var s in table.all)
            {
                bool q = st.IsQueued(s.uid);
                if (!s.Initialized) { if (s.discovered) un++; if (q) qInit++; }
                else { if (s.Pending > 0) owe++; if (q) qCatch++; }
            }
            cancelInit = qInit > 0;
            cancelCatch = qCatch > 0;
            initAll.Text = cancelInit ? "取消初始化（还剩 " + qInit + " 站）" : "一键初始化已发现的站（" + un + "）";
            catchAll.Text = cancelCatch ? "取消补算（还剩 " + qCatch + " 站）" : "补算所有欠天的站（" + owe + "）";
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
        readonly TextMeshProUGUI path, hint, perf;
        readonly Kit.Drop dirDrop;
        readonly TMP_InputField custom;
        float nextPerf;
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
            dirDrop = Drop("数据文件、截图和性能日志放在：", 0, y, 420, new List<string> { "C 盘游戏数据目录（默认）", "游戏目录里本模组的 data 文件夹", "自定义文件夹" }, Mathf.Clamp(cfg.dataDirMode, 0, 2),
                i =>
                {
                    if (i == 2 && string.IsNullOrEmpty(cfg.dataDirCustom))
                    {
                        // Nothing typed yet: keep the current folder until a folder is entered.
                        dirDrop.SetOptions(dirDrop.options, cfg.dataDirMode);
                        hint.text = "<color=#F4B30D>先在右边输入文件夹（例如 E:\\OuterSpaceTrade），再点「用这个文件夹」。</color>";
                        return;
                    }
                    cfg.dataDirMode = i; cfg.Save(); Applied();
                });
            custom = Kit.Input(root, "例如 E:\\OuterSpaceTrade", 430, y, 420, 30, null);
            custom.text = cfg.dataDirCustom ?? "";
            Kit.Button(root, "用这个文件夹", 858, y, 140, 30, () =>
            {
                var t = custom.text.Trim();
                if (string.IsNullOrEmpty(t)) { hint.text = "<color=#F4B30D>先输入文件夹路径。</color>"; return; }
                cfg.dataDirCustom = t; cfg.dataDirMode = 2; cfg.Save();
                dirDrop.SetOptions(dirDrop.options, 2);
                Applied();
            });
            Kit.Button(root, "打开文件夹", 1006, y, 140, 30, () => { try { Application.OpenURL("file:///" + cfg.DataDir.Replace("\\", "/")); } catch { } });
            y += 36;
            path = Kit.TxtAt(root, "", 0, y, w.BodyW, 26, 14, Theme.Text2);
            y += 26;
            hint = Kit.TxtAt(root, "", 0, y, w.BodyW, 26, 14, Theme.Text2);
            ShowPath();
            y += 30;
            Kit.TxtAt(root, "放在游戏目录里不影响游戏：游戏只读取模组文件夹里的 Manifest 和程序文件，Steam 校验和更新也不删多出来的文件；更新本模组时 data 文件夹会保留。配置 config.json 和计划 plans.json 固定在 C 盘游戏数据目录。", 0, y, w.BodyW, 26, 14, Theme.Text2);
            y += 34;
            Drop("距离显示：", 0, y, 420, new List<string> { "AU（带千分位）", "和游戏航线界面一样（超过 1 光年显示 ly）" }, Mathf.Clamp(cfg.distanceUnit, 0, 1),
                i => { cfg.distanceUnit = i; Fmt.Unit = i; cfg.Save(); host.PlansChanged(); });
            Kit.TxtAt(root, "距离按星门航线算（和游戏用同一个算法）；“舰队→某站”= 舰队所在星系到那个站所在星系的航程。", 430, y, w.BodyW - 430, 30, 14, Theme.Text2);
            y += 38;
            Kit.TxtAt(root, "性能（每 30 秒写一行到 perf.log）", 0, y, 400, 28, 17, Theme.Text);
            y += 30;
            perf = Kit.Txt(root, "", 13, Theme.Text2, TextAlignmentOptions.TopLeft);
            Kit.Place(perf.rectTransform, 0, y, w.BodyW, 66);
            perf.enableWordWrapping = true;
            perf.overflowMode = TextOverflowModes.Truncate;
            y += 70;
            Kit.TxtAt(root, "读取问题", 0, y, 300, 30, 17, Theme.Text);
            y += 34;
            var cols = new List<Column<string>> { new Column<string> { title = "内容", width = w.BodyW - 20, text = s => s, color = _ => Theme.Text2 } };
            errors = new Table<string>(root, 0, y, w.BodyW, w.BodyH - y, cols, -1, false) { emptyText = "没有" };
        }

        void ShowPath()
        {
            var d = cfg.DataDir;
            path.text = "现在写到：" + d + (cfg.dataDirMode != 0 && d == Config.Dir ? "   <color=#D66079>（选的文件夹建不了，暂时写回默认位置；看看路径对不对）</color>" : "");
        }

        void Applied()
        {
            ShowPath();
            var d = cfg.DataDir;
            bool ok = cfg.dataDirMode == 0 || d != Config.Dir;
            hint.text = ok ? "<color=#4AC37D>已切换，正在导出一次 latest.json 到这里。</color>" : "";
            if (ok) host.ExportNow();
        }

        public override void Tick()
        {
            if (Time.unscaledTime < nextPerf) return;
            nextPerf = Time.unscaledTime + 1f;
            perf.text = (string.IsNullOrEmpty(Perf.LastSummary) ? "（30 秒后出第一行）" : Perf.LastSummary) + "\n" + Perf.LastScan;
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            errors.SetData(s.errors);
        }
    }
}

namespace TradeHelper.Ui
{
    // Trades the player follows: live re-pricing of both stations, purchase detection, profit history and the best
    // places to sell the holding right now.
    public class PlansView : ViewBase
    {
        readonly Table<Plan> table;
        readonly TextMeshProUGUI detail, summary;
        Plan selected;
        int show;

        static string Signed(double v) => (v >= 0 ? "+" : "") + Fmt.Money(v);

        static string Change(Plan p)
        {
            if (double.IsNaN(p.CurProfit) || p.PlanProfit == 0) return "?";
            double d = (p.CurProfit - p.PlanProfit) / Math.Abs(p.PlanProfit) * 100;
            return (d >= 0 ? "+" : "") + d.ToString("0") + "%";
        }

        static Color ChangeColor(Plan p)
        {
            if (double.IsNaN(p.CurProfit)) return Theme.Text2;
            return p.CurProfit >= p.PlanProfit ? Theme.Good : (p.CurProfit > 0 ? Theme.Warn : Theme.Bad);
        }

        static string Station(string name, MarketInfo m) => m == null ? name : name + (m.Exact ? "" : " · " + m.Freshness);

        public PlansView(Window w, RectTransform body, Config cfg, TradeHelperBehaviour host) : base(w, body, cfg, host)
        {
            Kit.TxtAt(root, "状态会自动改：库存里多出这批货 → ② 已买，待卖；这批货全部从库存里没了 → ③ 已完成（按卖出站的成交记录写明在哪卖的）。「手动改状态」是按钮，可纠正。* = 同站同货还有别的计划，已按排队后被压低的价格算", 0, 0, w.BodyW, 24, 14, Theme.Text2);
            Drop("显示：", 0, 30, 230, new List<string> { "进行中", "全部", "已完成" }, 0, i => { show = i; table.Resort(); });
            Kit.Button(root, "删除所有已完成", 238, 30, 230, 32, () =>
            {
                foreach (var p in new List<Plan>(Plans.list)) if (p.state == 2) Plans.Remove(p, cfg);
                Refresh();
            });
            summary = Kit.TxtAt(root, "", 476, 30, w.BodyW - 476, 32, 15, Theme.Text);
            var cols = new List<Column<Plan>>
            {
                new Column<Plan> { title = "商品", width = 130, text = p => p.name, key = p => p.name },
                new Column<Plan> { title = "数量", width = 56, text = p => p.ShownQty.ToString(), key = p => p.ShownQty, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Plan> { title = "状态", width = 128, text = p => p.StateText, key = p => p.state, numeric = true, color = p => p.state == 0 ? Theme.Accent : p.state == 1 ? Theme.Good : Theme.Text2 },
                new Column<Plan> { title = "在哪买", width = 176, text = p => Station(p.buyName, p.buyMarket), key = p => p.buyName, color = p => p.buyMarket != null ? Fmt.FreshColor(p.buyMarket) : Theme.Text2 },
                new Column<Plan> { title = "去哪卖", width = 176, text = p => Station(p.sellName, p.sellMarket), key = p => p.sellName, color = p => p.sellMarket != null ? Fmt.FreshColor(p.sellMarket) : Theme.Text2 },
                new Column<Plan> { title = "计划净赚", width = 96, text = p => Fmt.Money(p.PlanProfit), key = p => p.PlanProfit, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Plan> { title = "现在净赚", width = 96, text = p => double.IsNaN(p.CurProfit) ? "?" : Fmt.Money(p.CurProfit) + (p.sellAhead > 0 || p.buyAhead > 0 ? "*" : ""), key = p => double.IsNaN(p.CurProfit) ? (IComparable)null : p.CurProfit, numeric = true, align = TextAlignmentOptions.MidlineRight, color = ChangeColor },
                new Column<Plan> { title = "变化", width = 70, text = Change, key = p => double.IsNaN(p.CurProfit) ? (IComparable)null : p.CurProfit - p.PlanProfit, numeric = true, align = TextAlignmentOptions.MidlineRight, color = ChangeColor },
                new Column<Plan> { title = "现在最好卖到", width = 176, text = p => p.alternatives.Count > 0 ? p.alternatives[0].Key.name : "?", key = p => p.alternatives.Count > 0 ? p.alternatives[0].Key.name : null },
                new Column<Plan> { title = "舰队→下一站", width = 104, text = p => Fmt.Dist(p.fleetToNextAu), key = p => p.fleetToNextAu < 0 ? (IComparable)null : p.fleetToNextAu, numeric = true, align = TextAlignmentOptions.MidlineRight },
                new Column<Plan> { title = "两站实时", width = 92, checkGet = p => p.keepFresh, checkSet = (p, v) => { p.keepFresh = v; Plans.ApplyKeepFresh(p, cfg, host.settlement); Plans.Save(); } },
                new Column<Plan> { title = "手动改状态", width = 110, buttonText = p => p.state == 0 ? "改为已买入" : p.state == 1 ? "改为已完成" : "改回已买入",
                    onButton = p => { if (p.state == 0) Plans.MarkBought(p, cfg, host.settlement); else if (p.state == 1) Plans.MarkDone(p, cfg); else Plans.Reopen(p, cfg, host.settlement); Refresh(); } },
                new Column<Plan> { title = "", width = 70, buttonText = _ => "删除", onButton = p => { Plans.Remove(p, cfg); if (selected == p) selected = null; Refresh(); } },
            };
            table = new Table<Plan>(root, 0, 70, w.BodyW, w.BodyH - 70 - 190, cols, 2, false);
            table.emptyText = "还没有追踪的计划。在“跑商建议”或“卖库存”里点「追踪」。";
            table.filter = p => show == 1 || (show == 0 && p.state != 2) || (show == 2 && p.state == 2);
            table.onRowClick = p => { selected = p; ShowDetail(); };
            table.isSelected = p => p == selected;
            var box = Kit.Place(Kit.New("Detail", root), 0, w.BodyH - 182, w.BodyW, 182);
            Kit.Img(box, Theme.Box, Theme.HeadBg);
            detail = Kit.Txt(box, "", 15, Theme.Text, TextAlignmentOptions.TopLeft);
            Kit.Stretch(detail.rectTransform, 12, 8, 12, 8);
            detail.enableWordWrapping = true;
            detail.overflowMode = TextOverflowModes.Truncate;
        }

        void Refresh() { table.SetData(new List<Plan>(Plans.list)); ShowSummary(); ShowDetail(); }

        // Totals over completed plans: trade plans (bought, then sold) and plans that sold stock already held are kept
        // apart, since the latter have no purchase cost.
        void ShowSummary()
        {
            int trades = 0, helds = 0, actual = 0; double profit = 0, received = 0;
            foreach (var p in Plans.list)
            {
                if (p.state != 2) continue;
                if (p.actualPaid) actual++;
                if (p.buyUid < 0) { helds++; received += p.soldRevenue; }
                else { trades++; profit += p.soldRevenue - p.planCost; }
            }
            if (trades + helds == 0) { summary.text = "<color=#A4A5A6>完成的计划会在这里汇总：一共赚了多少、哪条路线最赚。</color>"; return; }
            var sb = new System.Text.StringBuilder();
            if (trades > 0) sb.Append("已完成跑商 ").Append(trades).Append(" 笔，共净赚 <color=#").Append(ColorUtility.ToHtmlStringRGB(profit >= 0 ? Theme.Good : Theme.Bad)).Append(">").Append(Signed(profit)).Append("</color>，平均每笔 ").Append(Signed(profit / trades));
            if (helds > 0) sb.Append(trades > 0 ? "；" : "").Append("卖库存 ").Append(helds).Append(" 笔，到手 ").Append(Fmt.Money(received));
            var routes = Plans.Routes().FindAll(r => !r.held);
            if (routes.Count > 0) sb.Append("；最赚：").Append(routes[0].name).Append(" ").Append(routes[0].from).Append("→").Append(routes[0].to);
            sb.Append(actual < trades + helds ? "  <color=#A4A5A6>(" + (trades + helds - actual) + " 笔按估计)</color>" : "");
            summary.text = sb.ToString();
        }

        void ShowDetail()
        {
            var p = selected;
            if (p == null || !Plans.list.Contains(p)) { detail.text = RoutesText(); return; }
            var sb = new System.Text.StringBuilder();
            sb.Append("<b>").Append(p.name).Append(" × ").Append(p.ShownQty).Append("</b>   ").Append(p.StateText).Append("   计划于 ").Append(p.createdAt).Append('\n');
            sb.Append("计划时：成本 ").Append(Fmt.Money(p.planCost)).Append("  到手 ").Append(Fmt.Money(p.planRevenue)).Append("  净赚 ").Append(Fmt.Money(p.PlanProfit)).Append('\n');
            sb.Append("现在：").Append(p.state == 0 ? "成本 " + (double.IsNaN(p.curCost) ? "?" : Fmt.Money(p.curCost)) + "  " : "成本按计划 ").Append("到手 ")
              .Append(double.IsNaN(p.curRevenue) ? "?" : Fmt.Money(p.curRevenue)).Append("  净赚 ").Append(double.IsNaN(p.CurProfit) ? "?" : Fmt.Money(p.CurProfit))
              .Append("（").Append(Change(p)).Append("）");
            if (p.buyMarket != null && p.state == 0) sb.Append("   买入站").Append(p.buyMarket.Exact ? "实时" : p.buyMarket.Freshness);
            if (p.sellMarket != null) sb.Append("   卖出站").Append(p.sellMarket.Exact ? "实时" : p.sellMarket.Freshness);
            sb.Append('\n');
            if (p.sellMarket != null && !string.IsNullOrEmpty(p.sellMarket.where)) sb.Append("卖出站怎么去：").Append(p.sellMarket.where).Append('\n');
            if (p.state >= 1 && p.buyUid >= 0)
                sb.Append(p.autoBought ? "已在库存里看到 " + p.boughtQty + " 个（" + p.boughtAt + " 自动识别为已买入）" : "已买入（" + (p.boughtAt ?? "") + " 手动标记）")
                  .Append(p.state == 1 ? "，下一步：到 " + p.sellName + " 卖出" : "").Append('\n');
            if (p.state == 1 && p.soldQty > 0)
                sb.Append("库存里已少了 ").Append(p.soldQty).Append("/").Append(p.boughtQty).Append(" 个（按卖出算，约到手 ").Append(Fmt.Money(p.soldRevenue)).Append("），全部没了会自动完成\n");
            if (p.state == 2)
                sb.Append("<color=#A4A5A6>").Append(p.autoDone ? "自动完成" : "完成").Append("于 ").Append(p.doneAt ?? "?").Append("：").Append(p.doneNote ?? "")
                  .Append("。到手约 ").Append(Fmt.Money(p.soldRevenue)).Append("，净赚约 ").Append(Fmt.Money(p.soldRevenue - p.planCost)).Append(p.autoDone ? "。判断错了可点「改回已买入」" : "").Append("</color>\n");
            if (p.sellAhead > 0 || p.buyAhead > 0)
                sb.Append("<color=#D8982F>同站同货有别的计划排在前面：").Append(p.sellAhead > 0 ? "卖出站先有 " + p.sellAhead + " 个要卖，" : "").Append(p.buyAhead > 0 ? "买入站先有 " + p.buyAhead + " 个要买，" : "")
                  .Append("这里的价格已按被它们压过之后算（顺序：已买/持有的先，再按加入先后）</color>\n");
            if (!string.IsNullOrEmpty(p.note)) sb.Append("<color=#D8982F>").Append(p.note).Append("</color>\n");
            sb.Append("现在卖 ").Append(p.HoldQty).Append(" 个最好的站：");
            for (int i = 0; i < Math.Min(4, p.alternatives.Count); i++)
                sb.Append(i == 0 ? "" : "；").Append(p.alternatives[i].Key.name).Append(" ").Append(Fmt.Money(p.alternatives[i].Value))
                  .Append(p.alternatives[i].Key.Exact ? "" : "(估)");
            sb.Append('\n');
            sb.Append("利润记录：");
            int from = Math.Max(0, p.history.Count - 8);
            for (int i = from; i < p.history.Count; i++)
                sb.Append(i == from ? "" : " → ").Append(p.history[i].time).Append(" ").Append(Fmt.Money(p.history[i].profit));
            if (p.history.Count == 0) sb.Append("还没有");
            detail.text = sb.ToString();
        }

        static string RoutesText()
        {
            var routes = Plans.Routes();
            if (routes.Count == 0) return "<color=#A4A5A6>点一行看这个计划的细节：计划时和现在的价格、利润变化记录、现在卖到哪最好。完成的计划多了，这里会按路线汇总。</color>";
            var sb = new System.Text.StringBuilder("<b>按路线汇总（已完成）</b>   <color=#A4A5A6>点一行看单个计划</color>\n");
            for (int i = 0; i < Math.Min(6, routes.Count); i++)
            {
                var r = routes[i];
                sb.Append(r.name).Append("  ").Append(r.from).Append(" → ").Append(r.to).Append("   ").Append(r.count).Append(" 次 ").Append(r.units).Append(" 个   ")
                  .Append(r.held ? "到手 " + Fmt.Money(r.profit) : "共净赚 " + Signed(r.profit) + "，平均 " + Signed(r.profit / r.count)).Append('\n');
            }
            if (routes.Count > 6) sb.Append("<color=#A4A5A6>还有 ").Append(routes.Count - 6).Append(" 条路线</color>");
            return sb.ToString();
        }

        public override void OnSnapshot(Snapshot s)
        {
            base.OnSnapshot(s);
            Refresh();
        }
    }
}
