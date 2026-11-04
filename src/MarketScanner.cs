using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace TradeHelper
{
    // One goods line in one market, with everything needed to price the k-th unit bought or sold.
    public class GoodsQuote
    {
        public MarketInfo market;
        public string gid;
        public string name;
        public string category;
        public int type;
        public string itemId;
        public bool special;
        public bool contraband;
        public bool buyable;          // normal goods: only showcase items can be bought
        public int stock;
        public float tax;
        public bool exact;            // market owes no settlement days: inputs come from the game's own price functions
        public float baseFactor;      // normal: price factor before stock/pressure; special: pBase incl. event
        public float supplyS, ePool;
        public double depth, pressure;
        public float minCoef;
        public FWExchangeSpaceNormalGoods normal;
        public double buy1, sell1;    // first unit, tax included (cached at scan time)

        const double Ln4Half = 0.69314718055994530942; // ln(4)/2, FWTradePricePressure.CurveLog(4, 2)

        static double Mult(double p, double d)
        {
            if (d <= 0) return 1;
            if (p >= 2 * d) return 4.0;
            return Math.Exp(Ln4Half * p / d);
        }

        double NormalAt(double s, double p)
        {
            double bl = supplyS * (0.5 - s / Math.Max(1f, ePool));
            return baseFactor * Math.Max(0.0, 1.0 + bl) * Mult(p, depth);
        }

        double SpecialAt(double occupancy)
        {
            float e = Math.Max(1f, ePool);
            double store = 1.0 - Math.Max(0.0, occupancy - e) / e;
            return baseFactor * Math.Max(minCoef, store);
        }

        // Price before tax of the i-th unit (0-based) the player buys. Special goods follow
        // FWExchangeSpaceSpecialGoods.calculatePrice: each unit is priced at the occupancy before it moves.
        public double BuyUnit(int i)
        {
            if (special) return SpecialAt(stock - i);
            return NormalAt(stock - i - 0.5, pressure + i + 0.5);
        }

        public double SellUnit(int i)
        {
            if (special) return SpecialAt(stock + i);
            return NormalAt(stock + i + 0.5, pressure - i - 0.5);
        }

        // Sum of k unit prices. Special goods (at most a few dozen units) are summed unit by unit. Normal goods can be
        // tens of thousands of units: they are summed in runs of depth/500 units priced at the run's midpoint; the price
        // changes by under 0.2% across such a run, so the total differs from the unit-by-unit sum by far less than that.
        double SumUnits(int k, bool buying)
        {
            double sum = 0;
            int run = special ? 1 : Math.Max(1, (int)(depth / 500));
            for (int i = 0; i < k; i += run)
            {
                int n = Math.Min(run, k - i);
                double mid = i + (n - 1) / 2.0;
                if (special) sum += buying ? BuyUnit(i) : SellUnit(i);
                else sum += n * (buying ? NormalAt(stock - mid - 0.5, pressure + mid + 0.5) : NormalAt(stock + mid + 0.5, pressure - mid - 0.5));
            }
            return sum;
        }

        // Totals: normal goods in an up-to-date market use the game's own quote (exact); otherwise the model above.
        public double BuyCost(int k)
        {
            if (exact && normal != null)
            {
                try { return normal.QuoteTrade(-k).rawPrice * (1 + tax); } catch { }
            }
            return SumUnits(k, true) * (1 + tax);
        }

        public double SellRevenue(int k)
        {
            if (exact && normal != null)
            {
                try { return normal.QuoteTrade(k).rawPrice * (1 - tax); } catch { }
            }
            return SumUnits(k, false) * (1 - tax);
        }
    }

    public class MarketInfo
    {
        public long uid;
        public string name;
        public string galaxyId;
        public bool planet;
        public bool initialized;
        public int pendingDays;
        public float tax;
        public FWExchangeSpace ex;
        public List<GoodsQuote> goods = new List<GoodsQuote>();
        public Dictionary<string, GoodsQuote> byGid = new Dictionary<string, GoodsQuote>();
        public double fleetAu = -1;
        public bool Exact => initialized && pendingDays == 0;
        public string Freshness => !initialized ? "未开过" : pendingDays == 0 ? "实时" : ("欠" + pendingDays + "天");
    }

    public class InvLocation
    {
        public string name;
        public string galaxyId;
        public Dictionary<string, int> items = new Dictionary<string, int>(); // key "type_id"
    }

    public class Snapshot
    {
        public DateTime takenAt;
        public long credits;
        public string fleetGalaxyId;
        public List<MarketInfo> markets = new List<MarketInfo>();
        public Dictionary<string, string> galaxyName = new Dictionary<string, string>();
        public List<InvLocation> inventory = new List<InvLocation>();
        public Dictionary<string, string> itemKeyToGid = new Dictionary<string, string>();
        public Dictionary<string, string> itemNames = new Dictionary<string, string>();
        public Dictionary<string, string> gidCategory = new Dictionary<string, string>();
        public List<string> errors = new List<string>();
        public List<TradeIdea> trades = new List<TradeIdea>();
        public List<SellIdea> sells = new List<SellIdea>();
        public long scanMs;
        public int marketsDone, marketsTotal;
        public bool complete;
        readonly Dictionary<string, double> routeCache = new Dictionary<string, double>();

        // Route distance in AU exactly as the game shows it between galaxies (stargate route through the
        // galaxy field, GalaxyDistanceUnit.TryCalculateGalaxyFieldRouteToAu). 0 inside one galaxy, -1 unknown.
        public double RouteAu(string g1, string g2)
        {
            if (string.IsNullOrEmpty(g1) || string.IsNullOrEmpty(g2)) return -1;
            if (g1 == g2) return 0;
            string key = string.CompareOrdinal(g1, g2) < 0 ? g1 + "|" + g2 : g2 + "|" + g1;
            double v;
            if (routeCache.TryGetValue(key, out v)) return v;
            v = -1;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                double au;
                if (GalaxyDistanceUnit.TryCalculateGalaxyFieldRouteToAu(g1, g2, out au)) v = au;
            }
            catch { }
            Budget.tRoute += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            routeCache[key] = v;
            return v;
        }

        // How many of an item the player holds right now (all inventory locations together).
        public int Owned(string key)
        {
            int n = 0;
            foreach (var l in inventory) { int v; if (l.items.TryGetValue(key, out v)) n += v; }
            return n;
        }

        public string GalaxyName(string id)
        {
            if (id == null) return "?";
            string n;
            return galaxyName.TryGetValue(id, out n) ? n : id;
        }
    }

    // Builds a Snapshot a little at a time (IEnumerator stepped from Update with a time budget), so opening the
    // panel never freezes the game. Every number is computed exactly as before; only the work is spread out.
    public static class MarketScanner
    {
        public static FWGameData Game
        {
            get
            {
                try { return UserBean.getInstance()?.gameData; } catch { return null; }
            }
        }

        static float EventPrice(long uid, string gid)
        {
            try { return FWGalaxyEventTradeEffects.Get(uid, gid).Price; } catch { return 1f; }
        }

        static float SnapshotTax(FWExchangeSpace ex)
        {
            try
            {
                if (ex.economicRevisions != null)
                {
                    FWExchangeEconomicDaySnapshot s;
                    if (ex.economicRevisions.TryGetValue(ex.latestEconomicRevision, out s) && s?.common != null && s.common.Length > 6)
                        return s.common[6];
                }
            }
            catch { }
            return 0.3f;
        }

        public static IEnumerable<KeyValuePair<FWExchangeSpace, MarketInfo>> EnumerateExchanges(FWGameData gd)
        {
            var gals = gd?.galaxyBlock?.knownGalaxies;
            if (gals == null) yield break;
            for (int gi = 0; gi < gals.Count; gi++)
            {
                var g = gals[gi];
                if (g == null) continue;
                string gname;
                try { gname = g.getName(); } catch { gname = "星系" + g.id; }
                if (g.exchangeSpace != null)
                    yield return new KeyValuePair<FWExchangeSpace, MarketInfo>(g.exchangeSpace, new MarketInfo { uid = g.exchangeSpace.uid, name = gname + " 空间站", galaxyId = g.id });
                if (g.stars == null) continue;
                foreach (var st in g.stars)
                {
                    if (st?.exchangeSpace == null) continue;
                    yield return new KeyValuePair<FWExchangeSpace, MarketInfo>(st.exchangeSpace, new MarketInfo { uid = st.exchangeSpace.uid, name = (st.starName ?? st.starId) + "（星球市场）", galaxyId = g.id, planet = true });
                }
            }
        }

        // The game's own first category label (Trade_Goods.TagsDesc), e.g. 材料 / 智慧生物 / 舰载机 / 服装.
        static string MainCategory(Trade_Goods_Def def)
        {
            var t = def?.TagsDesc;
            if (string.IsNullOrEmpty(t)) return "其他";
            int i = t.IndexOf('|');
            return i < 0 ? t : t.Substring(0, i);
        }

        // One step = a small piece of work. The caller keeps stepping until the time budget runs out.
        public static IEnumerator Scan(Snapshot snap, Config cfg)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            Budget.Reset();
            var gd = Game;
            snap.takenAt = DateTime.Now;
            if (gd == null) { snap.errors.Add("还没进入存档"); yield break; }
            snap.credits = gd.creditPoint;
            try { snap.fleetGalaxyId = gd.galaxyBlock?.playerFleet?.pos?.galaxyId; } catch { }
            try
            {
                foreach (var def in ImplDef<Trade_Goods_Def>.beans)
                {
                    if (def == null) continue;
                    snap.itemKeyToGid[def._type + "_" + def._id] = def.ID;
                    snap.itemNames[def.ID] = def.Name;
                    snap.gidCategory[def.ID] = MainCategory(def);
                }
            }
            catch (Exception e) { snap.errors.Add("读商品表失败: " + e.Message); }
            var gals = gd.galaxyBlock?.knownGalaxies;
            if (gals != null)
                foreach (var g in gals)
                {
                    if (g == null || g.id == null) continue;
                    try { snap.galaxyName[g.id] = g.getName(); } catch { snap.galaxyName[g.id] = g.id; }
                }
            yield return null;

            var list = new List<KeyValuePair<FWExchangeSpace, MarketInfo>>(EnumerateExchanges(gd));
            snap.marketsTotal = list.Count;
            foreach (var kv in list)
            {
                snap.marketsDone++;
                var m = kv.Value;
                snap.markets.Add(m);
                m.fleetAu = snap.RouteAu(snap.fleetGalaxyId, m.galaxyId);
                IEnumerator inner = null;
                try { inner = ScanMarket(kv.Key, m, snap); }
                catch (Exception e) { snap.errors.Add(m.name + ": " + e.Message); }
                while (inner != null)
                {
                    bool more;
                    try { more = inner.MoveNext(); }
                    catch (Exception e) { snap.errors.Add(m.name + ": " + e.Message); break; }
                    if (!more) break;
                    yield return null;
                }
                yield return null;
            }

            long ti = System.Diagnostics.Stopwatch.GetTimestamp();
            try { ScanInventory(gd, snap); }
            catch (Exception e) { snap.errors.Add("读库存失败: " + e.Message); }
            Budget.tInventory += System.Diagnostics.Stopwatch.GetTimestamp() - ti;
            yield return null;

            var adv = Advisor.Arbitrage(snap, cfg);
            while (true)
            {
                bool more;
                try { more = adv.MoveNext(); }
                catch (Exception e) { snap.errors.Add("算跑商建议失败: " + e.Message); break; }
                if (!more) break;
                yield return null;
            }
            var sel = Advisor.SellOwnStock(snap, cfg);
            while (true)
            {
                bool more;
                try { more = sel.MoveNext(); }
                catch (Exception e) { snap.errors.Add("算卖货建议失败: " + e.Message); break; }
                if (!more) break;
                yield return null;
            }
            snap.scanMs = total.ElapsedMilliseconds;
            snap.complete = true;
        }

        // Region economics of one goods line in one market: tax rate, market capacity (EPool) and leaning (LStellar).
        // The game derives them from the galaxy's trade-tag rules; they only move when the market's economic inputs
        // change, which the game records as a new economic revision. They are cached per market and revision, and every
        // scan re-reads a random sample live: any difference throws the market's cache away (counted in perf.log).
        class Econ { public float tax, pool, lean; }
        class MarketCache { public long revision = long.MinValue; public readonly Dictionary<string, Econ> byGid = new Dictionary<string, Econ>(); }
        static readonly Dictionary<long, MarketCache> econCache = new Dictionary<long, MarketCache>();
        static readonly System.Random rng = new System.Random();
        const int SamplesPerMarket = 8;

        public static void ClearCache() { econCache.Clear(); }

        static Econ ReadEcon(FWExchangeSpace ex, FWExchangeSpaceGoods it, Trade_Goods_Def def, float fallbackTax)
        {
            var e = new Econ();
            long t0 = Perf.Now;
            try { e.tax = ex.getTaxRate(it.GoodID); } catch { e.tax = fallbackTax; }
            long t1 = Perf.Now; Budget.tTax += t1 - t0;
            try { e.pool = ex.getEPool(it.GoodID); } catch { e.pool = def.MarketSize; }
            long t2 = Perf.Now; Budget.tPool += t2 - t1;
            try { e.lean = it.getLStellar(); } catch { e.lean = 1f; }
            Budget.tBase += Perf.Now - t2;
            return e;
        }

        static bool Same(Econ a, Econ b) => a.tax == b.tax && a.pool == b.pool && a.lean == b.lean;

        static IEnumerator ScanMarket(FWExchangeSpace ex, MarketInfo m, Snapshot snap)
        {
            m.ex = ex;
            m.initialized = ex.storeInitialized && ex.itemList != null;
            m.pendingDays = ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0;
            m.tax = SnapshotTax(ex);
            if (!m.initialized) yield break;
            bool exact = m.Exact;
            float S = 1f, BG = 0f;
            try { S = ex.getTradeS(); } catch { }
            try { BG = ex.getBGlobal(); } catch { }
            var items = new List<FWExchangeSpaceGoods>(ex.itemList); // the list belongs to the game: copy

            MarketCache mc;
            econCache.TryGetValue(ex.uid, out mc);
            if (exact)
            {
                if (mc == null || mc.revision != ex.latestEconomicRevision)
                {
                    mc = new MarketCache { revision = ex.latestEconomicRevision };
                    econCache[ex.uid] = mc;
                }
                else if (mc.byGid.Count > 0)
                {
                    // Spot-check the cache against live values before trusting it.
                    for (int k = 0; k < SamplesPerMarket && items.Count > 0; k++)
                    {
                        var it = items[rng.Next(items.Count)];
                        Econ cached;
                        if (it == null || it.GoodID == null || !mc.byGid.TryGetValue(it.GoodID, out cached)) continue;
                        var def = it.getGoodsDef();
                        if (def == null) continue;
                        Perf.cacheChecks++;
                        if (!Same(cached, ReadEcon(ex, it, def, m.tax)))
                        {
                            Perf.cacheMismatch++;
                            Debug.Log("[TradeHelper] economics cache mismatch in " + m.name + " (" + it.GoodID + "): cache dropped");
                            mc.byGid.Clear();
                            break;
                        }
                    }
                }
            }

            foreach (var it in items)
            {
                if (Budget.Over)
                {
                    yield return null;
                    // A new game day may have queued a settlement day while we were paused: from then on this
                    // market must not be touched by the exact (settling) price functions.
                    if (exact && ex.hasPendingSettlement()) { exact = false; m.pendingDays = ex.pendingSettlementDays.Count; }
                }
                if (it == null || string.IsNullOrEmpty(it.GoodID)) continue;
                long t0 = Perf.Now;
                Trade_Goods_Def def = null;
                try { def = it.getGoodsDef(); } catch { }
                Budget.tDef += Perf.Now - t0;
                if (def == null) continue;
                Budget.nGoods++;
                string cat;
                snap.gidCategory.TryGetValue(it.GoodID, out cat);
                var q = new GoodsQuote { market = m, gid = it.GoodID, name = def.Name, category = cat ?? "其他", type = def._type, itemId = def._id, contraband = it.isContraband, exact = exact };

                Econ e = null;
                if (mc != null && mc.byGid.TryGetValue(it.GoodID, out e)) Perf.cacheHits++;
                else if (exact)
                {
                    e = ReadEcon(ex, it, def, m.tax);
                    mc.byGid[it.GoodID] = e;
                    Perf.cacheMisses++;
                }
                // A market that owes days keeps the last values read while it was up to date (an estimate).
                float lean = e != null ? e.lean : 1f;
                if (e != null) { q.tax = e.tax; q.ePool = e.pool; }
                else
                {
                    try { q.tax = ex.getTaxRate(it.GoodID); } catch { q.tax = m.tax; }
                    try { q.ePool = ex.getEPool(it.GoodID); } catch { q.ePool = def.MarketSize; }
                }
                if (q.ePool <= 0) q.ePool = Math.Max(1, def.MarketSize);
                long te = Perf.Now;
                float ev = EventPrice(ex.uid, it.GoodID);
                Budget.tEvent += Perf.Now - te;

                var ng = it as FWExchangeSpaceNormalGoods;
                if (ng != null)
                {
                    if (ng.thing == null) continue;
                    q.normal = ng;
                    q.stock = ng.thing.sum;
                    q.buyable = ng.show && !ng.isContraband;
                    q.supplyS = S;
                    q.pressure = ng.tradePressure;
                    q.depth = Math.Min(def.MarketSize / 20.0, def.Production * 2.0); // = FWTradePricePressure.Depth
                    // Same arithmetic as FWExchangeSpaceNormalGoods.GetBasePriceFactor(includeEvent: true).
                    q.baseFactor = (float)((double)def.Price * (double)lean * Math.Max(0.0, 1.0 + (double)BG) * (double)ng.PriceNoise * (double)ev);
                }
                else
                {
                    var sg = it as FWExchangeSpaceSpecialGoods;
                    if (sg == null) continue;
                    q.special = true;
                    q.stock = sg.goodsList != null ? sg.goodsList.Count : 0;
                    q.buyable = !sg.isContraband && q.stock > 0;
                    q.minCoef = sg.itemType == 550 ? 0.5f : 0.1f;
                    // Same arithmetic as FWExchangeSpaceSpecialGoods.getPBase(), times the event price factor that
                    // calculatePrice applies.
                    float num = 1f + Mathf.Max(-1f, sg.BLocal); if (num < 0f) num = 0f;
                    float num2 = 1f + BG; if (num2 < 0f) num2 = 0f;
                    q.baseFactor = Mathf.Round(def.Price * lean * num2 * num * sg.PriceNoise) * ev;
                }
                q.buy1 = q.BuyUnit(0) * (1 + q.tax);
                q.sell1 = q.SellUnit(0) * (1 - q.tax);
                m.goods.Add(q);
                m.byGid[q.gid] = q;
            }
        }

        static void AddThings(InvLocation loc, IEnumerable<FWThingBean> things)
        {
            if (things == null) return;
            foreach (var t in things)
            {
                if (t == null || t.sum <= 0) continue;
                var k = t.type + "_" + t.id;
                int v; loc.items.TryGetValue(k, out v);
                loc.items[k] = v + t.sum;
            }
        }

        static void ScanInventory(FWGameData gd, Snapshot snap)
        {
            var seen = new HashSet<long>();
            FWStarMapInfo mother = null;
            try { mother = gd.getMothershipStarMapInfo(); } catch { }
            if (mother != null && mother.starSpread != null)
            {
                var loc = new InvLocation { name = "母舰", galaxyId = snap.fleetGalaxyId };
                AddThings(loc, new List<FWThingBean>(mother.starSpread.things));
                seen.Add(mother.starMapId);
                snap.inventory.Add(loc);
            }
            var gals = gd.galaxyBlock?.knownGalaxies;
            if (gals != null)
                foreach (var g in gals)
                {
                    if (g?.stars == null) continue;
                    foreach (var st in g.stars)
                    {
                        if (st?.starMapList == null) continue;
                        foreach (var map in st.starMapList)
                        {
                            if (map == null || map.starSpread == null || seen.Contains(map.starMapId)) continue;
                            if (map.type != FWStarMapInfo.TYPE_DEPUTY_BASE && map.type != FWStarMapInfo.TYPE_ROOT_BASE) continue;
                            seen.Add(map.starMapId);
                            var loc = new InvLocation { name = (string.IsNullOrEmpty(map.name) ? (st.starName ?? st.starId) : map.name) + "（基地）", galaxyId = g.id };
                            AddThings(loc, new List<FWThingBean>(map.starSpread.things));
                            snap.inventory.Add(loc);
                        }
                    }
                }
            try
            {
                var fleet = gd.galaxyBlock?.playerFleet;
                if (fleet != null)
                {
                    var loc = new InvLocation { name = (string.IsNullOrEmpty(fleet.name) ? "舰队" : fleet.name) + "（舰队货仓）", galaxyId = fleet.pos?.galaxyId };
                    var things = fleet.getThings();
                    if (things != null) AddThings(loc, new List<FWThingBean>(things));
                    snap.inventory.Add(loc);
                }
            }
            catch (Exception e) { snap.errors.Add("读舰队货仓失败: " + e.Message); }
            try
            {
                // Space station warehouses (things bought into "空间站仓库").
                if (gd.spaceportList != null)
                    foreach (var sp in new List<FWSpaceport>(gd.spaceportList))
                    {
                        if (sp?.things == null || sp.things.Count == 0) continue;
                        var loc = new InvLocation { name = snap.GalaxyName(sp.galaxyId) + " 空间站仓库", galaxyId = sp.galaxyId };
                        AddThings(loc, new List<FWThingBean>(sp.things));
                        if (loc.items.Count > 0) snap.inventory.Add(loc);
                    }
            }
            catch (Exception e) { snap.errors.Add("读空间站仓库失败: " + e.Message); }
            if (gd.dnaList != null && gd.dnaList.Count > 0)
            {
                var loc = new InvLocation { name = "DNA 库", galaxyId = snap.fleetGalaxyId };
                foreach (var d in new List<FWRoleDNA>(gd.dnaList))
                {
                    if (d == null) continue;
                    var k = "58_" + d.roleId;
                    int v; loc.items.TryGetValue(k, out v);
                    loc.items[k] = v + 1;
                }
                snap.inventory.Add(loc);
            }
        }

        public static string ItemName(Snapshot snap, string key)
        {
            string gid, n;
            if (snap.itemKeyToGid.TryGetValue(key, out gid) && snap.itemNames.TryGetValue(gid, out n)) return n;
            try
            {
                var p = key.Split(new[] { '_' }, 2);
                var d = DefLauncher.getDefBean(int.Parse(p[0]), p[1]);
                if (d != null) return d.Name;
            }
            catch { }
            return key;
        }
    }
}
