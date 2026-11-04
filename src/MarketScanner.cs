using System;
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

        const double Ln4Half = 0.69314718055994530942; // ln(4)/2

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

        // Price before tax of the i-th unit (0-based) the player buys.
        public double BuyUnit(int i)
        {
            if (special) return SpecialAt(stock - i);
            return NormalAt(stock - i - 0.5, pressure + i + 0.5);
        }

        // Price before tax of the i-th unit (0-based) the player sells.
        public double SellUnit(int i)
        {
            if (special) return SpecialAt(stock + i);
            return NormalAt(stock + i + 0.5, pressure - i - 0.5);
        }

        public double BuyCost(int k)
        {
            if (exact && normal != null)
            {
                try { return normal.QuoteTrade(-k).rawPrice * (1 + tax); } catch { }
            }
            double s = 0;
            for (int i = 0; i < k; i++) s += BuyUnit(i);
            return s * (1 + tax);
        }

        public double SellRevenue(int k)
        {
            if (exact && normal != null)
            {
                try { return normal.QuoteTrade(k).rawPrice * (1 - tax); } catch { }
            }
            double s = 0;
            for (int i = 0; i < k; i++) s += SellUnit(i);
            return s * (1 - tax);
        }
    }

    public class MarketInfo
    {
        public long uid;
        public string name;
        public string galaxyId;
        public int galaxyIndex;
        public bool planet;
        public float x, y;
        public bool initialized;
        public int pendingDays;
        public float tax;
        public FWExchangeSpace ex;
        public List<GoodsQuote> goods = new List<GoodsQuote>();
        public Dictionary<string, GoodsQuote> byGid = new Dictionary<string, GoodsQuote>();
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
        public Dictionary<string, MarketInfo> marketByGalaxy = new Dictionary<string, MarketInfo>();
        public Dictionary<string, float[]> galaxyPos = new Dictionary<string, float[]>();
        public Dictionary<string, string> galaxyName = new Dictionary<string, string>();
        public List<InvLocation> inventory = new List<InvLocation>();
        public Dictionary<string, string> itemKeyToGid = new Dictionary<string, string>();
        public Dictionary<string, string> itemNames = new Dictionary<string, string>();
        public List<string> errors = new List<string>();

        public float Distance(string g1, string g2)
        {
            if (g1 == null || g2 == null) return -1;
            float[] a, b;
            if (!galaxyPos.TryGetValue(g1, out a) || !galaxyPos.TryGetValue(g2, out b)) return -1;
            float dx = a[0] - b[0], dy = a[1] - b[1];
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }

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
                    yield return new KeyValuePair<FWExchangeSpace, MarketInfo>(g.exchangeSpace, new MarketInfo { uid = g.exchangeSpace.uid, name = gname + " 空间站", galaxyId = g.id, galaxyIndex = gi, x = g.posX, y = g.posY });
                if (g.stars == null) continue;
                foreach (var st in g.stars)
                {
                    if (st?.exchangeSpace == null) continue;
                    yield return new KeyValuePair<FWExchangeSpace, MarketInfo>(st.exchangeSpace, new MarketInfo { uid = st.exchangeSpace.uid, name = (st.starName ?? st.starId) + "（星球市场）", galaxyId = g.id, galaxyIndex = gi, planet = true, x = g.posX, y = g.posY });
                }
            }
        }

        public static Snapshot Take()
        {
            var snap = new Snapshot { takenAt = DateTime.Now };
            var gd = Game;
            if (gd == null) { snap.errors.Add("还没进入存档"); return snap; }
            snap.credits = gd.creditPoint;
            try { snap.fleetGalaxyId = gd.galaxyBlock?.playerFleet?.pos?.galaxyId; } catch { }

            try
            {
                foreach (var def in ImplDef<Trade_Goods_Def>.beans)
                {
                    if (def == null) continue;
                    snap.itemKeyToGid[def._type + "_" + def._id] = def.ID;
                    snap.itemNames[def.ID] = def.Name;
                }
            }
            catch (Exception e) { snap.errors.Add("读商品表失败: " + e.Message); }

            var gals = gd.galaxyBlock?.knownGalaxies;
            if (gals != null)
                foreach (var g in gals)
                {
                    if (g == null || g.id == null) continue;
                    snap.galaxyPos[g.id] = new[] { g.posX, g.posY };
                    try { snap.galaxyName[g.id] = g.getName(); } catch { snap.galaxyName[g.id] = g.id; }
                }

            foreach (var kv in EnumerateExchanges(gd))
            {
                var ex = kv.Key; var m = kv.Value;
                try { ScanMarket(ex, m, snap); }
                catch (Exception e) { snap.errors.Add(m.name + ": " + e.Message); }
                snap.markets.Add(m);
                if (!m.planet) snap.marketByGalaxy[m.galaxyId] = m;
            }

            try { ScanInventory(gd, snap); }
            catch (Exception e) { snap.errors.Add("读库存失败: " + e.Message); }
            return snap;
        }

        static void ScanMarket(FWExchangeSpace ex, MarketInfo m, Snapshot snap)
        {
            m.ex = ex;
            m.initialized = ex.storeInitialized && ex.itemList != null;
            m.pendingDays = ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0;
            m.tax = SnapshotTax(ex);
            if (!m.initialized) return;
            bool exact = m.Exact;
            float S = 1f;
            try { S = ex.getTradeS(); } catch { }
            // Iterate over a copy: the list belongs to the game.
            var items = new List<FWExchangeSpaceGoods>(ex.itemList);
            foreach (var it in items)
            {
                if (it == null || string.IsNullOrEmpty(it.GoodID)) continue;
                Trade_Goods_Def def = null;
                try { def = it.getGoodsDef(); } catch { }
                if (def == null) continue;
                var q = new GoodsQuote { market = m, gid = it.GoodID, name = def.Name, type = def._type, itemId = def._id, contraband = it.isContraband, exact = exact };
                try { q.tax = ex.getTaxRate(it.GoodID); } catch { q.tax = m.tax; }
                try { q.ePool = ex.getEPool(it.GoodID); } catch { q.ePool = def.MarketSize; }
                if (q.ePool <= 0) q.ePool = Math.Max(1, def.MarketSize);
                float ev = EventPrice(ex.uid, it.GoodID);
                var ng = it as FWExchangeSpaceNormalGoods;
                if (ng != null)
                {
                    if (ng.thing == null) continue;
                    q.normal = ng;
                    q.stock = ng.thing.sum;
                    q.buyable = ng.show && !ng.isContraband;
                    q.supplyS = S;
                    q.pressure = ng.tradePressure;
                    q.depth = Math.Min(def.MarketSize / 20.0, def.Production * 2.0);
                    if (exact)
                    {
                        try { q.baseFactor = (float)ng.GetBasePriceFactor(true); q.depth = ng.GetTradeDepth(); }
                        catch { q.exact = false; }
                    }
                    if (!q.exact) q.baseFactor = def.Price * ng.PriceNoise * ev;
                }
                else
                {
                    var sg = it as FWExchangeSpaceSpecialGoods;
                    if (sg == null) continue;
                    q.special = true;
                    q.stock = sg.goodsList != null ? sg.goodsList.Count : 0;
                    q.buyable = !sg.isContraband && q.stock > 0;
                    q.minCoef = sg.itemType == 550 ? 0.5f : 0.1f;
                    if (exact)
                    {
                        try { q.baseFactor = sg.getPBase() * ev; } catch { q.exact = false; }
                    }
                    if (!q.exact) q.baseFactor = Mathf.Round(def.Price * sg.PriceNoise * Mathf.Max(0f, 1f + sg.BLocal)) * ev;
                }
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
                var loc = new InvLocation { name = "母舰" };
                AddThings(loc, mother.starSpread.things);
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
                            AddThings(loc, map.starSpread.things);
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
                    AddThings(loc, fleet.getThings());
                    snap.inventory.Add(loc);
                }
            }
            catch (Exception e) { snap.errors.Add("读舰队货仓失败: " + e.Message); }
            if (gd.dnaList != null && gd.dnaList.Count > 0)
            {
                var loc = new InvLocation { name = "DNA 库" };
                foreach (var d in gd.dnaList)
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
