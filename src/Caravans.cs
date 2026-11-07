using System;
using System.Collections.Generic;
using TradeHelper.Ui;

namespace TradeHelper
{
    // One goods line of a caravan: something it sells to the player, or something in the fleet's hold it would buy.
    public class CaravanLine
    {
        public string key, gid, name;
        public bool sells;               // true: the caravan sells it to the player; false: the caravan buys it from the player
        public int qty;                  // caravan stock (sells) or units in the fleet's hold (buys)
        public double unit = double.NaN; // first unit: price incl. tax and the caravan's price factor (sells) / credits received (buys)
        public double total = double.NaN;// all qty units at once (the game's quote moves the price unit by unit)
        public bool contraband;          // banned at the caravan's source market (a station there would not trade it)
        public string bestName;          // best station for the comparison (selling it there / buying it there)
        public double best = double.NaN; // that station's first unit: received when selling there, paid when buying there
        public string note = "";
        // Per unit, how much better the caravan is than the best station: for a line the caravan sells, station sale minus
        // caravan price (buy here, sell there); for a line it buys, caravan offer minus the best station sale.
        public double Gain => double.IsNaN(unit) ? double.NaN : sells ? (double.IsNaN(best) ? double.NaN : best - unit) : (double.IsNaN(best) ? unit : unit - best);
    }

    public class CaravanInfo
    {
        public long uid;                 // the fleet's uid
        public string name, force, kind, galaxyId, galaxyName, status = "", relation = "", source = "", priceNote = "";
        public bool black;               // 可疑商队: a smuggler fleet (black market)
        public int observe;              // galaxy exploration state (MarketInfo.observe)
        public double fleetAu = -1;
        public long leftMM = -1;         // game time until it leaves (-1: no set departure)
        public long cash;                // the caravan's own credits: it cannot pay more than this for what it buys
        public int priceModifier = 150;  // the fleet's price modifier towards the player, percent (FWFleetRelation.priceModifier)
        public bool metPlayer;           // a relation record with the player's fleet exists
        public bool hostile;
        public float tax;
        public bool priced;              // prices could be computed (source market up to date)
        public bool present;             // in the game's scene with its goods (else only where it is and when it leaves)
        public List<CaravanLine> lines = new List<CaravanLine>();
        public string NotVisitedTag => observe >= 2 ? "" : observe >= 1 ? "（未探索）" : "（未发现）";
        public double FactorBuy => Math.Max(0.0, 1.0 + tax) * (priceModifier / 100.0);
        public double FactorSell => Math.Max(0.0, 1.0 - tax) / (priceModifier / 100.0);
    }

    // Caravans (NPC trade fleets) and suspicious caravans (smuggler fleets, black market) the player can trade with.
    // Both are faction fleets (gd.factionFleetRepository) whose materialized state is an FWCaravanFleet carrying an
    // FWCaravan. A smuggler's caravan is created with isBlackMarket and stocks only goods its source market bans
    // (FWExchangeSpace.createCaravan goods filter). What any caravan buys is its trader's purchase list (isAcceptGood);
    // the game does not check contraband there.
    //
    // Prices follow the game's caravan trade page (CaravanInventoryContext.GetSellUnitPrice / GetPurchaseUnitPrice and
    // PageTradeInventory.QuoteCart): FWCaravan.QuotePressureTrade(...).rawPrice (normal goods: the source market's base
    // price factor with the caravan's own pressure) x GetTradePriceFactor = (1 +/- tax) x m (buying) or / m (selling),
    // tax = the source market's trade tax, m = the fleet's priceModifier towards the player / 100.
    // Read-only: the game's getFleetRelation and getPrice_收购原始单价 create records / cache prices when missing, so
    // the mod reads the relation list and the cached price lists itself, and quotes normal goods only when the source
    // market owes no settlement days (QuotePressureTrade would settle it otherwise).
    public static class Caravans
    {
        public const long GameHourMM = 60000;   // FWTimeUtils.GAME_DAY = 1440000

        static bool Usable(GoodsQuote q, Config cfg)
        {
            if (q == null || q.contraband) return false;
            if (!q.market.Explored && !cfg.includeUndiscovered) return false;
            if (q.market.planet) return false;
            if (!q.market.Exact && q.market.pendingDays > cfg.maxStaleDays) return false;
            return true;
        }

        public static void Scan(FWGameData gd, Snapshot snap, Config cfg)
        {
            var repo = gd.factionFleetRepository;
            if (repo == null) return;
            long now = gd.elapsedMM;
            long playerUid = -1;
            List<FWThingBean> hold = null;
            try
            {
                var pf = gd.galaxyBlock?.playerFleet;
                if (pf != null) { playerUid = pf.getUid(); var t = pf.getThings(); if (t != null) hold = new List<FWThingBean>(t); }
            }
            catch (Exception e) { snap.errors.Add("商队：读舰队背包失败: " + e.Message); }

            var observe = new Dictionary<string, int>();
            try { foreach (var g in gd.galaxyBlock.knownGalaxies) if (g?.id != null) observe[g.id] = g.observaState; } catch { }
            var byUid = new Dictionary<long, MarketInfo>();
            foreach (var m in snap.markets) byUid[m.uid] = m;

            // Best station prices per goods, first unit: sold there (received) and bought there (paid).
            var bestSell = new Dictionary<string, GoodsQuote>();
            var bestBuy = new Dictionary<string, GoodsQuote>();
            foreach (var m in snap.markets)
                foreach (var q in m.goods)
                {
                    if (!Usable(q, cfg)) continue;
                    GoodsQuote b;
                    if (q.sell1 > 0 && (!bestSell.TryGetValue(q.gid, out b) || q.sell1 > b.sell1)) bestSell[q.gid] = q;
                    if (q.buyable && q.buy1 > 0 && (!bestBuy.TryGetValue(q.gid, out b) || q.buy1 < b.buy1)) bestBuy[q.gid] = q;
                }

            foreach (var f in new List<FWFactionFleet>(repo))
            {
                CaravanInfo c = null;
                try
                {
                    if (f == null || f.resultSettled) continue;
                    // In the scene (the player's galaxy) the fleet object is f.fleet; elsewhere what was last kept of it.
                    var cf = (f.fleet ?? f.materializedState) as FWCaravanFleet;
                    var car = cf?.getCaravan();
                    int type = FleetType(f);
                    if (car == null && type != (int)GalaxyNPCFleetType.Trade && type != (int)GalaxyNPCFleetType.Smuggler) continue;
                    c = Read(gd, snap, f, cf, car, type, now, playerUid, observe, byUid);
                    if (car != null) Price(snap, c, car, hold, byUid, bestSell, bestBuy);
                }
                catch (Exception e)
                {
                    if (c == null) { snap.errors.Add("商队：读取失败: " + e.Message); continue; }
                    c.priceNote = "读这支商队时出错：" + e.Message;
                }
                if (c != null) snap.caravans.Add(c);
            }
            snap.caravans.Sort((a, b) =>
            {
                double x = a.fleetAu < 0 ? double.MaxValue : a.fleetAu, y = b.fleetAu < 0 ? double.MaxValue : b.fleetAu;
                return x.CompareTo(y);
            });
        }

        // The fleet's duty, as the game resolves it for faction fleets (NomadicFleetSpawnService.ResolveFleetType).
        static int FleetType(FWFactionFleet f)
        {
            if (f.runtimeFleetTypeOverride >= 0 && f.runtimeFleetTypeOverride < 11) return f.runtimeFleetTypeOverride;
            try { var d = ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId); return d != null ? d.ActivityType : -1; } catch { return -1; }
        }

        static CaravanInfo Read(FWGameData gd, Snapshot snap, FWFactionFleet f, FWCaravanFleet cf, FWCaravan car, int type, long now, long playerUid,
            Dictionary<string, int> observe, Dictionary<long, MarketInfo> byUid)
        {
            var c = new CaravanInfo { uid = f.uid, black = car != null ? car.isBlackMarket : type == (int)GalaxyNPCFleetType.Smuggler, cash = car != null ? car.cp : -1, present = car != null };
            c.name = cf != null && !string.IsNullOrEmpty(cf.name) ? cf.name : (f.Name ?? "商队");
            c.kind = c.black ? "可疑商队" : "商队";
            c.galaxyId = !string.IsNullOrEmpty(f.galaxyId) ? f.galaxyId : cf?.pos?.galaxyId;
            c.galaxyName = snap.GalaxyName(c.galaxyId);
            int ob; c.observe = c.galaxyId != null && observe.TryGetValue(c.galaxyId, out ob) ? ob : 0;
            c.fleetAu = snap.RouteAu(snap.fleetGalaxyId, c.galaxyId);
            try { var fd = ImplDef<G_StellarForce>.getDataID(f.ownerForceId); c.force = fd != null ? fd.Name : f.ownerForceId; } catch { c.force = f.ownerForceId; }
            if (f.activityDisappearTime > 0) c.leftMM = Math.Max(0, f.activityDisappearTime - now);
            if (f.activityAppearTime > now) c.status = "还没出现（约 " + Math.Max(1, (f.activityAppearTime - now) / GameHourMM) + " 小时后）";
            else c.status = f.journey != null ? "跨星系航行中（位置会变）" : "在这个星系里";
            if (car == null)
            {
                c.relation = "—";
                c.priceNote = "还没出场：游戏在你的舰队进入这个星系后才把它放进场景、生成它的货，到时这里才有货和价格";
                return c;
            }
            // The fleet's relation record with the player's fleet, read without creating one (FWFleet.getFleetRelation
            // adds a record when missing; a new record starts at priceModifier 150).
            FWFleetRelation rel = null;
            var rd = cf.relationData;
            if (rd?.fleetRelations != null)
                foreach (var r in rd.fleetRelations) if (r != null && r.fleetUid == playerUid) { rel = r; break; }
            c.metPlayer = rel != null;
            if (rel != null)
            {
                c.priceModifier = rel.priceModifier;
                var fr = rel.getFactionRelation();
                c.hostile = fr == FleetFactionRelation.Hostile;
                c.relation = fr == FleetFactionRelation.Friendly ? "友好" : fr == FleetFactionRelation.Hostile ? "敌对" : "中立";
            }
            else c.relation = "没打过交道";
            MarketInfo src;
            if (byUid.TryGetValue(car.sourceExchangeSpaceUid, out src))
            {
                c.source = src.name;
                c.tax = src.tax;
            }
            else
            {
                c.source = "来源站 " + car.sourceExchangeSpaceUid;
                try { var ex = car.getSourceExchangeSpace(); if (ex != null) c.tax = MarketScanner.TradeTax(ex); } catch { }
            }
            return c;
        }

        static bool NeedsSourceMarket(FWThingBean t)
        {
            if (t is FWRoleDNA || t is FWAnimalThing) return false;
            if (FWEquipmentPricePressure.IsEquipment(t.type)) return true;
            if (FWExchangeSpace.isSpecialGoods(t.type)) return false;
            return true;
        }

        static bool SourceLive(FWCaravan car, Dictionary<long, MarketInfo> byUid)
        {
            MarketInfo m;
            if (!byUid.TryGetValue(car.sourceExchangeSpaceUid, out m) || m.ex == null || !m.Exact) return false;
            if (m.ex.uid == Settlement.inFlightUid) return false;
            try { return !m.ex.hasPendingSettlement() && m.ex.isStoreInitialized(); } catch { return false; }
        }

        // The goods key the mod uses everywhere ("type_id"); DNA and creatures are keyed by what they are, as the game's
        // caravan price lists key them (FWCaravan.getExchangePriceKey: 58_roleId, 20_animalId).
        public static string ItemKey(FWBaseBean b)
        {
            if (b is FWRoleDNA d) return "58_" + d.roleId;
            if (b is FWAnimalThing a) return "20_" + a.animalId;
            return b is FWThingBean t ? t.type + "_" + t.id : null;
        }

        static bool IsCrew(FWBaseBean b) => b is FWRoleDNA || b is FWAnimalThing;

        static void Price(Snapshot snap, CaravanInfo c, FWCaravan car, List<FWThingBean> hold, Dictionary<long, MarketInfo> byUid,
            Dictionary<string, GoodsQuote> bestSell, Dictionary<string, GoodsQuote> bestBuy)
        {
            bool live = SourceLive(car, byUid);
            c.priced = live;
            MarketInfo src; byUid.TryGetValue(car.sourceExchangeSpaceUid, out src);
            if (!live) c.priceNote = src == null ? "来源站不在扫描结果里，这支商队的大部分价格算不出" :
                    "来源站 " + src.name + " " + src.Freshness + "：商队按它的行情报价，要等它变实时（可在“后台补算”页补算）";

            // What it sells.
            var sells = new Dictionary<string, List<FWThingBean>>();
            foreach (var b in new List<FWBaseBean>(car.allGoods))
            {
                var t = b as FWThingBean;
                if (t == null || t.getSum() <= 0) continue;
                List<FWThingBean> l; var k = ItemKey(t);
                if (!sells.TryGetValue(k, out l)) sells[k] = l = new List<FWThingBean>();
                l.Add(t);
            }
            foreach (var kv in sells)
            {
                var t = kv.Value[0];
                int n = 0; foreach (var x in kv.Value) n += x.getSum();
                var line = NewLine(snap, kv.Key, true, n, src);
                if (live || !NeedsSourceMarket(t))
                {
                    try
                    {
                        if (IsCrew(t))
                        {
                            // One at a time, each its own price (getPrice_贩卖原始单价: the caravan's price list).
                            double sum = 0;
                            foreach (var x in kv.Value) sum += car.getPrice_贩卖原始单价(x) * x.getSum();
                            line.unit = car.getPrice_贩卖原始单价(t) * c.FactorBuy;
                            line.total = sum * c.FactorBuy;
                        }
                        else
                        {
                            line.unit = RawSale(car, t, 1) * c.FactorBuy;
                            line.total = FWEquipmentPricePressure.IsEquipment(t.type) ? line.unit * n : RawSale(car, t, n) * c.FactorBuy;
                        }
                    }
                    catch (Exception e) { line.note = "算不出价格：" + e.Message; }
                }
                else line.note = "来源站不是实时，价格暂缺";
                GoodsQuote q;
                if (line.gid != null && bestSell.TryGetValue(line.gid, out q)) { line.bestName = q.market.name; line.best = q.sell1; }
                else line.note = Join(line.note, "没有空间站收（违禁或没有市场）");
                c.lines.Add(line);
            }

            // What it would buy from the fleet's hold (the caravan trade page sells from the fleet's hold).
            if (hold != null)
            {
                var held = new Dictionary<string, List<FWThingBean>>();
                foreach (var t in hold)
                {
                    if (t == null || t.getSum() <= 0) continue;
                    if (IsCrew(t)) continue;   // DNA and creatures are sold from the crew list, not the hold
                    List<FWThingBean> l; var k = ItemKey(t);
                    if (!held.TryGetValue(k, out l)) held[k] = l = new List<FWThingBean>();
                    l.Add(t);
                }
                foreach (var kv in held)
                {
                    var t = kv.Value[0];
                    bool accept;
                    try { accept = car.isAcceptGood(t); } catch { accept = false; }
                    if (!accept) continue;
                    int n = 0; foreach (var x in kv.Value) n += x.getSum();
                    var line = NewLine(snap, kv.Key, false, n, src);
                    if (live || !NeedsSourceMarket(t))
                    {
                        try
                        {
                            if (FWEquipmentPricePressure.IsEquipment(t.type))
                            {
                                double sum = 0;
                                foreach (var x in kv.Value) sum += RawPurchase(car, x, 1, live) * x.getSum();
                                line.unit = RawPurchase(car, t, 1, live) * c.FactorSell;
                                line.total = sum * c.FactorSell;
                                line.note = "装备按每件的耐久分别计价";
                            }
                            else
                            {
                                line.unit = RawPurchase(car, t, 1, live) * c.FactorSell;
                                line.total = RawPurchase(car, t, n, live) * c.FactorSell;
                            }
                        }
                        catch (Exception e) { line.note = "算不出价格：" + e.Message; }
                    }
                    else line.note = "来源站不是实时，价格暂缺";
                    GoodsQuote q;
                    if (line.gid != null && bestSell.TryGetValue(line.gid, out q)) { line.bestName = q.market.name; line.best = q.sell1; }
                    else line.note = Join(line.note, "空间站都不收（违禁或没有市场），只能卖给商队");
                    if (!double.IsNaN(line.total) && line.total > c.cash) line.note = Join(line.note, "商队只有 " + Fmt.Money(c.cash) + " 信用点，一次收不完");
                    c.lines.Add(line);
                }
            }
        }

        static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + "；" + b;

        static CaravanLine NewLine(Snapshot snap, string key, bool sells, int qty, MarketInfo src)
        {
            var line = new CaravanLine { key = key, sells = sells, qty = qty, name = MarketScanner.ItemName(snap, key) };
            if (key.StartsWith("58_") || key.StartsWith("20_")) line.note = "人员类：在交易界面的人员页买";
            string gid;
            if (snap.itemKeyToGid.TryGetValue(key, out gid)) line.gid = gid;
            GoodsQuote sq;
            if (src != null && gid != null && src.byGid.TryGetValue(gid, out sq)) line.contraband = sq.contraband;
            return line;
        }

        // FWCaravan.getPrice_贩卖原始总价: the pressure quote when there is one, else the legacy unit price x n.
        static double RawSale(FWCaravan car, FWThingBean t, int n)
        {
            object owner;
            var q = car.QuotePressureTrade(t, n, true, out owner);
            if (q != null) return FWEquipmentPricePressure.IsEquipment(t.type) ? q.unitPrice * n : q.rawPrice;
            return LegacySale(car, t) * n;
        }

        // FWCaravan.getLegacySaleUnitPrice.
        static double LegacySale(FWCaravan car, FWThingBean t)
        {
            var k = ItemKey(t);
            if (car.exchangePriceList != null)
                foreach (var p in car.exchangePriceList)
                    if (p != null && p.itemType + "_" + p.itemId == k) return p.unitPrice;
            var def = t.getDef();
            return def.Price * def.PriceUp * car.getPrice_贩卖因子(t);
        }

        // FWCaravan.getPrice_收购原始总价 without the price cache it writes.
        static double RawPurchase(FWCaravan car, FWThingBean t, int n, bool sourceLive)
        {
            object owner;
            var q = car.QuotePressureTrade(t, n, false, out owner);
            if (q != null) return FWEquipmentPricePressure.IsEquipment(t.type) ? q.unitPrice * n : q.rawPrice;
            return LegacyPurchase(car, t, sourceLive) * n;
        }

        // FWCaravan.getLegacyPurchaseUnitPrice: a cached price, else the source market's caravan purchase price (which
        // the game then caches), else the definition price x the trader's purchase factor.
        static double LegacyPurchase(FWCaravan car, FWThingBean t, bool sourceLive)
        {
            var k = ItemKey(t);
            if (car.exchangePurchasePriceList != null)
                foreach (var p in car.exchangePurchasePriceList)
                    if (p != null && p.itemType + "_" + p.itemId == k) return p.unitPrice;
            if (!sourceLive) throw new InvalidOperationException("来源站不是实时");
            var ex = car.getSourceExchangeSpace();
            float price;
            if (ex != null && ex.tryGetCaravanPurchaseUnitPrice(t, out price)) return price;
            return t.getDef().Price * car.getPrice_收购因子(t);
        }
    }
}
