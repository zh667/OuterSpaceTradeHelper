using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace TradeHelper
{
    // The player's checkouts at galaxy trade markets, recorded by the mod (ledger.json). The game keeps no record of
    // selling special goods (fleet wings, creatures...): the checkout only moves credits and puts the items back on the
    // market's shelf. So while the trade page is open, the mod prices the cart the way the page's bill does
    // (PageTradeInventory.QuoteCart: PreviewCartItem, else ResolveItemTradePrice, per item, in cart order) and, when the
    // game's checkout counter (FWGameData.TradeTransactionVersion) moves, compares the credits that actually changed
    // with that bill. When they agree to the credit the entry is exact, per goods.
    public class LedgerItem
    {
        public string key;            // "<type>_<id>"
        public string name;
        public bool buy;
        public int qty;
        public double raw;            // the bill's price before tax
        public double net;            // credits for this line after tax: negative = paid, positive = received
    }

    public class LedgerEntry
    {
        public string time;
        public long gameMM;
        public long marketUid = -1;
        public string market;
        public float tax;
        public long creditDelta;      // what the player's credits actually changed by
        public long billDelta;        // what the bill said they would change by
        public bool exact;            // the two agree and every line was priced
        public string note;
        public List<LedgerItem> items = new List<LedgerItem>();
    }

    public class LedgerFile
    {
        public int format = 1;
        public List<LedgerEntry> entries = new List<LedgerEntry>();
    }

    public static class Ledger
    {
        public static List<LedgerEntry> entries = new List<LedgerEntry>();
        // Entries not yet handed to the plans (Plans.Track reads and clears them).
        public static readonly List<LedgerEntry> pending = new List<LedgerEntry>();
        public static int version;
        public static Func<long, string> marketName = uid => null;
        public static Func<string, string> goodsName = key => null;
        public static Func<FWCaravanFleet, string> caravanGalaxy;
        const int Max = 2000;
        static string Path => System.IO.Path.Combine(Config.Dir, "ledger.json");
        static bool loaded;

        class Bill
        {
            public long marketUid; public string market; public float tax;
            public List<LedgerItem> items = new List<LedgerItem>();
            public double buy, sell;
            public bool complete = true;      // every line priced
            public bool caravan;              // a caravan's trade page: the price factor holds the tax
            public long delta;                // credits change the checkout will make
            public float at;
        }

        static object watchedGame;
        static long lastVersion, lastCredits;
        static Bill bill;
        static string billSig;
        static PageTradeInventory page;
        static float nextFind;
        static bool reflectOk = true;
        static FieldInfo fHandler, fLeft;
        static MethodInfo mPreview, mResolve, mCrew, mTax;
        public static string lastError;

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (File.Exists(Path))
                {
                    var f = JsonConvert.DeserializeObject<LedgerFile>(File.ReadAllText(Path));
                    if (f?.entries != null) entries = f.entries;
                    // Before 0.6.0 caravan checkouts were not priced: say so on those old entries.
                    foreach (var e in entries)
                        if (e != null && e.market == null && e.note != null && e.note.StartsWith("没看到购物车"))
                            e.note = "模组没看到这次结账的购物车，只记下信用点变化（0.6.0 之前在商队那里的买卖都是这样，0.6.0 起会逐项记下）";
                }
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] ledger read failed: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                if (entries.Count > Max) entries.RemoveRange(0, entries.Count - Max);
                Directory.CreateDirectory(Config.Dir);
                File.WriteAllText(Path, JsonConvert.SerializeObject(new LedgerFile { entries = entries }, Formatting.Indented));
            }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] ledger write failed: " + e.Message); }
        }

        static bool Reflect()
        {
            if (fHandler != null) return reflectOk;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var t = typeof(PageTradeInventory);
            fHandler = t.GetField("_transferHandler", F);
            fLeft = t.GetField("_leftContext", F);
            mPreview = t.GetMethod("PreviewCartItem", F);
            mResolve = t.GetMethod("ResolveItemTradePrice", F);
            mCrew = t.GetMethod("ResolveCrewTradePrice", F);
            mTax = t.GetMethod("GetCurrentTaxRate", F);
            reflectOk = fHandler != null && fLeft != null && mPreview != null && mResolve != null && mCrew != null && mTax != null;
            if (!reflectOk) { lastError = "交易界面的结构变了（游戏更新？），结账记录暂时只能记信用点变化"; Debug.LogWarning("[TradeHelper] ledger: " + lastError); }
            return reflectOk;
        }

        // Every frame. Reading the counter and the credits needs no lock; pricing the cart runs under the game's locks.
        public static void Watch(FWGameData gd)
        {
            Load();
            if (!ReferenceEquals(gd, watchedGame))
            {
                watchedGame = gd;
                bill = null; billSig = null; page = null;
                pending.Clear();
                if (gd == null) return;
                lastVersion = gd.TradeTransactionVersion;
                lastCredits = gd.creditPoint;
                return;
            }
            if (gd == null) return;
            long v = gd.TradeTransactionVersion, cr = gd.creditPoint;
            bool tookByDialogue = WatchDialogue(gd, cr);
            if (v != lastVersion)
            {
                if (!tookByDialogue) Record(gd, cr - lastCredits, v - lastVersion);
                lastVersion = v;
            }
            lastCredits = cr;

            if (page == null)
            {
                if (Time.unscaledTime < nextFind) return;
                nextFind = Time.unscaledTime + 0.25f;
                // The game's list of open dialogs (UIDispatcher.addPanel puts every page there): far cheaper than
                // searching the scene.
                var open = UIDispatcher.NotifyArrays;
                for (int i = open.Count - 1; i >= 0 && page == null; i--)
                    if (open[i] is PageTradeInventory pt && pt && pt.gameObject.activeInHierarchy) page = pt;
                if (page == null) { bill = null; billSig = null; return; }
            }
            if (!page || !page.gameObject.activeInHierarchy) { page = null; bill = null; billSig = null; return; }
            UpdateBill(gd);
        }

        static void UpdateBill(FWGameData gd)
        {
            if (!Reflect()) return;
            var h = fHandler.GetValue(page) as ExchangeTransferHandler;
            var left = fLeft.GetValue(page) as ExchangeInventoryContext;
            // A caravan's trade page (CaravanTransferHandler): lines are priced with the caravan's price factor, which
            // already holds the tax (PageTradeInventory.QuoteCart / DoCaravanCheckout).
            var cctx = (h as CaravanTransferHandler)?.CaravanContext;
            if (h == null || (cctx == null && left?.Exchange == null)) { bill = null; billSig = null; return; }
            int lines = h.BuyCart.Count + h.SellCart.Count + h.BuyShipCart.Count + h.SellShipCart.Count + h.BuyCrewCart.Count + h.SellCrewCart.Count;
            if (lines == 0) { bill = null; billSig = null; return; }
            long uid = cctx != null ? -Math.Abs(cctx.CaravanFleet != null ? cctx.CaravanFleet.getUid() : (cctx.Caravan?.uid ?? 0)) : left.Exchange.uid;
            var sig = new System.Text.StringBuilder().Append(uid);
            foreach (var it in h.BuyCart) sig.Append("|b").Append(it.ItemID).Append(':').Append(Key(it)).Append('x').Append(it.Quantity);
            foreach (var it in h.SellCart) sig.Append("|s").Append(it.ItemID).Append(':').Append(Key(it)).Append('x').Append(it.Quantity);
            sig.Append("|ships ").Append(h.BuyShipCart.Count).Append('/').Append(h.SellShipCart.Count).Append("|crew ").Append(h.BuyCrewCart.Count).Append('/').Append(h.SellCrewCart.Count);
            string s = sig.ToString();
            // Re-price when the cart changes, and every second while it sits (prices move with the game clock).
            if (s == billSig && bill != null && Time.unscaledTime - bill.at < 1f) return;
            if (!GameLock.TryEnter(gd)) return;
            try
            {
                var b = new Bill { marketUid = uid, at = Time.unscaledTime };
                if (cctx != null)
                {
                    var cf = cctx.CaravanFleet;
                    string nm = cf != null && !string.IsNullOrEmpty(cf.name) ? cf.name : "商队";
                    b.market = (cctx.Caravan != null && cctx.Caravan.isBlackMarket ? "可疑商队 " : "商队 ") + nm + (caravanGalaxy != null && cf != null ? "（" + caravanGalaxy(cf) + "）" : "");
                    b.tax = cctx.GetCurrentTaxRate();
                    b.caravan = true;
                }
                else
                {
                    b.market = marketName(b.marketUid) ?? ("贸易市场 " + b.marketUid);
                    b.tax = (float)mTax.Invoke(page, null);
                }
                var preview = new FWTradePressurePreview();
                foreach (bool buying in new[] { true, false })
                    foreach (var it in buying ? h.BuyCart : h.SellCart)
                    {
                        double raw;
                        var q = mPreview.Invoke(page, new object[] { preview, it, buying }) as FWTradePressureQuote;
                        if (q != null) raw = q.rawPrice * (cctx != null ? cctx.GetPriceFactor(buying) : 1.0);
                        else raw = (double)mResolve.Invoke(page, new object[] { it, buying, preview });
                        string key = Key(it);
                        b.items.Add(new LedgerItem { key = key, name = goodsName(key) ?? key, buy = buying, qty = it.Quantity, raw = raw });
                        if (buying) b.buy += raw; else b.sell += raw;
                    }
                foreach (bool buying in new[] { true, false })
                    foreach (var ship in buying ? h.BuyShipCart : h.SellShipCart)
                    {
                        string key = "550_" + ship.getShipId();
                        if (buying) b.buy += ship.tempDisplayPrice; else b.sell += ship.tempDisplayPrice;
                        b.items.Add(new LedgerItem { key = key, name = goodsName(key) ?? "舰船", buy = buying, qty = 1, raw = ship.tempDisplayPrice });
                    }
                foreach (bool buying in new[] { true, false })
                    foreach (var crew in buying ? h.BuyCrewCart : h.SellCrewCart)
                    {
                        double p = (float)mCrew.Invoke(page, new object[] { crew, buying });
                        if (buying) b.buy += p; else b.sell += p;
                        string key = Caravans.ItemKey(CrewDisplayInstance.Unwrap(crew) as FWBaseBean) ?? "crew";
                        b.items.Add(new LedgerItem { key = key, name = goodsName(key) ?? "人员", buy = buying, qty = 1, raw = p });
                    }
                if (b.caravan)
                {
                    // DoCaravanCheckout rounds the two totals once each; the price factor already holds the tax.
                    long pay = FWTradePricePressure.RoundPayment(b.buy, true);
                    long get = FWTradePricePressure.RoundPayment(b.sell, false);
                    b.delta = get - pay;
                    foreach (var li in b.items) li.net = li.buy ? -li.raw : li.raw;
                }
                else
                {
                    // The checkout (PageTradeInventory.DoExchangeCheckout) rounds the two totals once each.
                    long pay = FWTradePricePressure.RoundPayment(b.buy * Math.Max(0.0, 1.0 + b.tax), true);
                    long get = FWTradePricePressure.RoundPayment(b.sell * Math.Max(0.0, 1.0 - b.tax), false);
                    b.delta = get - pay;
                    foreach (var li in b.items) li.net = li.buy ? -li.raw * Math.Max(0.0, 1.0 + b.tax) : li.raw * Math.Max(0.0, 1.0 - b.tax);
                }
                bill = b; billSig = s;
            }
            catch (Exception e)
            {
                bill = null; billSig = s;
                lastError = "给购物车报价失败：" + (e.InnerException ?? e).Message;
                Debug.LogWarning("[TradeHelper] ledger bill: " + (e.InnerException ?? e));
            }
            finally { GameLock.Exit(gd); }
        }

        // ---- Deals in a dialogue with an NPC fleet (smuggler deals, a caravan's "buy a navigation core"...) ----
        // The game runs them as dialogue scripts: they add or remove items in the fleet's hold and add credits
        // (ET_CSAPI.player_additem / player_addcredit), with no checkout. While such a dialogue is open
        // (GalaxyEventsDialog.instance with a fleet NPC), the mod keeps a copy of the hold's counts; when the credits
        // change it records what the hold gained and lost, priced with the smuggler's fixed deals (read from the game's
        // dialogue scripts) and checked against the credits that actually changed.
        public static Func<string, string> galaxyName = id => id;
        static long dlgNpc = -1;
        static Dictionary<string, int> dlgHold;
        static long dlgCredits;
        static string dlgMarket;
        static long dlgFleetUid;
        static bool dlgSmuggler;

        static Dictionary<string, int> HoldCounts(FWGameData gd)
        {
            var d = new Dictionary<string, int>();
            var things = gd.galaxyBlock?.playerFleet?.getThings();
            if (things == null) return d;
            foreach (var b in things)
            {
                string k = Caravans.ItemKey(b);
                if (k == null) continue;
                int n = b is FWThingBean t ? t.getSum() : 1;
                int o; d.TryGetValue(k, out o); d[k] = o + n;
            }
            return d;
        }

        // True when this frame's credit change was recorded as a dialogue deal.
        static bool WatchDialogue(FWGameData gd, long cr)
        {
            TK_NPCSetBean nb = null;
            try { var dlg = GalaxyEventsDialog.instance; if (dlg != null && dlg && dlg.gameObject.activeInHierarchy) nb = dlg.NpcBean; } catch { }
            FWFleet fl = null;
            try { fl = (nb as TK_NPCSetBean_Fleet)?.getFleetBean()?.bean; } catch { }
            if (fl == null) { dlgNpc = -1; dlgHold = null; return false; }
            long npc = -1;
            try { npc = nb.getNpcUid(); } catch { }
            if (npc != dlgNpc || dlgHold == null)
            {
                // A new dialogue: copy the hold (under the game's locks; when they are busy, next frame).
                if (!GameLock.TryEnter(gd)) return false;
                try
                {
                    dlgHold = HoldCounts(gd);
                    dlgCredits = cr; dlgNpc = npc; dlgFleetUid = fl.getUid();
                    GalaxyNPCFleetType t; string why;
                    dlgSmuggler = FleetPlayerAntiDisturbService.TryResolveType(fl, out t, out why) && t == GalaxyNPCFleetType.Smuggler;
                    string g = fl.pos?.galaxyId;
                    dlgMarket = (dlgSmuggler ? "走私船 " : "商队 ") + (string.IsNullOrEmpty(fl.name) ? "?" : fl.name) + "（" + (galaxyName(g) ?? g) + "）对话";
                }
                catch (Exception e) { dlgHold = null; Debug.LogWarning("[TradeHelper] ledger dialogue: " + e.Message); }
                finally { GameLock.Exit(gd); }
                return false;
            }
            if (cr == dlgCredits) return false;
            if (!GameLock.TryEnter(gd)) return false;
            Dictionary<string, int> now;
            try { now = HoldCounts(gd); }
            catch (Exception e) { Debug.LogWarning("[TradeHelper] ledger dialogue: " + e.Message); return false; }
            finally { GameLock.Exit(gd); }
            RecordDialogue(gd, cr - dlgCredits, dlgHold, now);
            dlgHold = now; dlgCredits = cr;
            return true;
        }

        static void RecordDialogue(FWGameData gd, long delta, Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var e = new LedgerEntry { time = DateTime.Now.ToString("MM-dd HH:mm:ss"), creditDelta = delta, marketUid = -Math.Abs(dlgFleetUid), market = dlgMarket };
            try { e.gameMM = gd.elapsedMM; } catch { }
            var keys = new HashSet<string>(before.Keys); keys.UnionWith(after.Keys);
            var deals = dlgSmuggler ? Caravans.Deals() : null;
            double sum = 0; bool priced = true;
            foreach (var k in keys)
            {
                int b0, a0; before.TryGetValue(k, out b0); after.TryGetValue(k, out a0);
                int d = a0 - b0;
                if (d == 0) continue;
                var li = new LedgerItem { key = k, name = goodsName(k) ?? k, buy = d > 0, qty = Math.Abs(d) };
                double price = double.NaN;
                if (deals != null)
                {
                    if (d < 0)
                    {
                        foreach (var kv in deals.buys) if (kv.Key == k) { price = Math.Floor(-d * kv.Value * deals.rate); break; }
                    }
                    else
                    {
                        foreach (var o in deals.sells) if (o.type + "_" + o.id == k && o.count == d) { price = -o.cost; break; }
                    }
                }
                if (double.IsNaN(price)) priced = false; else { li.net = price; li.raw = Math.Abs(price); sum += price; }
                e.items.Add(li);
            }
            // One goods line and no fixed deal: the whole credit change is that line's price.
            if (!priced && e.items.Count == 1) { e.items[0].net = delta; e.items[0].raw = Math.Abs(delta); priced = true; sum = delta; }
            e.billDelta = (long)Math.Round(sum);
            e.exact = priced && e.items.Count > 0 && e.billDelta == delta;
            if (e.items.Count == 0) e.note = "对话里的收支，货舱里的货没变（议价、勒索、任务奖励之类）";
            else if (!priced) e.note = "对话交易：货舱的变化记下了，但模组不知道每样的单价";
            else if (!e.exact) e.note = "对话交易：按走私船的固定价算应为 " + e.billDelta + "，实际 " + delta + "（差 " + (delta - e.billDelta) + "；同种货分成几堆时游戏会少付）";
            else e.note = dlgSmuggler ? "走私船对话里的固定买卖（不扣税）" : "对话交易";
            entries.Add(e);
            pending.Add(e);
            version++;
            Save();
            Debug.Log("[TradeHelper] dialogue deal recorded: " + e.market + " credits " + delta + " items " + e.items.Count + (e.exact ? " exact" : " (" + e.note + ")"));
        }

        static string Key(StorageItemInstance it) => it.RawData is FWThingBean t ? t.type + "_" + t.id : "item" + it.ItemID;

        static void Record(FWGameData gd, long delta, long checkouts)
        {
            var e = new LedgerEntry { time = DateTime.Now.ToString("MM-dd HH:mm:ss"), creditDelta = delta };
            try { e.gameMM = gd.elapsedMM; } catch { }
            var b = bill;
            if (b != null && checkouts == 1 && Time.unscaledTime - b.at < 120f)
            {
                e.marketUid = b.marketUid; e.market = b.market; e.tax = b.tax; e.billDelta = b.delta;
                e.items = b.items;
                e.exact = b.complete && delta == b.delta;
                if (!e.exact)
                    e.note = "信用点实际变化 " + delta + "，结账单算的是 " + b.delta + "（差 " + (delta - b.delta) + "：结账前价格刚变，或同一时刻还有别的收支）；各行金额是结账单的";
            }
            else if (checkouts > 1) e.note = "两次结账之间没来得及看购物车，只记下信用点变化";
            else e.note = "模组没看到这次结账的购物车（不是在贸易市场或商队的交易界面里结的账），只记下信用点变化";
            bill = null; billSig = null;
            entries.Add(e);
            pending.Add(e);
            version++;
            Save();
            Debug.Log("[TradeHelper] checkout recorded: " + (e.market ?? "?") + " credits " + delta + (e.exact ? " (matches the bill)" : " (" + e.note + ")"));
        }
    }
}
