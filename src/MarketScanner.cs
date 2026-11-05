using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public FWExchangeSpaceSpecialGoods specialGoods;
        public float eventFactor = 1f; // galaxy event price factor used for this quote (FWGalaxyEventTradeEffects)
        public double occupancy;      // special: store occupancy = sum of the items' durability factors (getStoreOccupancy)
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

        // Ships (type 550). The trade page prices every ship on its own, at the market's occupancy when the page is shown
        // (PageTradeInventory.GetShipsForPanel -> FWExchangeSpace.getSpecialGoodsPriceToSell(ship) -> calculatePrice of
        // that one ship), for buying and selling alike, and keeps those prices until the checkout. So within a checkout
        // ships do not get dearer or cheaper one after another. FWExchangeSpaceSpecialGoods.getInstancePriceCoefficient:
        // pBase x max(0.5, pStore) x 1 / 0.4 / 0.1 / 0.01 for 0 / 1 / 2 / more defects.
        public bool ship;
        public List<FWAirShip> storeShips;   // in store, fewest defects first
        public int[] shipDefects;

        public static double DefectFactor(int defects) => defects <= 0 ? 1.0 : defects == 1 ? 0.4 : defects == 2 ? 0.1 : 0.01;

        public double ShipAt(int defects)
        {
            float e = Math.Max(1f, ePool);
            double store = 1.0 - Math.Max(0.0, occupancy - e) / e;
            return baseFactor * Math.Max(minCoef, store) * DefectFactor(defects);
        }

        public static int Defects(FWBaseBean b) { try { return b is FWAirShip s ? s.GetDefectCount() : 0; } catch { return 0; } }

        // Special goods (not ships) in store, best durability first, as the player would pick them: durability factor of
        // each item and the running sum before it. FWExchangeSpaceSpecialGoods.calculatePrice prices an item at
        // pBase x max(min, durability x pStore(occupancy)) and moves the occupancy by its durability.
        public float[] storeDur;
        public double[] durBefore;

        public void SetStoreDurability(List<float> durs)
        {
            durs.Sort((a, b) => b.CompareTo(a));
            storeDur = durs.ToArray();
            durBefore = new double[storeDur.Length + 1];
            for (int i = 0; i < storeDur.Length; i++) durBefore[i + 1] = durBefore[i] + storeDur[i];
        }

        double ItemAt(double durability, double occ)
        {
            float e = Math.Max(1f, ePool);
            double store = 1.0 - Math.Max(0.0, occ - e) / e;
            return baseFactor * Math.Max(minCoef, durability * store);
        }

        // Before tax: the i-th item (0-based) of a batch sold here after `from` full items, when the batch is the items
        // bought from another market's store starting at its `start`-th best item (their durability carries over).
        public double SellBoughtUnit(int from, GoodsQuote bought, int start, int i)
        {
            if (ship) return ShipAt(bought?.shipDefects != null && start + i < bought.shipDefects.Length ? bought.shipDefects[start + i] : 0);
            if (!special || bought?.storeDur == null || start + i >= bought.storeDur.Length) return SellUnit(from + i);
            double occ = occupancy + from + (bought.durBefore[start + i] - bought.durBefore[start]);
            return ItemAt(bought.storeDur[start + i], occ);
        }

        public double SellBoughtRevenue(int from, GoodsQuote bought, int start, int k)
        {
            if (ship)
            {
                double t = 0;
                for (int i = 0; i < k; i++) t += SellBoughtUnit(from, bought, start, i);
                return t * (1 - tax);
            }
            if (!special || bought?.storeDur == null || start + k > bought.storeDur.Length)
                return SellRevenue(from + k) - (from > 0 ? SellRevenue(from) : 0);
            double sum = 0;
            for (int i = 0; i < k; i++) sum += SellBoughtUnit(from, bought, start, i);
            return sum * (1 - tax);
        }

        // Price before tax of the i-th unit (0-based) the player buys. Special goods follow
        // FWExchangeSpaceSpecialGoods.calculatePrice: each unit is priced at the occupancy before it moves.
        public double BuyUnit(int i)
        {
            if (ship) return ShipAt(shipDefects != null && i < shipDefects.Length ? shipDefects[i] : 0);
            if (special)
            {
                if (storeDur != null && i < storeDur.Length) return ItemAt(storeDur[i], occupancy - durBefore[i]);
                return SpecialAt(occupancy - i);
            }
            return NormalAt(stock - i - 0.5, pressure + i + 0.5);
        }

        public double SellUnit(int i)
        {
            if (ship) return ShipAt(0);
            if (special) return SpecialAt(occupancy + i);
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
            if (exact && normal != null && StillExact())
            {
                try { return normal.QuoteTrade(-k).rawPrice * (1 + tax); } catch { }
            }
            return SumUnits(k, true) * (1 + tax);
        }

        // Revenue after tax for selling units [from, from + k) of the player's own items. Special goods in an up-to-date
        // market are priced by the game itself (FWExchangeSpaceSpecialGoods.getPriceForPlayerSell, the checkout's price:
        // it also weighs each item's durability, and moves the store by that durability). Otherwise the model.
        public double SellHeld(int from, int k, List<FWBaseBean> items)
        {
            if (k <= 0) return 0;
            if (ship && items != null && from + k <= items.Count)
            {
                // Each ship on its own, as the trade page prices it.
                bool live = exact && specialGoods != null && StillExact();
                double t = 0;
                for (int i = from; i < from + k; i++)
                {
                    double p = -1;
                    if (live) { try { p = specialGoods.getPriceForPlayerSell(items[i]); } catch { p = -1; } }
                    t += p >= 0 ? p : ShipAt(Defects(items[i]));
                }
                return t * (1 - tax);
            }
            if (special && exact && specialGoods != null && items != null && from + k <= items.Count && StillExact())
            {
                try
                {
                    // items: best durability first (Snapshot.HeldSorted), as calculatePrice orders them.
                    var sg = specialGoods;
                    double b2 = sg.getPriceForPlayerSell(items.GetRange(0, from + k));
                    double a2 = from > 0 ? sg.getPriceForPlayerSell(items.GetRange(0, from)) : 0;
                    if (b2 > 0) return (b2 - a2) * (1 - tax);
                }
                catch { }
            }
            return SellRevenue(from + k) - (from > 0 ? SellRevenue(from) : 0);
        }

        // The game's quote functions settle a market that owes days (EnsureSettledForUse). A quote marked exact at scan
        // time is only asked live while its market still owes nothing and is not on the game's worker thread.
        bool StillExact()
        {
            var ex = market?.ex;
            if (ex == null || ex.uid == Settlement.inFlightUid) return false;
            try { return !ex.hasPendingSettlement(); } catch { return false; }
        }

        public double SellRevenue(int k)
        {
            if (exact && normal != null && StillExact())
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
        public bool planet;          // a planet's ground market (地面交易行): used from a base on that planet, not by the fleet
        public string starId;
        public FWStarInfo star;      // planet market: its planet
        public string where = "";    // where in the game this market is opened
        public string groundBase;    // planet market: the player's base on that planet, if any
        public bool initialized;
        public int pendingDays;
        public float tax;
        public FWExchangeSpace ex;
        public List<GoodsQuote> goods = new List<GoodsQuote>();
        public Dictionary<string, GoodsQuote> byGid = new Dictionary<string, GoodsQuote>();
        public double fleetAu = -1;
        public long fingerprint;     // hash of everything the quotes were built from (see Fingerprint)
        // Galaxy exploration state (FWGalaxy.observaState, kept by GalaxyExplorationService): 0 = not discovered (not on
        // the player's map), 1 = discovered (on the map, the fleet has not been there), 2 = explored (the fleet has been there).
        public int observe;
        public bool Discovered => observe >= 1;
        public bool Explored => observe >= 2;
        // Suffix for names of markets the player has not been to.
        public string NotVisitedTag => Explored ? "" : Discovered ? "（未探索）" : "（未发现）";
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
        public Dictionary<string, List<MarketScanner.StationPlace>> stationPlaces = new Dictionary<string, List<MarketScanner.StationPlace>>(); // galaxy -> stations with a trade button
        public Dictionary<string, string> baseOnStar = new Dictionary<string, string>();               // starId -> player's base name
        public Dictionary<string, List<FWBaseBean>> heldItems = new Dictionary<string, List<FWBaseBean>>(); // key -> the player's items, one entry per unit
        public List<FWBaseBean> Held(string key) { List<FWBaseBean> l; return key != null && heldItems.TryGetValue(key, out l) ? l : null; }
        public void AddHeld(string key, FWBaseBean b) { List<FWBaseBean> l; if (!heldItems.TryGetValue(key, out l)) heldItems[key] = l = new List<FWBaseBean>(); l.Add(b); }
        readonly HashSet<string> heldSorted = new HashSet<string>();
        // The player's items of a goods, best durability first (the durability factor depends on the item only, so any
        // market's goods line can rank them). Sorted once per snapshot.
        public List<FWBaseBean> HeldSorted(string key, FWExchangeSpaceSpecialGoods sg)
        {
            var l = Held(key);
            if (l == null || sg == null || heldSorted.Contains(key)) return l;
            try { l.Sort((a, b) => sg.getPDurability(b).CompareTo(sg.getPDurability(a))); } catch { }   // ships: fewer defects = higher factor
            heldSorted.Add(key);
            return l;
        }
        public List<InvLocation> inventory = new List<InvLocation>();
        public Dictionary<string, string> itemKeyToGid = new Dictionary<string, string>();
        public Dictionary<string, string> itemNames = new Dictionary<string, string>();
        public Dictionary<string, string> gidCategory = new Dictionary<string, string>();
        public List<string> errors = new List<string>();
        public List<TradeIdea> trades = new List<TradeIdea>();
        // Every category of goods tradable between the usable markets, and for each one without a paying route, a line
        // about the best first-unit margin (after both taxes), so the page can show it instead of an empty table.
        public List<string> tradeCategories = new List<string>();
        public Dictionary<string, string> categoryNote = new Dictionary<string, string>();
        public List<SellIdea> sells = new List<SellIdea>();
        public long scanMs;
        public int marketsDone, marketsTotal, marketsReused;
        public bool complete, adviceReused;
        public string phase = "";
        // Memory allocated by each part of the scan (bytes; steps during which a garbage collection ran are not counted).
        public long allocRead, allocInventory, allocTrades, allocSells; public int allocSkipped;
        public double maxStepMs;
        public string maxStepPhase = "";
        public long inventoryHash;
        public string adviceKey;
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

        // The tax the player actually pays: PageTradeInventory.GetCurrentTaxRate() =
        // FWGlobalParameter.ApplyTradeTaxRate(exchange.getTaxRate()), applied to the whole cart at checkout
        // (buy x (1 + t), sell x (1 - t)). It is the market's rate (average of its towns/stations), not the per-goods
        // getTaxRate(goodsId), which only feeds the market's own history. getTaxRate() reads the stations' rates and does
        // not settle the market.
        public static float TradeTax(FWExchangeSpace ex)
        {
            try { return FWGlobalParameter.ApplyTradeTaxRate(ex.getTaxRate()); }
            catch { return SnapshotTax(ex); }
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

        // The name a planet has on the game's maps: the star definition's replacement name when it has one, otherwise
        // getStarName() (GalaxyMapView labels planets this way; 塞拉遗迹 is a replacement name).
        public static string PlanetName(FWStarInfo st)
        {
            if (st == null) return "?";
            try
            {
                var def = st.getStarDef();
                if (def != null && !string.IsNullOrWhiteSpace(def.ReplaceName) && def.ReplaceName != "-1")
                {
                    var t = LanguageDef.getText(def.ReplaceName);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
                }
            }
            catch { }
            try { var n = st.getStarName(); if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
            return st.starName ?? st.starId;
        }

        // Stations whose menu has the trade button (PageInGalaxySpace.setPlatfrom: a trade centre in the station's
        // definition, or a story facility that StoryFacilityRules.CanTrade allows).
        public static bool HasTradeButton(FWGameData gd, FWSpaceport sp)
        {
            if (sp == null) return false;
            try
            {
                if (sp.IsStoryFacility) return StoryFacilityRules.CanTrade(gd, sp);
                var def = sp.getDef();
                return def != null && def.HasTradeCenterID();
            }
            catch { return false; }
        }

        public class StationPlace { public string name; }

        // Stations of a galaxy whose menu opens its market. Stations are not tied to planets; only the galaxy and the
        // station's own name tell the player where to go.
        public static List<StationPlace> TradeStations(FWGameData gd, FWGalaxy g)
        {
            var res = new List<StationPlace>();
            if (gd?.spaceportList == null || g == null) return res;
            foreach (var sp in new List<FWSpaceport>(gd.spaceportList))
                if (sp != null && sp.galaxyId == g.id && HasTradeButton(gd, sp)) res.Add(new StationPlace { name = SpName(sp) });
            return res;
        }

        // Where each market is used in the game. A galaxy's market (贸易市场) opens from the「交易」button of a space station in
        // that galaxy (PageInGalaxySpace.showExChange; story stations only when StoryFacilityRules.CanTrade). A planet's market
        // (地面交易行) opens only from a trade building in a base on that planet and trades with that base's ground storage
        // (ViewBuild_Fctions_Base.openTradeShopPage), so the fleet cannot sell there directly.
        static void BuildWhere(FWGameData gd, Snapshot snap)
        {
            snap.stationPlaces.Clear(); snap.baseOnStar.Clear();
            try
            {
                var galaxies = gd.galaxyBlock?.knownGalaxies;
                if (galaxies != null)
                    foreach (var g in galaxies)
                        if (g?.id != null && g.exchangeSpace != null) snap.stationPlaces[g.id] = TradeStations(gd, g);
            }
            catch (Exception e) { snap.errors.Add("读空间站位置失败: " + e.Message); }
            var gals = gd.galaxyBlock?.knownGalaxies;
            if (gals != null)
                foreach (var g in gals)
                {
                    if (g?.stars == null) continue;
                    foreach (var st in g.stars)
                    {
                        if (st?.starMapList == null || st.starId == null) continue;
                        foreach (var map in st.starMapList)
                        {
                            if (map == null || (map.type != FWStarMapInfo.TYPE_DEPUTY_BASE && map.type != FWStarMapInfo.TYPE_ROOT_BASE)) continue;
                            if (!snap.baseOnStar.ContainsKey(st.starId)) snap.baseOnStar[st.starId] = string.IsNullOrEmpty(map.name) ? "你的基地" : map.name;
                        }
                    }
                }
        }

        static void SetWhere(MarketInfo m, Snapshot snap)
        {
            if (m.planet)
            {
                string b = null;
                if (m.starId != null) snap.baseOnStar.TryGetValue(m.starId, out b);
                m.groundBase = b;
                m.where = b != null ? "地面交易行：在你这颗星球上的基地「" + b + "」里用交易建筑打开，买卖的是基地地面仓库，舰队不能直接卖"
                                    : "地面交易行：只能在这颗星球上自己的基地里打开（你在这里没有基地），舰队不能直接卖";
            }
            else
            {
                // Named after the station(s) whose trade button opens it, as the player finds them on the map.
                List<StationPlace> l;
                string gname = snap.GalaxyName(m.galaxyId);
                if (snap.stationPlaces.TryGetValue(m.galaxyId ?? "", out l) && l.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var p in l) if (!names.Contains(p.name)) names.Add(p.name);
                    m.name = gname + "·" + string.Join("/", names.Count > 2 ? names.GetRange(0, 2) : names) + (names.Count > 2 ? "等" : "");
                    m.where = "在 " + gname + " 星系的 " + string.Join("、", names) + " 点「贸易市场」";
                }
                else
                {
                    m.name = gname + "·贸易市场";
                    m.where = gname + " 星系现在没有能打开贸易市场的空间站";
                }
            }
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
                    yield return new KeyValuePair<FWExchangeSpace, MarketInfo>(g.exchangeSpace, new MarketInfo { uid = g.exchangeSpace.uid, name = gname + " 贸易市场", galaxyId = g.id, observe = g.observaState });
                if (g.stars == null) continue;
                foreach (var st in g.stars)
                {
                    if (st?.exchangeSpace == null) continue;
                    string sname = PlanetName(st);
                    yield return new KeyValuePair<FWExchangeSpace, MarketInfo>(st.exchangeSpace, new MarketInfo { uid = st.exchangeSpace.uid, name = sname + " 地面交易行", galaxyId = g.id, planet = true, starId = st.starId, star = st, observe = g.observaState });
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

        // Last completed snapshot: stations whose fingerprint did not change are carried over from it unchanged.
        public static Snapshot lastComplete;

        static long Mix(long h, long v) { unchecked { return (h ^ v) * 1099511628211L; } }
        static long Mix(long h, double v) => Mix(h, BitConverter.DoubleToInt64Bits(v));

        // Hash of every input the quotes of one station are computed from: owed days, economic revision, trade
        // sensitivity, global pressure, the event rules active right now, and per goods line its stock, trade
        // pressure, price noise, local supply, showcase and contraband flags. Same hash = same numbers.
        static long Fingerprint(FWExchangeSpace ex)
        {
            long h = unchecked((long)14695981039346656037UL);
            h = Mix(h, (long)(ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0));
            h = Mix(h, ex.storeInitialized ? 1L : 0L);
            h = Mix(h, ex.latestEconomicRevision);
            h = Mix(h, (double)TradeTax(ex));
            try { h = Mix(h, (double)ex.getTradeS()); h = Mix(h, (double)ex.getBGlobal()); } catch { }
            try
            {
                var gd = Game;
                var ev = FWGalaxyEventTradeEffects.CaptureInputs(ex.uid, gd != null ? gd.elapsedMM : 0);
                if (ev?.rules != null)
                    foreach (var r in ev.rules)
                    {
                        if (r == null) continue;
                        h = Mix(h, (long)(r.effectId ?? "").GetHashCode());
                        h = Mix(h, (long)(r.requireGoodsIds ?? "").GetHashCode());
                        h = Mix(h, (long)(r.requireTags ?? "").GetHashCode());
                        h = Mix(h, (double)r.price);
                    }
            }
            catch { h = Mix(h, -7L); }
            var items = ex.itemList;
            if (items == null) return h;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null) continue;
                h = Mix(h, (double)it.PriceNoise);
                h = Mix(h, (double)it.BLocal);
                h = Mix(h, it.isContraband ? 3L : 5L);
                var ng = it as FWExchangeSpaceNormalGoods;
                if (ng != null)
                {
                    h = Mix(h, (long)(ng.thing != null ? ng.thing.sum : -1));
                    h = Mix(h, ng.tradePressure);
                    h = Mix(h, ng.show ? 11L : 13L);
                }
                else
                {
                    var sg = it as FWExchangeSpaceSpecialGoods;
                    if (sg != null) h = Mix(h, (long)(sg.goodsList != null ? sg.goodsList.Count : -1));
                }
            }
            return h;
        }

        // The economics cache spot check, also run on stations that are carried over.
        static bool SpotCheck(FWExchangeSpace ex, MarketInfo m, MarketCache mc, List<FWExchangeSpaceGoods> items)
        {
            if (mc == null || mc.byGid.Count == 0 || items == null || items.Count == 0) return true;
            for (int k = 0; k < SamplesPerMarket; k++)
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
                    return false;
                }
            }
            return true;
        }

        // One step = a small piece of work. The caller keeps stepping until the time budget runs out.
        public static IEnumerator Scan(Snapshot snap, Config cfg)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            Budget.Reset();
            var gd = Game;
            snap.takenAt = DateTime.Now;
            if (gd == null) { snap.errors.Add("还没进入存档"); yield break; }
            if (!diskLoaded) LoadDiskCache();
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
            BuildWhere(gd, snap);
            var prev = lastComplete;
            var prevByUid = new Dictionary<long, MarketInfo>();
            if (prev != null) foreach (var pm in prev.markets) prevByUid[pm.uid] = pm;
            foreach (var kv in list)
            {
                snap.marketsDone++;
                var m = kv.Value;
                snap.phase = "读站 " + m.name;
                MarketInfo old;
                // Being settled on the game's worker thread: do not read it now; keep what the last scan had.
                if (kv.Key.uid == Settlement.inFlightUid)
                {
                    if (prevByUid.TryGetValue(m.uid, out old) && old.ex == kv.Key)
                    {
                        old.name = m.name; old.observe = m.observe; old.starId = m.starId; old.star = m.star;
                        SetWhere(old, snap);
                        old.fleetAu = snap.RouteAu(snap.fleetGalaxyId, old.galaxyId);
                        snap.markets.Add(old);
                        snap.marketsReused++;
                    }
                    continue;
                }
                long fp = 0;
                try { fp = Fingerprint(kv.Key); } catch { fp = 0; }
                if (fp != 0 && prevByUid.TryGetValue(m.uid, out old) && old.fingerprint == fp && old.ex == kv.Key)
                {
                    MarketCache omc;
                    econCache.TryGetValue(m.uid, out omc);
                    bool ok = !old.Exact || SpotCheck(kv.Key, old, omc, kv.Key.itemList != null ? new List<FWExchangeSpaceGoods>(kv.Key.itemList) : null);
                    if (ok)
                    {
                        // Nothing this station's numbers depend on has changed: keep its quotes.
                        old.name = m.name; old.observe = m.observe; old.starId = m.starId;
                        SetWhere(old, snap);
                        old.fleetAu = snap.RouteAu(snap.fleetGalaxyId, old.galaxyId);
                        snap.markets.Add(old);
                        snap.marketsReused++;
                        if (Budget.Over) yield return null;
                        continue;
                    }
                }
                m.fingerprint = fp;
                SetWhere(m, snap);
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

            snap.phase = "读库存";
            long ti = System.Diagnostics.Stopwatch.GetTimestamp();
            try { ScanInventory(gd, snap); }
            catch (Exception e) { snap.errors.Add("读库存失败: " + e.Message); }
            Budget.tInventory += System.Diagnostics.Stopwatch.GetTimestamp() - ti;
            yield return null;

            unchecked
            {
                long ih = 17;
                foreach (var loc in snap.inventory)
                    foreach (var kv2 in loc.items) ih = Mix(ih, (long)kv2.Key.GetHashCode() * 31 + kv2.Value);
                snap.inventoryHash = ih;
            }
            long explored = 17;
            unchecked { foreach (var m in snap.markets) explored = Mix(explored, m.uid * 3 + m.observe); }
            // Exploring a galaxy changes which markets advice may use, though no station's numbers change.
            snap.adviceKey = cfg.maxStaleDays + "|" + cfg.includeUndiscovered + "|" + cfg.includeGround + "|" + Plans.version + "|" + explored;
            if (prev != null && prev.complete && snap.marketsReused == snap.markets.Count && prev.markets.Count == snap.markets.Count &&
                prev.inventoryHash == snap.inventoryHash && prev.adviceKey == snap.adviceKey)
            {
                // Same stations, same holdings, same settings: the advice is the same; only distances to the fleet moved.
                snap.trades = prev.trades; snap.sells = prev.sells;
                foreach (var t in snap.trades) t.fleetAu = t.buy.market.fleetAu;
                foreach (var t in snap.sells) t.fleetAu = t.market.market.fleetAu;
                snap.adviceReused = true;
                snap.scanMs = total.ElapsedMilliseconds;
                snap.complete = true;
                lastComplete = snap;
                yield break;
            }
            snap.phase = "算跑商建议";
            var adv = Advisor.Arbitrage(snap, cfg);
            while (true)
            {
                bool more;
                try { more = adv.MoveNext(); }
                catch (Exception e) { snap.errors.Add("算跑商建议失败: " + e.Message); break; }
                if (!more) break;
                yield return null;
            }
            snap.phase = "算卖货建议";
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
            lastComplete = snap;
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

        // allowDisk: false keeps the next scan from reusing the cache file (a scan from scratch).
        public static void ClearCache(bool allowDisk = true)
        {
            econCache.Clear(); lastComplete = null; fromDisk.Clear();
            diskLoaded = !allowDisk;
        }

        // The cache is also kept on disk (econ-cache.bin in the mod's settings folder) for the save it was read from, so
        // a new game session does not have to ask the game for every goods line again. Markets taken from the file are
        // spot-checked with twice the usual sample the first time they are used; any difference drops that market.
        const int DiskFormat = 1;
        static bool diskLoaded, diskDirty;
        static readonly HashSet<long> fromDisk = new HashSet<long>();
        static string DiskPath => Path.Combine(Config.Dir, "econ-cache.bin");
        public static int DiskMarketsLoaded;

        static string ArchiveId()
        {
            try { return UserBean.getInstance()?.archive?.info?.EnsureArchiveId(); } catch { return null; }
        }

        static void LoadDiskCache()
        {
            diskLoaded = true;
            DiskMarketsLoaded = 0;
            var id = ArchiveId();
            if (id == null || !File.Exists(DiskPath)) return;
            try
            {
                using (var br = new BinaryReader(File.OpenRead(DiskPath)))
                {
                    if (br.ReadInt32() != DiskFormat || br.ReadString() != id) return;
                    int markets = br.ReadInt32();
                    for (int i = 0; i < markets; i++)
                    {
                        long uid = br.ReadInt64(), rev = br.ReadInt64();
                        int n = br.ReadInt32();
                        var mc = new MarketCache { revision = rev };
                        for (int j = 0; j < n; j++)
                        {
                            string gid = br.ReadString();
                            float pool = br.ReadSingle(), lean = br.ReadSingle();
                            mc.byGid[gid] = new Econ { pool = pool, lean = lean };
                        }
                        if (!econCache.ContainsKey(uid) && n > 0) { econCache[uid] = mc; fromDisk.Add(uid); DiskMarketsLoaded++; }
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] econ cache read failed: " + e.Message); econCache.Clear(); fromDisk.Clear(); DiskMarketsLoaded = 0; }
        }

        public static void SaveDiskCache()
        {
            if (!diskDirty) return;
            var id = ArchiveId();
            if (id == null) return;
            diskDirty = false;
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var tmp = DiskPath + ".tmp";
                using (var bw = new BinaryWriter(File.Create(tmp)))
                {
                    bw.Write(DiskFormat); bw.Write(id);
                    int count = 0; foreach (var kv in econCache) if (kv.Value.byGid.Count > 0) count++;
                    bw.Write(count);
                    foreach (var kv in econCache)
                    {
                        if (kv.Value.byGid.Count == 0) continue;
                        bw.Write(kv.Key); bw.Write(kv.Value.revision); bw.Write(kv.Value.byGid.Count);
                        foreach (var g in kv.Value.byGid) { bw.Write(g.Key); bw.Write(g.Value.pool); bw.Write(g.Value.lean); }
                    }
                }
                File.Copy(tmp, DiskPath, true);
                File.Delete(tmp);
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] econ cache write failed: " + e.Message); }
        }

        // Test support: every cached value against the game's live functions (neither settles a market).
        public static void CompareCacheWithLive(Snapshot snap, out int markets, out int compared, out int differ)
        {
            markets = compared = differ = 0;
            foreach (var m in snap.markets)
            {
                MarketCache mc;
                if (!m.initialized || m.ex == null || !econCache.TryGetValue(m.uid, out mc) || mc.byGid.Count == 0) continue;
                markets++;
                foreach (var it in new List<FWExchangeSpaceGoods>(m.ex.itemList))
                {
                    Econ e;
                    if (it == null || it.GoodID == null || !mc.byGid.TryGetValue(it.GoodID, out e)) continue;
                    var def = it.getGoodsDef(); if (def == null) continue;
                    compared++;
                    if (!Same(e, ReadEcon(m.ex, it, def, m.tax))) differ++;
                }
            }
        }

        // The economic sources of one market, gathered once: what FWExchangeSpace.calculateEPool and
        // FWExchangeSpaceGoods.calculationLStellar loop over for every goods (they copy the whole spaceport list on
        // each call). Summing the same sources in the same order with the same float arithmetic gives the same values.
        public class Region
        {
            public bool star;
            public List<FWStarMapInfo> towns = new List<FWStarMapInfo>();
            public List<FWSpaceport> stations = new List<FWSpaceport>();
            public FWGalaxy galaxy;
        }

        public static Region RegionOf(FWExchangeSpace ex, FWGameData gd)
        {
            var r = new Region();
            var region = ex.getRegion();
            if (region is FWStarInfo st)
            {
                r.star = true;
                if (st.starMapList != null) foreach (var sm in st.starMapList) if (StoryFacilityRules.IsEconomicTown(sm)) r.towns.Add(sm);
                return r;
            }
            r.galaxy = ex.getGalaxy();
            var list = gd?.getSpaceportList();
            if (list != null) foreach (var sp in list) if (StoryFacilityRules.IsEconomic(sp) && sp.galaxyId == ex.galaxyId) r.stations.Add(sp);
            return r;
        }

        public static float EPoolOf(Region r, string gid)
        {
            float num = 0f;
            if (r.star) { foreach (var t in r.towns) num += t.getETown(gid); return num; }
            foreach (var sp in r.stations) num += sp.getEStation(gid);
            return num;
        }

        public static float LeanOf(Region r, string gid)
        {
            float l = 1f;
            if (r.star) { foreach (var t in r.towns) l += t.getLTown(gid); return l; }
            foreach (var sp in r.stations) l += sp.getLStation(gid);
            l += TradeFacilityEconomics.getStellarCapacity(r.galaxy, gid, "Leaning");
            if (l < 0f) l = 0f;
            return l;
        }

        static Econ ReadEcon(FWExchangeSpace ex, FWExchangeSpaceGoods it, Trade_Goods_Def def, float fallbackTax)
        {
            var e = new Econ();
            long t0 = Perf.Now;
            e.tax = fallbackTax; // the per-goods rate is not what the player pays (see TradeTax)
            long t1 = Perf.Now; Budget.tTax += t1 - t0;
            try { e.pool = ex.getEPool(it.GoodID); } catch { e.pool = def.MarketSize; }
            long t2 = Perf.Now; Budget.tPool += t2 - t1;
            try { e.lean = it.getLStellar(); } catch { e.lean = 1f; }
            Budget.tBase += Perf.Now - t2;
            return e;
        }

        static bool Same(Econ a, Econ b) => a.pool == b.pool && a.lean == b.lean;

        static IEnumerator ScanMarket(FWExchangeSpace ex, MarketInfo m, Snapshot snap)
        {
            m.ex = ex;
            m.initialized = ex.storeInitialized && ex.itemList != null;
            m.pendingDays = ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0;
            m.tax = TradeTax(ex);
            if (!m.initialized) yield break;
            bool exact = m.Exact;
            float S = 1f, BG = 0f;
            try { S = ex.getTradeS(); } catch { }
            try { BG = ex.getBGlobal(); } catch { }
            var items = new List<FWExchangeSpaceGoods>(ex.itemList); // the list belongs to the game: copy

            // getEPool and getLStellar do not settle the market (they read the stations and trade rules), so markets that
            // owe days use them too, through the same cache.
            MarketCache mc;
            econCache.TryGetValue(ex.uid, out mc);
            {
                if (mc == null || mc.revision != ex.latestEconomicRevision)
                {
                    mc = new MarketCache { revision = ex.latestEconomicRevision };
                    econCache[ex.uid] = mc;
                }
                else if (mc.byGid.Count > 0)
                {
                    // Spot-check the cache against live values before trusting it (more samples for values from disk).
                    int samples = fromDisk.Remove(ex.uid) ? SamplesPerMarket * 2 : SamplesPerMarket;
                    for (int k = 0; k < samples && items.Count > 0; k++)
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
                            diskDirty = true;
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
                else
                {
                    e = ReadEcon(ex, it, def, m.tax);
                    mc.byGid[it.GoodID] = e;
                    diskDirty = true;
                    Perf.cacheMisses++;
                }
                float lean = e != null ? e.lean : 1f;
                // Checkout charges the market's rate on every goods line (see TradeTax).
                q.tax = m.tax;
                if (e != null) q.ePool = e.pool;
                else
                {
                    try { q.ePool = ex.getEPool(it.GoodID); } catch { q.ePool = def.MarketSize; }
                }
                if (q.ePool <= 0) q.ePool = Math.Max(1, def.MarketSize);
                long te = Perf.Now;
                float ev = EventPrice(ex.uid, it.GoodID);
                q.eventFactor = ev;
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
                    q.specialGoods = sg;
                    // Same as getStoreOccupancy() without its settlement call: the sum of the stored items' durability factors.
                    double occ = 0;
                    var durs = new List<float>();
                    bool ships = false;
                    if (sg.goodsList != null)
                        for (int gi = 0; gi < sg.goodsList.Count; gi++)
                        {
                            var g = sg.goodsList[gi];
                            if (g == null || g.goods == null) continue;
                            float d = 1f;
                            try { d = sg.getPDurability(g.goods); } catch { }
                            occ += d; durs.Add(d);
                            if (g.goods is FWAirShip) ships = true;
                        }
                    q.occupancy = occ;
                    // Ships are priced by their defects, not durability (see GoodsQuote.ship).
                    if (!ships && !sg.UsesEquipmentPressure && durs.Count > 0) q.SetStoreDurability(durs);
                    if (sg.itemType == 550)
                    {
                        q.ship = true;
                        q.storeShips = new List<FWAirShip>();
                        if (sg.goodsList != null) foreach (var g in sg.goodsList) if (g?.goods is FWAirShip a) q.storeShips.Add(a);
                        q.storeShips.Sort((a, b) => GoodsQuote.Defects(a).CompareTo(GoodsQuote.Defects(b)));
                        q.shipDefects = q.storeShips.ConvertAll(a => GoodsQuote.Defects(a)).ToArray();
                    }
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

        static void AddThings(InvLocation loc, IEnumerable<FWThingBean> things, Snapshot snap)
        {
            if (things == null) return;
            foreach (var t in things)
            {
                if (t == null || t.sum <= 0) continue;
                var k = t.type + "_" + t.id;
                int v; loc.items.TryGetValue(k, out v);
                loc.items[k] = v + t.sum;
                AddHeld(snap, k, t, Math.Min(t.sum, 200));
            }
        }

        static void AddHeld(Snapshot snap, string key, FWBaseBean item, int n)
        {
            List<FWBaseBean> l;
            if (!snap.heldItems.TryGetValue(key, out l)) snap.heldItems[key] = l = new List<FWBaseBean>();
            for (int i = 0; i < n && l.Count < 200; i++) l.Add(item);
        }

        static string SpName(FWSpaceport sp)
        {
            try { var n = sp.getName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "空间站";
        }

        static void ScanInventory(FWGameData gd, Snapshot snap)
        {
            var seen = new HashSet<long>();
            FWStarMapInfo mother = null;
            try { mother = gd.getMothershipStarMapInfo(); } catch { }
            if (mother != null && mother.starSpread != null)
            {
                var loc = new InvLocation { name = "母舰", galaxyId = snap.fleetGalaxyId };
                AddThings(loc, new List<FWThingBean>(mother.starSpread.things), snap);
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
                            AddThings(loc, new List<FWThingBean>(map.starSpread.things), snap);
                            snap.inventory.Add(loc);
                        }
                    }
                }
            try
            {
                var fleet = gd.galaxyBlock?.playerFleet;
                if (fleet != null)
                {
                    var loc = new InvLocation { name = (string.IsNullOrEmpty(fleet.name) ? "舰队" : fleet.name) + "（舰队背包）", galaxyId = fleet.pos?.galaxyId };
                    var things = fleet.getThings();
                    if (things != null) AddThings(loc, new List<FWThingBean>(things), snap);
                    // The fleet's ships, as goods "550_<ship id>" (a plan that trades ships follows them like goods).
                    var ships = fleet.ships;
                    if (ships != null)
                        foreach (var s in new List<FWAirShip>(ships))
                        {
                            if (s == null || string.IsNullOrEmpty(s.id)) continue;
                            var k = "550_" + s.id;
                            int v; loc.items.TryGetValue(k, out v); loc.items[k] = v + 1;
                            snap.AddHeld(k, s);
                        }
                    snap.inventory.Add(loc);
                }
            }
            catch (Exception e) { snap.errors.Add("读舰队背包失败: " + e.Message); }
            try
            {
                // Space station warehouses (things bought into "空间站仓库").
                if (gd.spaceportList != null)
                    foreach (var sp in new List<FWSpaceport>(gd.spaceportList))
                    {
                        if (sp?.things == null || sp.things.Count == 0) continue;
                        var loc = new InvLocation { name = snap.GalaxyName(sp.galaxyId) + " " + SpName(sp) + " 仓库", galaxyId = sp.galaxyId };
                        AddThings(loc, new List<FWThingBean>(sp.things), snap);
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
                    AddHeld(snap, k, d, 1);
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
