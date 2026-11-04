using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace TradeHelper
{
    [Serializable]
    public class PlanPoint
    {
        public string time;      // real clock
        public long gameMM;      // game clock
        public double cost, revenue, profit;
        public int buyPending, sellPending;
    }

    // A trade the player chose to follow: buy qty of a goods line at one station, sell at another.
    [Serializable]
    public class Plan
    {
        public string id;
        public string gid, key, name;           // key = "type_id" in the player's inventory
        public int qty;
        public long buyUid = -1, sellUid;
        public string buyName, sellName, buyGalaxy, sellGalaxy;
        public string createdAt;
        public long createdMM;
        public double planCost, planRevenue;
        public int ownedAtStart;
        public int state;                       // 0 = to buy, 1 = bought (holding), 2 = done
        public int boughtQty;
        public bool keepFresh = true;
        public List<long> addedKeepFresh = new List<long>();
        public List<PlanPoint> history = new List<PlanPoint>();
        public string fromLocation;             // plans from held stock: where the stock is (e.g. 灾厄舰队（舰队货仓）)
        public bool autoBought;                 // the purchase was seen in the inventory (not marked by hand)
        public string boughtAt;

        // Live values (not saved).
        [JsonIgnore] [NonSerialized] public double curCost = double.NaN, curRevenue = double.NaN;
        [JsonIgnore] [NonSerialized] public int buyStock = -1, owned;
        [JsonIgnore] [NonSerialized] public MarketInfo buyMarket, sellMarket;
        [JsonIgnore] [NonSerialized] public List<KeyValuePair<MarketInfo, double>> alternatives = new List<KeyValuePair<MarketInfo, double>>();
        [JsonIgnore] [NonSerialized] public double fleetToNextAu = -1;
        [JsonIgnore] [NonSerialized] public string note = "";
        // Units of the same goods that other open plans sell (or buy) at the same station before this one.
        [JsonIgnore] [NonSerialized] public int sellAhead, buyAhead;
        [JsonIgnore] [NonSerialized] public DateTime lastPointAt = DateTime.MinValue;

        [JsonIgnore] public double PlanProfit => planRevenue - planCost;
        [JsonIgnore] public double CurProfit => state == 0 ? curRevenue - curCost : curRevenue - planCost;
        [JsonIgnore] public int HoldQty => state == 0 ? qty : Math.Max(1, boughtQty);
        // Stage of the plan. Stage 1 means "goods in hand, still to sell".
        [JsonIgnore] public string StateText => state == 0 ? "① 待买入" : state == 1 ? "② 已买 " + boughtQty + "，待卖" : "③ 已完成";
    }

    // plans.json. Written with the game's Newtonsoft.Json: Unity's JsonUtility silently wrote "{}" for this file (0.3.0 and
    // earlier), so plans did not survive a restart.
    public class PlanFile { public int format = 2; public List<Plan> plans = new List<Plan>(); }

    public static class Plans
    {
        public static List<Plan> list = new List<Plan>();
        // Bumped whenever the set of plans or a plan's stage changes (advice depends on it; history points do not).
        public static int version;
        static string Path => System.IO.Path.Combine(Config.Dir, "plans.json");
        const int HistoryMax = 60;
        static bool saveChecked;

        public static void Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var f = JsonConvert.DeserializeObject<PlanFile>(File.ReadAllText(Path));
                    if (f?.plans != null) list = f.plans;
                }
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] plans read failed: " + e.Message); }
            foreach (var p in list)
            {
                if (p.history == null) p.history = new List<PlanPoint>();
                if (p.addedKeepFresh == null) p.addedKeepFresh = new List<long>();
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var json = JsonConvert.SerializeObject(new PlanFile { plans = list }, Formatting.Indented);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, Path, true);
                File.Delete(tmp);
                if (!saveChecked)
                {
                    // Once per session: read the file back and make sure every plan is in it.
                    saveChecked = true;
                    var back = JsonConvert.DeserializeObject<PlanFile>(File.ReadAllText(Path));
                    int n = back?.plans?.Count ?? -1;
                    Debug.Log("[TradeHelper] plans saved and read back: " + n + "/" + list.Count + (n == list.Count ? " ok" : " MISMATCH"));
                }
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] plans write failed: " + e.Message); }
        }

        static void KeepFresh(Plan p, long uid, Config cfg, Settlement st)
        {
            if (uid < 0) return;
            if (!cfg.keepFresh.Contains(uid)) { cfg.keepFresh.Add(uid); p.addedKeepFresh.Add(uid); }
            st.Enqueue(uid);
        }

        public static void ApplyKeepFresh(Plan p, Config cfg, Settlement st)
        {
            if (p.keepFresh)
            {
                if (p.state == 0) KeepFresh(p, p.buyUid, cfg, st);
                if (p.state != 2) KeepFresh(p, p.sellUid, cfg, st);
            }
            else ReleaseKeepFresh(p, cfg);
            cfg.Save();
        }

        // Undo only the "keep up to date" ticks this plan added, and only if no other open plan still needs them.
        static void ReleaseKeepFresh(Plan p, Config cfg)
        {
            foreach (var uid in new List<long>(p.addedKeepFresh))
            {
                bool needed = list.Exists(o => o != p && o.state != 2 && o.keepFresh && (o.buyUid == uid || o.sellUid == uid));
                if (!needed) cfg.keepFresh.Remove(uid);
                p.addedKeepFresh.Remove(uid);
            }
        }

        public static Plan FromTrade(TradeIdea t, Snapshot snap, Config cfg, Settlement st)
        {
            var key = t.buy.type + "_" + t.buy.itemId;
            var p = new Plan
            {
                id = Guid.NewGuid().ToString("N"), gid = t.gid, key = key, name = t.name, qty = t.qty,
                buyUid = t.buy.market.uid, buyName = t.buy.market.name, buyGalaxy = t.buy.market.galaxyId,
                sellUid = t.sell.market.uid, sellName = t.sell.market.name, sellGalaxy = t.sell.market.galaxyId,
                createdAt = DateTime.Now.ToString("MM-dd HH:mm"), createdMM = GameMM(),
                planCost = t.cost, planRevenue = t.revenue, ownedAtStart = snap.Owned(key), state = 0,
            };
            list.Add(p);
            version++;
            ApplyKeepFresh(p, cfg, st);
            Save();
            return p;
        }

        public static Plan FromSell(SellIdea s, Snapshot snap, Config cfg, Settlement st)
        {
            var p = new Plan
            {
                id = Guid.NewGuid().ToString("N"), gid = s.market.gid, key = s.key, name = s.name, qty = s.qty,
                sellUid = s.market.market.uid, sellName = s.market.market.name, sellGalaxy = s.market.market.galaxyId,
buyName = "（持有·" + s.location + "）", fromLocation = s.location, createdAt = DateTime.Now.ToString("MM-dd HH:mm"), createdMM = GameMM(),
                planCost = 0, planRevenue = s.revenue, ownedAtStart = snap.Owned(s.key), state = 1, boughtQty = s.qty,
            };
            list.Add(p);
            version++;
            ApplyKeepFresh(p, cfg, st);
            Save();
            return p;
        }

        public static void Remove(Plan p, Config cfg)
        {
            ReleaseKeepFresh(p, cfg);
            list.Remove(p);
            version++;
            cfg.Save();
            Save();
        }

        public static void MarkBought(Plan p, Config cfg, Settlement st)
        {
            p.state = 1;
            version++;
            p.autoBought = false;
            p.boughtAt = DateTime.Now.ToString("MM-dd HH:mm");
            if (p.boughtQty <= 0) p.boughtQty = p.qty;
            ReleaseBuySide(p, cfg);
            Save();
        }

        public static void MarkDone(Plan p, Config cfg)
        {
            p.state = 2;
            version++;
            ReleaseKeepFresh(p, cfg);
            cfg.Save();
            Save();
        }

        static void ReleaseBuySide(Plan p, Config cfg)
        {
            if (!p.addedKeepFresh.Contains(p.buyUid) || p.buyUid == p.sellUid) return;
            bool needed = list.Exists(o => o != p && o.state != 2 && o.keepFresh && (o.buyUid == p.buyUid || o.sellUid == p.buyUid));
            if (!needed) cfg.keepFresh.Remove(p.buyUid);
            p.addedKeepFresh.Remove(p.buyUid);
            cfg.Save();
        }

        static long GameMM()
        {
            try { return MarketScanner.Game?.elapsedMM ?? 0; } catch { return 0; }
        }

        // Re-price every open plan against a fresh snapshot.
        public static void Track(Snapshot snap, Config cfg, Settlement st)
        {
            bool changed = false;
            // Plans that sell (or buy) the same goods at the same station share that station's price curve: the second
            // batch sells after the first has pushed the price down. Price them in queue order: goods already in hand
            // first, then plans still to buy; inside each group, older plans first. No recovery between the batches is
            // assumed (the station's daily consumption would only make the later batch a little better).
            var order = new List<Plan>(list);
            var pos = new Dictionary<Plan, int>();
            for (int i = 0; i < list.Count; i++) pos[list[i]] = i;
            order.Sort((a, b) =>
            {
                int c = (a.state == 0 ? 1 : 0).CompareTo(b.state == 0 ? 1 : 0);
                if (c == 0) c = a.createdMM.CompareTo(b.createdMM);
                return c != 0 ? c : pos[a].CompareTo(pos[b]);
            });
            var soldAhead = new Dictionary<string, int>();
            var boughtAhead = new Dictionary<string, int>();
            var sellTotal = new Dictionary<string, int>();
            foreach (var o in list)
                if (o.state != 2) { var k = o.sellUid + "|" + o.gid; int v; sellTotal.TryGetValue(k, out v); sellTotal[k] = v + o.HoldQty; }
            foreach (var p in order)
            {
                p.note = "";
                p.sellAhead = p.buyAhead = 0;
                p.buyMarket = snap.markets.Find(m => m.uid == p.buyUid);
                p.sellMarket = snap.markets.Find(m => m.uid == p.sellUid);
                p.owned = snap.Owned(p.key);
                // Names follow the game (older plans saved the mod's earlier names).
                if (p.sellMarket != null) p.sellName = p.sellMarket.name;
                if (p.buyMarket != null && p.buyUid >= 0) p.buyName = p.buyMarket.name;
                GoodsQuote bq = null, sq = null;
                if (p.buyMarket != null) p.buyMarket.byGid.TryGetValue(p.gid, out bq);
                if (p.sellMarket != null) p.sellMarket.byGid.TryGetValue(p.gid, out sq);

                // The baseline for spotting the purchase is the lowest holding seen while waiting to buy: if the player
                // sells some of the same goods first, buying afterwards must still count in full.
                if (p.state == 0 && p.owned < p.ownedAtStart) { p.ownedAtStart = p.owned; changed = true; }
                // Detect the purchase from the inventory: the holding grew since the plan was made.
                if (p.state == 0 && p.owned - p.ownedAtStart >= 1)
                {
                    p.state = 1;
                    version++;
                    p.boughtQty = Math.Min(p.qty, p.owned - p.ownedAtStart);
                    p.autoBought = true;
                    p.boughtAt = DateTime.Now.ToString("MM-dd HH:mm");
                    ReleaseBuySide(p, cfg);
                    changed = true;
                }
                else if (p.state == 1 && p.buyUid >= 0 && p.autoBought && p.boughtQty < p.qty && p.owned - p.ownedAtStart > p.boughtQty)
                {
                    // Bought in more than one go: follow the holding up to the planned quantity.
                    p.boughtQty = Math.Min(p.qty, p.owned - p.ownedAtStart);
                    version++;
                    changed = true;
                }
                else if (p.state == 1 && p.buyUid >= 0 && p.owned <= p.ownedAtStart)
                {
                    // Holding back to where it was: sold (or moved somewhere this panel does not see).
                    p.note = "库存里已经看不到这批货：卖掉了的话点“改为已完成”";
                }

                int hold = p.HoldQty;
                if (p.state == 0)
                {
                    if (bq == null) { p.curCost = double.NaN; p.note = "买入站现在没有这个货"; }
                    else
                    {
                        p.buyStock = bq.stock;
                        var bk = p.buyUid + "|" + p.gid;
                        int ob; boughtAhead.TryGetValue(bk, out ob);
                        p.buyAhead = ob;
                        // If the station now has fewer than planned (after earlier plans' share), price what can actually be bought.
                        int avail = Math.Max(0, bq.stock - ob);
                        hold = Math.Min(p.qty, avail);
                        p.curCost = hold > 0 ? bq.BuyCost(ob + hold) - bq.BuyCost(ob) : double.NaN;
                        boughtAhead[bk] = ob + hold;
                        if (avail < p.qty) p.note = "买入站库存只剩 " + bq.stock + " 个" + (ob > 0 ? "（其中 " + ob + " 个归前面的计划）" : "") + "，按能买到的 " + hold + " 个算";
                        if (bq.contraband) p.note = "买入站现在把它列为违禁品，买不了";
                    }
                }
                var sk = p.sellUid + "|" + p.gid;
                int os = 0;
                if (p.state != 2) { soldAhead.TryGetValue(sk, out os); soldAhead[sk] = os + hold; }
                p.sellAhead = os;
                p.curRevenue = sq != null && !sq.contraband && hold > 0 ? sq.SellRevenue(os + hold) - sq.SellRevenue(os) : double.NaN;
                if (sq != null && sq.contraband) p.note = "卖出站现在把它列为违禁品，卖不了";
                if (p.sellMarket != null && p.sellMarket.planet && p.state != 2)
                    p.note = "注意：卖出站是星球的地面交易行，舰队不能直接卖（" + p.sellMarket.where + "）。看下面“现在卖最好的站”换一个。";

                // Best places to sell the holding right now.
                p.alternatives.Clear();
                foreach (var m in snap.markets)
                {
                    GoodsQuote q;
                    if (!m.byGid.TryGetValue(p.gid, out q) || q.contraband) continue;
                    if (m.planet && !cfg.includeGround) continue;
                    if (!m.Discovered && !cfg.includeUndiscovered) continue;
                    if (hold <= 0) continue;
                    // At its own sell station the plan keeps its queue place; anywhere else every other plan selling the same
                    // goods there is counted as selling first (the cautious case).
                    int oa;
                    if (m.uid == p.sellUid) oa = os;
                    else { sellTotal.TryGetValue(m.uid + "|" + p.gid, out oa); }
                    p.alternatives.Add(new KeyValuePair<MarketInfo, double>(m, q.SellRevenue(oa + hold) - q.SellRevenue(oa)));
                }
                p.alternatives.Sort((a, b) => b.Value.CompareTo(a.Value));
                if (p.alternatives.Count > 5) p.alternatives.RemoveRange(5, p.alternatives.Count - 5);

                string next = p.state == 0 ? p.buyGalaxy : p.sellGalaxy;
                p.fleetToNextAu = snap.RouteAu(snap.fleetGalaxyId, next);

                if (p.state != 2 && !double.IsNaN(p.CurProfit))
                {
                    var pt = new PlanPoint
                    {
                        time = DateTime.Now.ToString("HH:mm:ss"), gameMM = GameMM(),
                        cost = p.state == 0 ? p.curCost : p.planCost, revenue = p.curRevenue, profit = p.CurProfit,
                        buyPending = p.buyMarket != null ? p.buyMarket.pendingDays : -1, sellPending = p.sellMarket != null ? p.sellMarket.pendingDays : -1,
                    };
                    var last = p.history.Count > 0 ? p.history[p.history.Count - 1] : null;
                    // A new point when the profit moved, or at least every five minutes.
                    bool moved = last == null || Math.Abs(last.profit - pt.profit) > Math.Max(1.0, Math.Abs(last.profit) * 0.001);
                    if (moved || (DateTime.Now - p.lastPointAt).TotalMinutes >= 5)
                    {
                        p.history.Add(pt);
                        p.lastPointAt = DateTime.Now;
                        if (p.history.Count > HistoryMax) p.history.RemoveRange(0, p.history.Count - HistoryMax);
                        changed = true;
                    }
                }
            }
            if (changed) Save();
        }
    }
}
