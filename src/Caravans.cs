using System;
using System.Collections.Generic;
using TradeHelper.Ui;
using UnityEngine;

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

    // How long a caravan or smuggler in the scene still has on its route. The game ends it when it has flown its route
    // (GalaxyNPCFleet.OnPathCompleted -> settled "NaturalComplete"), usually long before its disappear time. Every
    // second: its position (GalaxyNPCFleet local position), its next route point (path module's CurrentPathIndex into the
    // route the game restored from FWFactionFleet.activityRoutePoints), the route length left, and its speed measured
    // from how far it moved per game minute. Read-only, main thread.
    public static class Motion
    {
        class Track { public Vector2 pos; public long mm; public double speed = -1; public int idx = -1, points; public double left = -1; public float seen; }
        static readonly Dictionary<long, Track> tracks = new Dictionary<long, Track>();
        static float next;

        public static void Tick(FWGameData gd, Snapshot snap)
        {
            if (gd == null || snap == null || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 1f;
            var mgr = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance;
            if (mgr == null) return;
            long mm = gd.elapsedMM;
            foreach (var c in snap.caravans)
            {
                if (c.route == null || c.journey) continue;
                Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleet npc = null;
                try { npc = mgr.GetFleetByUid(c.uid); } catch { }
                if (npc == null || !npc) continue;
                Vector2 p = npc.GetLocalPosition();
                // The path it really flies (the path module's PathPoints): the game rebuilds it when the fleet comes into
                // the scene (a smuggler gets a new black-market route; other duties have their points resolved), so it is
                // not the saved route.
                int idx = -1; List<Vector2> path = null;
                try
                {
                    var pf = npc.GetModule<Script.FreeWorldAirShip.Galaxy.NPCFleet.Modules.GalaxyNPCFleetPathFindingModule>();
                    if (pf != null && pf.HasValidPath) { idx = pf.CurrentPathIndex; path = pf.PathPoints; }
                }
                catch { }
                Track t;
                if (!tracks.TryGetValue(c.uid, out t)) { t = new Track { pos = p, mm = mm }; tracks[c.uid] = t; }
                else if (mm > t.mm)
                {
                    double d = Vector2.Distance(p, t.pos), dt = mm - t.mm;
                    if (d > 1e-4)
                    {
                        double v = d / dt;   // units per game millisecond
                        t.speed = t.speed < 0 ? v : t.speed * 0.6 + v * 0.4;
                    }
                    t.pos = p; t.mm = mm;
                }
                t.seen = Time.unscaledTime;
                t.idx = idx;
                t.points = path != null ? path.Count : 0;
                t.left = idx >= 0 ? RouteLeft(path, idx, p) : -1;
            }
        }

        // The path a fleet in the scene still has to fly (its position, then the path points from the next one), or
        // null. For drawing; read from the game's objects at call time.
        public static List<Vector2> PathAhead(long uid)
        {
            var mgr = Script.FreeWorldAirShip.Galaxy.NPCFleet.GalaxyNPCFleetManager.Instance;
            var npc = mgr != null ? mgr.GetFleetByUid(uid) : null;
            if (npc == null || !npc) return null;
            var pf = npc.GetModule<Script.FreeWorldAirShip.Galaxy.NPCFleet.Modules.GalaxyNPCFleetPathFindingModule>();
            if (pf == null || !pf.HasValidPath || pf.PathPoints == null) return null;
            var r = new List<Vector2> { npc.GetLocalPosition() };
            for (int i = Math.Max(0, pf.CurrentPathIndex); i < pf.PathPoints.Count; i++) r.Add(pf.PathPoints[i]);
            return r;
        }

        // Typical flight speed measured in the scene (galaxy units per game hour; caravans 13-17, smugglers 12-18
        // including their stops), for fleets not in the scene.
        public const double TypicalPerHour = 15.0;

        // Route length still to fly: to the next point, then the remaining segments.
        public static double RouteLeft(List<Vector2> route, int idx, Vector2 pos)
        {
            if (route == null || idx < 0 || idx >= route.Count) return -1;
            double s = Vector2.Distance(pos, route[idx]);
            for (int i = idx + 1; i < route.Count; i++) s += Vector2.Distance(route[i - 1], route[i]);
            return s;
        }

        // Game time until it finishes its route, or -1 when not known yet (not in the scene, or not moved yet).
        public static long LeaveMM(long uid)
        {
            Track t;
            if (!tracks.TryGetValue(uid, out t) || t.speed <= 0 || t.left < 0 || Time.unscaledTime - t.seen > 5f) return -1;
            return (long)(t.left / t.speed);
        }

        public static string Describe(long uid)
        {
            Track t;
            return tracks.TryGetValue(uid, out t) ? "point " + t.idx + " of " + t.points + ", path left " + t.left.ToString("0.0") + ", speed/h " + (t.speed * 60000).ToString("0.00") : "none";
        }
    }

    // One galaxy in the smuggler forecast.
    public class SmugglerSpot
    {
        public const int Here = 0, Due = 1, Wait = 2, Unknown = 3, Never = 4;
        public string galaxyId, galaxyName, smuggler;
        public long smugglerUid = -1;
        public double fleetAu = -1;
        public int state, observe;
        public long waitMM, leftMM = -1;
        // Where the smuggler would appear if the fleet enters this galaxy's view today (the game's own plan for the
        // seed of this galaxy, slot and day): its first point and route, local coordinates.
        public bool predicted;
        public Vector2 predStart;
        public List<Vector2> predRoute;
        public long predDay = -1;
        public int predSlot = -1;
        public string predNote;
        public long predFromMM, predUntilMM;   // game-time window (absolute) in which entering gives this spawn point
        public long nextCheckMM;               // absolute game time of the galaxy's next check
        public string NotVisitedTag => observe >= 2 ? "" : observe >= 1 ? "（未探索）" : "（未发现）";
        public string StateText
        {
            get
            {
                switch (state)
                {
                    case Here: return "有：" + smuggler;
                    case Due: return "进星系画面就刷 1 支";
                    case Wait: return "要等";
                    case Never: return "不会刷（没有贸易市场）";
                    default: return "不确定（读不到市场条件）";
                }
            }
        }
    }

    public class CaravanInfo
    {
        public long uid;                 // the fleet's uid
        public string name, fleetName, force, kind, galaxyId, galaxyName, status = "", relation = "", source = "", priceNote = "";
        // The trader's name (what the galaxy map and the trade window call it) and, when different, the fleet's own name.
        public string FullName => string.IsNullOrEmpty(fleetName) || fleetName == name ? name : name + "（" + fleetName + "）";
        public bool black;               // 可疑商队: a smuggler fleet (black market)
        public int observe;              // galaxy exploration state (MarketInfo.observe)
        public double fleetAu = -1;
        public long leftMM = -1;         // game time until it leaves (-1: no set departure)
        public List<Vector2> route;      // its route in the galaxy (FWFactionFleet.activityRoutePoints, local coordinates)
        public double routeLen;          // that route's length
        public long appearMM = -1;       // absolute game time it appeared (activityAppearTime)
        public bool journey;             // crossing galaxies (FleetJourney): no route end here
        public long cash;                // the caravan's own credits: it cannot pay more than this for what it buys
        public int priceModifier = 150;  // the fleet's price modifier towards the player, percent (FWFleetRelation.priceModifier)
        public bool metPlayer;           // a relation record with the player's fleet exists
        public bool hostile;
        public float tax;
        public bool priced;              // prices could be computed (source market up to date)
        public bool present;             // in the game's scene with its goods (else only where it is and when it leaves)
        public bool kept;                // not in the scene, but its goods from the last time it was are known
        public bool smuggler;            // traded through the smuggler dialogue's fixed deals, not a trade window
        public double smugglerSaleTotal; public int smugglerSaleKinds, smugglerListSize; public bool smugglerSold;
        public List<CaravanLine> lines = new List<CaravanLine>();
        public string NotVisitedTag => observe >= 2 ? "" : observe >= 1 ? "（未探索）" : "（未发现）";
        // FWCaravan.GetTradePriceFactor: the multiplier is a float (getFleetDiscount = priceModifier / 100f), the tax a
        // float widened to double; computed the same way, to the last digit, so a rounded payment matches the checkout.
        double M => Single(priceModifier / 100f);
        // An explicit float conversion forces the rounding the game gets from returning a float; without it the runtime
        // may keep the quotient at double precision.
        static double Single(float v) => (double)(float)v;
        public double FactorBuy => Math.Max(0.0, 1.0 + Single(tax)) * M;
        public double FactorSell => Math.Max(0.0, 1.0 - Single(tax)) * (1.0 / M);
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
                    if (c.black) Smuggler(snap, c, car, hold, byUid, bestSell);
                    else if (car != null) Price(snap, c, car, hold, byUid, bestSell, bestBuy);
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
            try { Forecast(gd, snap, repo, observe); }
            catch (Exception e) { snap.errors.Add("走私船预测失败: " + e.Message); }
        }

        // Where a smuggler can appear. The game checks a galaxy when the galaxy view of the player's fleet's galaxy is
        // built (GalaxySceneInterface -> NomadicFleetSpawnService.RefreshForPlayerGalaxy): when its next check time
        // (FWGalaxy.nextNomadicFleetRefreshTime) has come, and the galaxy has no smuggler (nomadic, not settled, activity
        // type 8), and it can host one (GalaxyFleetTradeMarketPolicy.CanCreate: a galaxy market and an economic
        // spaceport), it creates one; then the next check is 5 game days later (NomadicFleetSpawnRules.RefreshInterval).
        static System.Reflection.MethodInfo canCreate;
        static bool canCreateLooked;
        static int CanHost(FWGameData gd, string galaxyId)
        {
            if (!canCreateLooked)
            {
                canCreateLooked = true;
                var t = typeof(FWGameData).Assembly.GetType("GalaxyFleetTradeMarketPolicy");
                canCreate = t?.GetMethod("CanCreate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(FWGameData), typeof(string), typeof(int) }, null);
            }
            if (canCreate == null) return -1;
            try { return (bool)canCreate.Invoke(null, new object[] { gd, galaxyId, (int)GalaxyNPCFleetType.Smuggler }) ? 1 : 0; }
            catch { return -1; }
        }

        // The smuggler the game would create on entering the galaxy view today (NomadicFleetSpawnService.TrySpawn):
        // slot = the first "nomadic-low-frequency:<galaxy>:smuggler:<i>" no unsettled fleet holds; seed = FNV-1a of
        // "<galaxy>:8:<slot>:<day>" (BuildStableSeed, day = FWGameData.ForceTime_Day); cycles =
        // NomadicFleetSpawnRules.ResolvePlannedCycles; then the game's own TryBuildLowFrequencyPlan on the galaxy's
        // spatial map (GalaxySpatialMapProvider.Resolve). No randomness beyond the seed.
        static System.Reflection.MethodInfo planFn;

        // On request from the panel (main thread, under the game's locks; false when they are busy).
        public static bool PredictNow(FWGameData gd, SmugglerSpot s)
        {
            if (gd == null || s == null || !GameLock.TryEnter(gd)) return false;
            try
            {
                var g = gd.getGalaxy(s.galaxyId);
                if (g != null) s.nextCheckMM = g.nextNomadicFleetRefreshTime;
                Predict(gd, s.galaxyId, gd.factionFleetRepository, s);
            }
            catch (Exception e) { s.predNote = "算不出出生点：" + (e.InnerException ?? e).Message; }
            finally { GameLock.Exit(gd); }
            return true;
        }

        const long DayMM = 1440000L, NoonMM = 720000L;   // WorldTimeTools: 1 game day, 12 game hours
        // The game's day number at a game time (ForceDailyRefreshSchedule.GetLastDueDay: the day turns at 12:00).
        public static long DayAt(long mm) => mm < NoonMM ? 0 : (mm - NoonMM) / DayMM;

        static void Predict(FWGameData gd, string galaxyId, List<FWFactionFleet> repo, SmugglerSpot s)
        {
            var star = GalaxyMapData.getInstance()?.getGalaxyStar(galaxyId);
            if (star == null) { s.predNote = "找不到这个星系"; return; }
            var map = Script.FreeWorldAirShip.Galaxy.NPCFleet.Modules.GalaxySpatialMapProvider.Resolve(gd, star);
            if (map == null) { s.predNote = "这个星系还没有空间图"; return; }
            int slot = -1;
            for (int i = 0; i < 64 && slot < 0; i++)
            {
                string key = "nomadic-low-frequency:" + galaxyId + ":smuggler:" + i;
                bool used = false;
                foreach (var f in repo) if (f != null && !f.resultSettled && f.activityTargetKey == key) { used = true; break; }
                if (!used) slot = i;
            }
            // Entering now (due) or when the next check comes (waiting): the day at that moment; the point holds until
            // the day turns at the next 12:00.
            long from = Math.Max(gd.elapsedMM, s.nextCheckMM);
            long day = from <= gd.elapsedMM ? gd.ForceTime_Day : DayAt(from);
            s.predFromMM = from;
            s.predUntilMM = (DayAt(from) + 1) * DayMM + NoonMM;
            string text = galaxyId + ":8:" + slot + ":" + Math.Max(0L, day);
            uint h = 2166136261u;
            unchecked { foreach (char ch in text) h = (h ^ ch) * 16777619; }
            int seed = (int)(h & 0x7FFFFFFF);
            int cycles = NomadicFleetSpawnRules.ResolvePlannedCycles((int)GalaxyNPCFleetType.Smuggler, seed);
            if (planFn == null)
                planFn = typeof(NomadicFleetSpawnService).GetMethod("TryBuildLowFrequencyPlan", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (planFn == null) { s.predNote = "游戏的出生点函数变了（游戏更新？）"; return; }
            var args = new object[] { map, (int)GalaxyNPCFleetType.Smuggler, seed, cycles, null, null };
            if (!(bool)planFn.Invoke(null, args)) { s.predNote = "这个星系今天挑不出出生点（游戏会刷不出来）"; return; }
            s.predicted = true;
            s.predStart = (Vector2)args[4];
            s.predRoute = new List<Vector2>((List<Vector2>)args[5]);
            s.predDay = day; s.predSlot = slot;
        }

        public const long RefreshIntervalMM = 7200000L;   // NomadicFleetSpawnRules.RefreshInterval: 5 game days

        static void Forecast(FWGameData gd, Snapshot snap, List<FWFactionFleet> repo, Dictionary<string, int> observe)
        {
            snap.smugglerSpots.Clear();
            var gals = gd.galaxyBlock?.knownGalaxies;
            if (gals == null) return;
            long now = gd.elapsedMM;
            var active = new Dictionary<string, FWFactionFleet>();
            foreach (var f in repo)
            {
                if (f == null || f.resultSettled || !f.IsNomadicOwned || string.IsNullOrEmpty(f.galaxyId)) continue;
                G_ForceFleetType_Def d = null;
                try { d = ImplDef<G_ForceFleetType_Def>.getDataID(f.gForceFleetTypeResId); } catch { }
                if (d != null && d.ActivityType == (int)GalaxyNPCFleetType.Smuggler && !active.ContainsKey(f.galaxyId)) active[f.galaxyId] = f;
            }
            foreach (var g in gals)
            {
                if (g == null || string.IsNullOrEmpty(g.id)) continue;
                var s = new SmugglerSpot { galaxyId = g.id, galaxyName = snap.GalaxyName(g.id), fleetAu = snap.RouteAu(snap.fleetGalaxyId, g.id), observe = g.observaState };
                FWFactionFleet sm;
                if (active.TryGetValue(g.id, out sm))
                {
                    s.state = SmugglerSpot.Here;
                    var c = snap.caravans.Find(x => x.uid == sm.uid);
                    s.smuggler = c != null ? c.FullName : sm.Name;
                    s.smugglerUid = sm.uid;
                    s.leftMM = sm.activityDisappearTime > 0 ? Math.Max(0, sm.activityDisappearTime - now) : -1;
                }
                else
                {
                    int host = CanHost(gd, g.id);
                    if (host == 0) s.state = SmugglerSpot.Never;
                    else
                    {
                        s.waitMM = Math.Max(0, g.nextNomadicFleetRefreshTime - now);
                        s.state = host < 0 ? SmugglerSpot.Unknown : s.waitMM == 0 ? SmugglerSpot.Due : SmugglerSpot.Wait;
                    }
                }
                s.nextCheckMM = g.nextNomadicFleetRefreshTime;
                // The fleet's own galaxy: predicted right away (its map is the one the game has loaded). Others on request.
                if ((s.state == SmugglerSpot.Due || s.state == SmugglerSpot.Wait) && g.id == snap.fleetGalaxyId)
                {
                    try { Predict(gd, g.id, repo, s); }
                    catch (Exception e) { s.predNote = "算不出出生点：" + (e.InnerException ?? e).Message; }
                }
                snap.smugglerSpots.Add(s);
            }
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
            // In the scene (f.fleet set) the caravan can be traded; a caravan kept from an earlier visit (only
            // materializedState) still has its goods, but the game trades only with fleets in the scene (it needs the
            // fleet's NPC: FWCaravan.getFleetDiscount -> getFleetCaravan()).
            var c = new CaravanInfo { uid = f.uid, black = car != null ? car.isBlackMarket : type == (int)GalaxyNPCFleetType.Smuggler, cash = car != null ? car.cp : -1, present = car != null && f.fleet != null, kept = car != null && f.fleet == null };
            c.fleetName = f.Name;
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
            c.journey = f.journey != null;
            if (f.activityRoutePoints != null && f.activityRoutePoints.Count >= 2)
            {
                c.route = new List<Vector2>(f.activityRoutePoints.Count);
                foreach (var pt in f.activityRoutePoints) if (pt != null) c.route.Add(new Vector2(pt.x, pt.y));
                for (int k = 1; k < c.route.Count; k++) c.routeLen += Vector2.Distance(c.route[k - 1], c.route[k]);
                c.appearMM = f.activityAppearTime;
            }
            if (c.kept)
                c.priceNote = "它现在不在场景里（舰队在它所在的星系、进入星系画面时才会出场：左上角“至星系”，F11；星域图拉近看到的星系里没有 NPC 舰队），下面是上次出场时的货和价格，出场后可能已经变了；不在场景里时不能交易";
            if (car == null)
            {
                c.relation = "—";
                c.priceNote = "还没出场：游戏只在你的舰队在它所在的星系、并且进入星系画面（左上角“至星系”，F11）时才把它放进场景、生成它的货；母舰画面、星域图（F12）拉近看到的星系里都不会。进去后点“刷新”，或在本页点“追踪”再点“定位追踪的商队”自动进去。出场后它会用商人的名字显示";
                return c;
            }
            // The fleet's relation record with the player's fleet, read without creating one (FWFleet.getFleetRelation
            // adds a record when missing; a new record starts at priceModifier 150).
            // The game prices with the relation on the fleet bound to the caravan's NPC (FWCaravan.getFleetDiscount ->
            // getFleetCaravan()); dialogue price changes (ET_CSAPI.player_addpricemodifier) are written there too.
            FWFleetRelation rel = null;
            FWFleet rf = null;
            try { rf = car.getFleetCaravan(); } catch { }
            var rd = (rf ?? cf).relationData;
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

            // The rest of what it buys (its trader's purchase list, MapTraderSellDef of the trader's SellGroup), priced
            // for one unit, so the player sees what to bring.
            try
            {
                var def = car.getDef();
                var have = new HashSet<string>();
                foreach (var l in c.lines) if (!l.sells) have.Add(l.key);
                foreach (var sd in ImplDef<MapTraderSellDef>.beans)
                {
                    if (sd == null || sd.ItemGroup != def.SellGroup || sd.itemReward == null) continue;
                    int ty = sd.itemReward.type; string id = sd.itemReward.id;
                    if (ty == 58 || ty == 20 || FWEquipmentPricePressure.IsEquipment(ty)) continue;   // crew and equipment: only when held
                    var key = ty + "_" + id;
                    if (have.Contains(key)) continue;
                    have.Add(key);
                    var t = new FWThingBean { type = ty, id = id, sum = 1 };
                    var line = NewLine(snap, key, false, 0, src);
                    line.note = "你舰队背包里没有";
                    if (live || !NeedsSourceMarket(t))
                    {
                        try { line.unit = RawPurchase(car, t, 1, live) * c.FactorSell; }
                        catch (Exception e) { line.note = Join(line.note, "算不出价格：" + e.Message); }
                    }
                    GoodsQuote q;
                    if (line.gid != null && bestSell.TryGetValue(line.gid, out q)) { line.bestName = q.market.name; line.best = q.sell1; }
                    else line.note = Join(line.note, "空间站都不收");
                    c.lines.Add(line);
                }
            }
            catch (Exception e) { c.priceNote = Join(c.priceNote, "读它的收购清单失败：" + e.Message); }
        }

        // ---- Smuggler fleets (黑潮走私船): no trade window, only the dialogue's fixed deals ----
        // The smuggler's dialogue (TK_TaskChain IllegalFleetShell0) offers fixed purchases and one sale of the hold's
        // "suspicious goods"; their numbers live in the triggers' Lua scripts (TK_EventTriggerDef.TriggerScript), read
        // here so a game update carries over. Each "once" deal is remembered per NPC in the save's ET records.
        public class Offer { public int type; public string id; public int count; public long cost; public string once; }
        public class SmugglerDeals
        {
            public List<Offer> sells = new List<Offer>();
            public List<KeyValuePair<string, double>> buys = new List<KeyValuePair<string, double>>();   // "type_id" -> base price
            public double rate = 2;
            public string buyOnce;
            public string error;
        }
        static SmugglerDeals deals;
        public static readonly string[] SellTriggers = { "IllegalFleet-BuyUraninite", "IllegalFleet-BuyHelium3", "IllegalFleet-BuyNavCore" };
        public const string BuyTrigger = "IllegalFleet-SellContraband";

        static Dictionary<string, string> LuaLocals(string script)
        {
            var d = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(script, @"local\s+(\w+)\s*=\s*(-?\d+(?:\.\d+)?|""[^""]*"")"))
                if (!d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
            return d;
        }

        static string Arg(Dictionary<string, string> vars, string a)
        {
            a = a.Trim().Trim('"');
            string v;
            return vars.TryGetValue(a, out v) ? v : a;
        }

        public static SmugglerDeals Deals()
        {
            if (deals != null) return deals;
            var d = new SmugglerDeals();
            try
            {
                foreach (var id in SellTriggers)
                {
                    var tr = ImplDef<TK_EventTriggerDef>.getDataID(id);
                    if (tr == null || string.IsNullOrEmpty(tr.TriggerScript)) continue;
                    var vars = LuaLocals(tr.TriggerScript);
                    var add = System.Text.RegularExpressions.Regex.Match(tr.TriggerScript, @"Player\.AddItem\(([^,]+),([^,]+),([^,]+),");
                    if (!add.Success || !vars.ContainsKey("COST")) continue;
                    var o = new Offer { count = int.Parse(Arg(vars, add.Groups[1].Value)), type = int.Parse(Arg(vars, add.Groups[2].Value)), id = Arg(vars, add.Groups[3].Value), cost = long.Parse(vars["COST"]) };
                    var once = System.Text.RegularExpressions.Regex.Match(tr.TriggerScript, @"ONCE_KEY\s*=\s*""([^""]+)""\s*\.\.");
                    if (once.Success) o.once = once.Groups[1].Value;
                    d.sells.Add(o);
                }
                var st = ImplDef<TK_EventTriggerDef>.getDataID(BuyTrigger);
                if (st != null && !string.IsNullOrEmpty(st.TriggerScript))
                {
                    var vars = LuaLocals(st.TriggerScript);
                    string r; if (vars.TryGetValue("PRICE_RATE", out r)) d.rate = double.Parse(r, System.Globalization.CultureInfo.InvariantCulture);
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(st.TriggerScript, @"\{\s*(\d+)\s*,\s*""([^""]+)""\s*,\s*(\d+(?:\.\d+)?)\s*\}"))
                        d.buys.Add(new KeyValuePair<string, double>(m.Groups[1].Value + "_" + m.Groups[2].Value, double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)));
                    var once = System.Text.RegularExpressions.Regex.Match(st.TriggerScript, @"ONCE_KEY\s*=\s*""([^""]+)""\s*\.\.");
                    if (once.Success) d.buyOnce = once.Groups[1].Value;
                }
            }
            catch (Exception e) { d.error = e.Message; }
            deals = d;
            return d;
        }

        // ET_CSAPI.utils_tempvarlong: the deal was already made with this NPC.
        static bool Done(string prefix, long npcUid)
        {
            if (prefix == null || npcUid < 0) return false;
            try { var rec = UserBean.getInstance().getET_Record(prefix + npcUid); return rec != null && rec.longValue > 0; } catch { return false; }
        }

        // The hold sale's count and removal for one goods, on copies of the fleet's stacks (see Smuggler).
        static void ReplayHoldSale(List<FWThingBean> things, string key, out int taken, out int paid)
        {
            taken = paid = 0;
            var stacks = new List<FWThingBean>();
            foreach (var t in new List<FWThingBean>(things))
                if (t != null && t.type + "_" + t.id == key && t.getSum() > 0) stacks.Add(t.copyBean());
            if (stacks.Count == 0) return;
            int before = FirstGroup(stacks);
            int num = before;
            foreach (var t in stacks)
            {
                if (num <= 0) break;
                int h = t.getSum();
                if (num <= h) { t.setSum(h - num); num = 0; }
                else { num -= h; t.setSum(0); }
            }
            stacks.RemoveAll(t => t.getSum() <= 0);
            int after = stacks.Count == 0 ? 0 : FirstGroup(stacks);
            taken = before;
            paid = Math.Max(0, Math.Min(before, before - after));
        }

        static int FirstGroup(List<FWThingBean> stacks)
        {
            foreach (var g in AirShipLocalData.Instance.getThingsByGroup(stacks, false))
                if (g != null && g.getSum() > 0) return g.getSum();
            return 0;
        }

        static void Smuggler(Snapshot snap, CaravanInfo c, FWCaravan car, List<FWThingBean> hold, Dictionary<long, MarketInfo> byUid, Dictionary<string, GoodsQuote> bestSell)
        {
            var d = Deals();
            c.smuggler = true;
            c.priced = true;
            long npc = car != null ? car.NpcUid : -1;
            MarketInfo src = null;
            if (car != null) byUid.TryGetValue(car.sourceExchangeSpaceUid, out src);
            if (src == null) src = snap.markets.Find(m => !m.planet && m.galaxyId == c.galaxyId);
            if (d.error != null) c.priceNote = "读走私船的对话脚本失败：" + d.error;
            foreach (var o in d.sells)
            {
                var line = NewLine(snap, o.type + "_" + o.id, true, o.count, src);
                line.total = o.cost; line.unit = o.count > 0 ? (double)o.cost / o.count : double.NaN;
                line.note = (o.once != null ? "对话选项，每支船只卖一次" : "对话选项") + "，" + o.count + " 个一共 " + Fmt.Money(o.cost);
                if (o.once != null && Done(o.once, npc)) line.note = "这支船已经卖给过你，不会再卖";
                GoodsQuote q;
                if (line.gid != null && bestSell.TryGetValue(line.gid, out q)) { line.bestName = q.market.name; line.best = q.sell1; }
                else line.note = Join(line.note, "空间站都不收");
                c.lines.Add(line);
            }
            // "出售货舱里的可疑货物": everything on its list in the fleet's hold, at base x rate each, rounded down per goods.
            var held = new Dictionary<string, int>();
            if (hold != null)
                foreach (var t in hold)
                {
                    if (t == null || t.getSum() <= 0) continue;
                    var k = t.type + "_" + t.id; int v; held.TryGetValue(k, out v); held[k] = v + t.getSum();
                }
            // The dialogue's hold sale, step by step (IllegalFleet-SellContraband):
            //   before = Global.Player.GetItemCount(t, id, 1)  -> the fleet's getThingBeanByTypeId_Group: the FIRST group of
            //            that goods among the things grouped by AirShipLocalData.getThingsByGroup, not the total;
            //   Player.AddItem(-before, t, id, 1)              -> takes `before` pieces from the stacks in list order;
            //   after = GetItemCount again; it pays for min(before, before - after).
            // With the goods in several groups (pieces bought apart, such as weapons) it takes more than it pays for. The
            // mod replays the steps on copies of the fleet's stacks of that goods.
            List<FWThingBean> fleetThings = null;
            try { fleetThings = AirShipLocalData.Instance?.getCurFleet()?.bean?.getThings(); } catch { }
            bool sold = d.buyOnce != null && Done(d.buyOnce, npc);
            double all = 0; int kinds = 0;
            foreach (var kv in d.buys)
            {
                int total; held.TryGetValue(kv.Key, out total);
                if (total <= 0) continue;
                int taken = total, paid = total;
                if (fleetThings != null) ReplayHoldSale(fleetThings, kv.Key, out taken, out paid);
                if (taken <= 0) continue;
                var line = NewLine(snap, kv.Key, false, taken, src);
                line.unit = kv.Value * d.rate;
                line.total = Math.Floor(paid * kv.Value * d.rate);
                line.note = sold ? "这支船已经收过一次，不会再收" : "对话“出售货舱里的可疑货物”：清单上的货一次全卖，不能挑";
                if (taken < total) line.note = Join(line.note, "货舱里有 " + total + " 个，游戏只拿第一堆的 " + taken + " 个");
                if (paid < taken) line.note = Join(line.note, "<color=#E06A5A>游戏会拿走 " + taken + " 个却只付 " + paid + " 个的钱（同种货分成几堆时游戏算错）：卖之前把它放到一起或先卖到别处</color>");
                GoodsQuote q;
                if (line.gid != null && bestSell.TryGetValue(line.gid, out q)) { line.bestName = q.market.name; line.best = q.sell1; }
                else line.note = Join(line.note, "空间站都不收");
                c.lines.Add(line);
                all += line.total; kinds++;
            }
            // The rest of its list (not in the fleet's hold), so the player sees what it would take and for how much.
            foreach (var kv in d.buys)
            {
                if (c.lines.Exists(l => !l.sells && l.key == kv.Key)) continue;
                var line = NewLine(snap, kv.Key, false, 0, src);
                line.unit = kv.Value * d.rate;
                line.note = sold ? "这支船已经收过一次，不会再收" : "舰队货舱里没有（要放在舰队货舱里才收；数量不限，每支船只收一次）";
                GoodsQuote q;
                if (line.gid != null && bestSell.TryGetValue(line.gid, out q)) { line.bestName = q.market.name; line.best = q.sell1; }
                else line.note = Join(line.note, "空间站都不收");
                c.lines.Add(line);
            }
            c.smugglerSaleTotal = all; c.smugglerSaleKinds = kinds; c.smugglerSold = sold; c.smugglerListSize = d.buys.Count;
            if (car == null) c.priceNote = "还没出场（舰队在它所在的星系、进入星系画面时它才会出现：左上角“至星系”，F11）；对话里的买卖价格是固定的，这里先列出来";
            else if (npc < 0) c.priceNote = Join(c.priceNote, "读不到它的编号，“只卖/只收一次”的状态未知");
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
