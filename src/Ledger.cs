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
            if (v != lastVersion)
            {
                Record(gd, cr - lastCredits, v - lastVersion);
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
                foreach (var crew in h.BuyCrewCart) { double p = (float)mCrew.Invoke(page, new object[] { crew, true }); b.buy += p; b.items.Add(new LedgerItem { key = "crew", name = "人员", buy = true, qty = 1, raw = p }); }
                foreach (var crew in h.SellCrewCart) { double p = (float)mCrew.Invoke(page, new object[] { crew, false }); b.sell += p; b.items.Add(new LedgerItem { key = "crew", name = "人员", buy = false, qty = 1, raw = p }); }
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
            else e.note = "没看到购物车（不是在贸易市场或商队的交易界面结的账），只记下信用点变化";
            bill = null; billSig = null;
            entries.Add(e);
            pending.Add(e);
            version++;
            Save();
            Debug.Log("[TradeHelper] checkout recorded: " + (e.market ?? "?") + " credits " + delta + (e.exact ? " (matches the bill)" : " (" + e.note + ")"));
        }
    }
}
