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
        public long boughtMM = -1;              // game clock when the goods were in hand (sales after it count)
        public int soldQty;                     // units of this plan that have left the inventory since
        public double soldRevenue;              // estimate for those units at the time they left (actual when known)
        public bool autoDone;                   // completed automatically
        public bool actualPaid;                 // soldRevenue is what the game's trade records say was paid
        public string doneAt, doneNote;

        // Live values (not saved).
        [JsonIgnore] [NonSerialized] public double curCost = double.NaN, curRevenue = double.NaN;
        [JsonIgnore] [NonSerialized] public int buyStock = -1, owned;
        [JsonIgnore] [NonSerialized] public MarketInfo buyMarket, sellMarket;
        [JsonIgnore] [NonSerialized] public List<KeyValuePair<MarketInfo, double>> alternatives = new List<KeyValuePair<MarketInfo, double>>();
        [JsonIgnore] [NonSerialized] public double fleetToNextAu = -1;
        [JsonIgnore] [NonSerialized] public string note = "";
        // Units of the same goods that other open plans sell (or buy) at the same station before this one.
        [JsonIgnore] [NonSerialized] public int sellAhead, buyAhead;
        // Units this plan counts as in hand that the inventory does not have (the open plans of this goods together claim
        // more than the player holds, e.g. a plan marked bought by hand before buying).
        [JsonIgnore] [NonSerialized] public int missing;
        [JsonIgnore] [NonSerialized] public DateTime lastPointAt = DateTime.MinValue;

        [JsonIgnore] public double PlanProfit => planRevenue - planCost;
        [JsonIgnore] public double CurProfit => state == 0 ? curRevenue - curCost
                                               : state == 1 ? (HoldQty > 0 ? curRevenue : 0) + soldRevenue - planCost
                                               : soldRevenue - planCost;
        // Net profit if the goods still to sell went to a market paying `revenue` for them (stage 1: at today's buying
        // cost; stage 2: what was paid, plus what the units already gone brought in).
        public double ProfitIfSoldFor(double revenue) => state == 0 ? revenue - curCost
                                                       : state == 1 ? revenue + soldRevenue - planCost
                                                       : double.NaN;
        // Net profit if everything still to sell went to the best market right now (NaN when there is none).
        [JsonIgnore] public double BestProfit => alternatives.Count > 0 ? ProfitIfSoldFor(alternatives[0].Value) : double.NaN;
        // Units still to buy (stage 1) or still in hand to sell (stage 2).
        [JsonIgnore] public int HoldQty => state == 0 ? qty : Math.Max(0, boughtQty - soldQty);
        [JsonIgnore] public int ShownQty => state == 2 ? boughtQty : HoldQty;
        [JsonIgnore] public string StateText => state == 1 && missing > 0 ? (missing >= HoldQty ? "② 库存里没有" : "② 库存缺 " + missing)
                                              : state == 0 ? "① 待买入"
                                              : state == 1 ? (soldQty > 0 ? "② 已卖 " + soldQty + "/" + boughtQty : "② 已买 " + boughtQty + "，待卖")
                                              : (autoDone ? "③ 已完成·自动" : "③ 已完成");
    }

    // plans.json. Written with the game's Newtonsoft.Json: Unity's JsonUtility silently wrote "{}" for this file (0.3.0 and
    // earlier), so plans did not survive a restart.
    public class PlanFile
    {
        public int format = 2;
        public List<Plan> plans = new List<Plan>();
        public Dictionary<string, int> lastOwned = new Dictionary<string, int>();
    }

    public static class Plans
    {
        public static List<Plan> list = new List<Plan>();
        // How many of each tracked goods the player held at the last look; changes are handed to plans (see Track).
        static Dictionary<string, int> lastOwned = new Dictionary<string, int>();
        static object baselineGame;
        // Store size of each tracked goods in every market at the last look ("uid|gid"). Selling special goods (fleet
        // wings, creatures...) leaves no trade record in the game (the checkout only puts the items back into the
        // store), so the market whose store grew tells where they went.
        static Dictionary<string, int> lastStock = new Dictionary<string, int>();
        static long lastLookMM = -1;            // game clock at the last look (sales after it are new)
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
                    if (f?.lastOwned != null) lastOwned = f.lastOwned;
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
                var json = JsonConvert.SerializeObject(new PlanFile { plans = list, lastOwned = lastOwned }, Formatting.Indented);
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
            lastOwned[key] = snap.Owned(key);
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
                boughtMM = GameMM(),
            };
            lastOwned[s.key] = snap.Owned(s.key);
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
            p.boughtMM = GameMM();
            if (p.boughtQty <= 0) p.boughtQty = p.qty;
            ReleaseBuySide(p, cfg);
            Save();
        }

        public static void MarkDone(Plan p, Config cfg)
        {
            p.autoDone = false;
            p.doneAt = DateTime.Now.ToString("MM-dd HH:mm");
            p.doneNote = "手动标记";
            if (p.soldQty < p.boughtQty)
            {
                // The rest is taken as sold at the current estimate.
                if (!double.IsNaN(p.curRevenue)) p.soldRevenue += p.curRevenue;
                p.soldQty = p.boughtQty;
            }
            p.state = 2;
            version++;
            ReleaseKeepFresh(p, cfg);
            cfg.Save();
            Save();
        }

        // Undo an automatic completion (e.g. the goods were moved somewhere this panel does not see).
        // Back to "to buy" (a plan marked bought by hand that was not bought); the next purchase is seen as usual.
        public static void MarkUnbought(Plan p, Config cfg, Settlement st)
        {
            p.state = 0;
            p.boughtQty = 0; p.soldQty = 0; p.soldRevenue = 0;
            p.autoBought = false; p.boughtAt = null; p.boughtMM = -1;
            p.missing = 0;
            version++;
            ApplyKeepFresh(p, cfg, st);
            Save();
        }

        public static void Reopen(Plan p, Config cfg, Settlement st)
        {
            p.state = 1;
            p.soldQty = 0; p.soldRevenue = 0;
            p.autoDone = false; p.actualPaid = false; p.doneAt = null; p.doneNote = null;
            version++;
            ApplyKeepFresh(p, cfg, st);
            Save();
        }

        // Player sales of this goods at a market since the given game time, from the market's own trade records (the game
        // keeps the latest 16 per goods line, in memory only): units and what the player was paid.
        static void PlayerSales(MarketInfo m, string gid, long sinceMM, out int units, out double paid)
        {
            units = 0; paid = 0;
            GoodsQuote q;
            if (m == null || gid == null || !m.byGid.TryGetValue(gid, out q)) return;
            FWExchangeSpaceGoods g = (FWExchangeSpaceGoods)q.normal ?? q.specialGoods;
            var hs = g?.transactionHistories;
            if (hs == null) return;
            foreach (var h in new List<FWExchangeSpaceTransactionHistory>(hs))
            {
                if (h == null || h.source != 0 || h.direction != -1 || h.transactionMM < sinceMM) continue;
                units += Math.Max(1, Math.Abs(h.sum));
                paid += h.actualPayment;
            }
        }

        static void ReleaseBuySide(Plan p, Config cfg)
        {
            if (!p.addedKeepFresh.Contains(p.buyUid) || p.buyUid == p.sellUid) return;
            bool needed = list.Exists(o => o != p && o.state != 2 && o.keepFresh && (o.buyUid == p.buyUid || o.sellUid == p.buyUid));
            if (!needed) cfg.keepFresh.Remove(p.buyUid);
            p.addedKeepFresh.Remove(p.buyUid);
            cfg.Save();
        }

        public class RouteTotal
        {
            public string name, from, to;
            public int count, units;
            public double profit;
            public bool held;           // plans selling stock already held: "profit" is what was received
        }

        // Completed plans grouped by goods and route, best total first.
        public static List<RouteTotal> Routes()
        {
            var map = new Dictionary<string, RouteTotal>();
            foreach (var p in list)
            {
                if (p.state != 2) continue;
                bool held = p.buyUid < 0;
                string from = held ? "（已持有）" : p.buyName;
                var k = p.name + "|" + from + "|" + p.sellName;
                RouteTotal r;
                if (!map.TryGetValue(k, out r)) map[k] = r = new RouteTotal { name = p.name, from = from, to = p.sellName, held = held };
                r.count++; r.units += p.boughtQty; r.profit += p.soldRevenue - p.planCost;
            }
            var res = new List<RouteTotal>(map.Values);
            res.Sort((a, b) => b.profit.CompareTo(a.profit));
            return res;
        }

        static long GameMM()
        {
            try { return MarketScanner.Game?.elapsedMM ?? 0; } catch { return 0; }
        }

        // Stage changes from the inventory. For each goods with open plans, the change in the player's total holding since
        // the last look is handed out in queue order: a rise fills plans waiting to buy (stage 1 -> 2), a fall counts as
        // sold for plans holding goods (stage 2 -> 3 once all their units are gone). Which station took them is read from
        // the markets' trade records.
        static bool FollowInventory(Snapshot snap, Config cfg, List<Plan> order)
        {
            bool changed = false;
            var keys = new HashSet<string>();
            foreach (var p in order) if (p.state != 2 && p.key != null) keys.Add(p.key);
            // A partly read inventory would look like a sale: change nothing.
            if (snap.inventory.Count == 0 || snap.errors.Exists(e => e.StartsWith("读舰队货仓失败") || e.StartsWith("读空间站仓库失败"))) return false;
            // After loading a save (or starting the game) the holding is whatever that save has: take it as the new baseline.
            var game = MarketScanner.Game;
            if (!ReferenceEquals(game, baselineGame))
            {
                baselineGame = game;
                foreach (var key in keys) lastOwned[key] = snap.Owned(key);
                return true;
            }
            foreach (var key in keys)
            {
                int now = snap.Owned(key), prev;
                if (!lastOwned.TryGetValue(key, out prev)) { lastOwned[key] = now; changed = true; continue; }
                int d = now - prev;
                if (d == 0) continue;
                lastOwned[key] = now; changed = true;
                if (d > 0)
                {
                    foreach (var p in order)
                    {
                        if (d <= 0) break;
                        if (p.key != key || p.buyUid < 0) continue;
                        if (p.state == 0)
                        {
                            int take = Math.Min(d, p.qty);
                            p.state = 1; p.boughtQty = take; p.autoBought = true;
                            p.boughtAt = DateTime.Now.ToString("MM-dd HH:mm"); p.boughtMM = GameMM();
                            ReleaseBuySide(p, cfg);
                            d -= take; version++;
                        }
                        else if (p.state == 1 && p.autoBought && p.soldQty == 0 && p.boughtQty < p.qty)
                        {
                            int take = Math.Min(d, p.qty - p.boughtQty);
                            p.boughtQty += take; d -= take; version++;
                        }
                    }
                }
                else
                {
                    int n = -d;
                    // Plans of the same goods are interchangeable in the hold; the units gone count first for the plans
                    // whose sell market shows the sale (a trade record or, for special goods, a larger store since the
                    // last look), then for plans whose units the inventory had at the last look (not a plan marked
                    // bought by hand without buying), then in queue order.
                    var byPlace = new List<Plan>(order);
                    var soldHere = new Dictionary<Plan, bool>();
                    foreach (var p in byPlace) if (p.key == key && p.state == 1) soldHere[p] = SoldAtSinceLastLook(snap, p);
                    byPlace.Sort((a, b) =>
                    {
                        bool sa, sb;
                        soldHere.TryGetValue(a, out sa); soldHere.TryGetValue(b, out sb);
                        int c = (sa ? 0 : 1).CompareTo(sb ? 0 : 1);
                        if (c == 0) c = (a.missing >= a.HoldQty ? 1 : 0).CompareTo(b.missing >= b.HoldQty ? 1 : 0);
                        return c != 0 ? c : order.IndexOf(a).CompareTo(order.IndexOf(b));
                    });
                    foreach (var p in byPlace)
                    {
                        if (n <= 0) break;
                        if (p.key != key || p.state != 1) continue;
                        int left = p.boughtQty - p.soldQty;
                        if (left <= 0) continue;
                        int take = Math.Min(n, left);
                        // Value the units at the last estimate for what this plan still held.
                        if (!double.IsNaN(p.curRevenue) && left > 0) p.soldRevenue += p.curRevenue * take / left;
                        p.soldQty += take; n -= take; version++;
                        if (p.soldQty >= p.boughtQty) Complete(p, snap, cfg);
                    }
                }
            }
            foreach (var k in new List<string>(lastOwned.Keys)) if (!keys.Contains(k)) { lastOwned.Remove(k); changed = true; }
            return changed;
        }

        static bool SoldAtSinceLastLook(Snapshot snap, Plan p)
        {
            if (lastLookMM < 0) return false;
            var m = snap.markets.Find(x => x.uid == p.sellUid);
            if (m == null) return false;
            int units; double paid;
            PlayerSales(m, p.gid, lastLookMM, out units, out paid);
            if (units > 0) return true;
            GoodsQuote q; int before;
            return m.byGid.TryGetValue(p.gid, out q) && q.special && lastStock.TryGetValue(m.uid + "|" + p.gid, out before) && q.stock > before;
        }

        // Open plans that together claim more units in hand than the inventory has: the excess is put on plans marked
        // bought by hand first (the usual cause), then on the newest.
        static void CheckHoldings(Snapshot snap, List<Plan> order)
        {
            foreach (var p in order) p.missing = 0;
            if (snap.inventory.Count == 0 || snap.errors.Exists(e => e.StartsWith("读舰队货仓失败") || e.StartsWith("读空间站仓库失败"))) return;
            var keys = new HashSet<string>();
            foreach (var p in order) if (p.state == 1 && p.key != null) keys.Add(p.key);
            foreach (var key in keys)
            {
                var holders = order.FindAll(p => p.key == key && p.state == 1);
                int claimed = 0;
                foreach (var p in holders) claimed += p.HoldQty;
                int excess = claimed - snap.Owned(key);
                if (excess <= 0) continue;
                holders.Reverse();   // newest first
                holders.Sort((a, b) => (a.autoBought || a.buyUid < 0 ? 1 : 0).CompareTo(b.autoBought || b.buyUid < 0 ? 1 : 0));
                foreach (var p in holders)
                {
                    if (excess <= 0) break;
                    p.missing = Math.Min(excess, p.HoldQty);
                    excess -= p.missing;
                }
            }
        }

        static void Complete(Plan p, Snapshot snap, Config cfg)
        {
            p.state = 2;
            p.autoDone = true;
            p.doneAt = DateTime.Now.ToString("MM-dd HH:mm");
            long since = p.boughtMM >= 0 ? p.boughtMM : p.createdMM;
            int units; double paid;
            PlayerSales(snap.markets.Find(m => m.uid == p.sellUid), p.gid, since, out units, out paid);
            if (units > 0)
            {
                p.doneNote = "在计划的 " + p.sellName + " 卖出";
                // The records cover the whole sale only when they hold exactly this plan's units (the game keeps 16).
                if (units == p.boughtQty && paid > 0) { p.soldRevenue = paid; p.actualPaid = true; p.doneNote += "，实际到手按游戏成交记录"; }
            }
            else
            {
                string where = null;
                foreach (var m in snap.markets)
                {
                    PlayerSales(m, p.gid, since, out units, out paid);
                    if (units > 0) { where = m.name; break; }
                }
                // No trade record: look for the store that grew (special goods).
                MarketInfo grew = null; int best = 0;
                if (where == null)
                    foreach (var m in snap.markets)
                    {
                        GoodsQuote q; int before;
                        if (!m.byGid.TryGetValue(p.gid, out q) || !q.special || !lastStock.TryGetValue(m.uid + "|" + p.gid, out before)) continue;
                        int grown = q.stock - before;
                        if (grown > best || (grown == best && grown > 0 && m.uid == p.sellUid)) { best = grown; grew = m; }
                    }
                if (where != null) p.doneNote = "在 " + where + " 卖出（不是计划的站）";
                else if (grew != null && grew.uid == p.sellUid) p.doneNote = "在计划的 " + grew.name + " 卖出（按该站库存增加 " + best + " 个判断）";
                else if (grew != null) p.doneNote = "在 " + grew.name + " 卖出（不是计划的站，按该站库存增加 " + best + " 个判断）";
                else p.doneNote = "库存里这批货没了，各站都没有对应的卖出记录或库存增加（可能挪到了别处）";
            }
            ReleaseKeepFresh(p, cfg);
            cfg.Save();
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
            changed |= FollowInventory(snap, cfg, order);
            CheckHoldings(snap, order);
            var sellTotal = new Dictionary<string, int>();
            foreach (var o in list)
                if (o.state != 2) { var k = o.sellUid + "|" + o.gid; int v; sellTotal.TryGetValue(k, out v); sellTotal[k] = v + o.HoldQty - o.missing; }
            foreach (var p in order)
            {
                p.note = "";
                if (p.missing > 0)
                {
                    int owned = snap.Owned(p.key), claimed = 0;
                    foreach (var o in list) if (o.key == p.key && o.state == 1) claimed += o.HoldQty;
                    p.note = "库存里只有 " + owned + " 个" + p.name + "，进行中的计划合计算了 " + claimed + " 个，这个计划有 " + p.missing + " 个对不上"
                           + (p.autoBought ? "" : "（它是手动改成已买入的，可能还没真买）") + "。没买就点「改回待买入」，不要了就删除。";
                }
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

                // Units not in the inventory are neither priced nor counted in the queue at the sell market.
                int hold = p.HoldQty - p.missing;
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
                // Goods in hand are priced from the actual items (durability included) where the game can quote them.
                GoodsQuote anyQ = sq ?? bq;
                var items = p.state == 1 ? snap.HeldSorted(p.key, anyQ != null ? anyQ.specialGoods : null) : null;
                // Still to buy: the units sold are the items bought, with their durability.
                int boughtFrom = p.buyAhead;
                Func<GoodsQuote, int, double> revenueAt = (q2, from) => p.state == 0 && bq != null ? q2.SellBoughtRevenue(from, bq, boughtFrom, hold) : q2.SellHeld(from, hold, items);
                p.curRevenue = sq != null && !sq.contraband && hold > 0 ? revenueAt(sq, os) : double.NaN;
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
                    if (!m.Explored && !cfg.includeUndiscovered) continue;
                    if (hold <= 0) continue;
                    // At its own sell station the plan keeps its queue place; anywhere else every other plan selling the same
                    // goods there is counted as selling first (the cautious case).
                    int oa;
                    if (m.uid == p.sellUid) oa = os;
                    else { sellTotal.TryGetValue(m.uid + "|" + p.gid, out oa); }
                    p.alternatives.Add(new KeyValuePair<MarketInfo, double>(m, revenueAt(q, oa)));
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
            RememberStock(snap);
            lastLookMM = GameMM();
            if (changed) Save();
        }

        static void RememberStock(Snapshot snap)
        {
            lastStock.Clear();
            var gids = new HashSet<string>();
            foreach (var p in list) if (p.state != 2 && p.gid != null) gids.Add(p.gid);
            if (gids.Count == 0) return;
            foreach (var m in snap.markets)
                foreach (var gid in gids)
                {
                    GoodsQuote q;
                    if (m.byGid.TryGetValue(gid, out q)) lastStock[m.uid + "|" + gid] = q.stock;
                }
        }
    }
}
