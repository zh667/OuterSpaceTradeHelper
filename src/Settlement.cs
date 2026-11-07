using System;
using System.Collections.Generic;
using UnityEngine;

namespace TradeHelper
{
    // Background market catch-up for the stations the player picked. Each market is handed to the game's own
    // FWExchangeSpace.ensureStoreInitializedAsync, exactly what opening its exchange does ("贸易数据通讯中"): the game
    // settles every owed day and builds a never-opened store on a worker thread (one market at a time), then calls back
    // on the main thread. The next market starts after the callback.
    public class Settlement
    {
        readonly Queue<long> queue = new Queue<long>();
        readonly HashSet<long> queued = new HashSet<long>();
        public string lastError;
        public int daysDoneTotal, marketsDone;
        public long currentUid = -1;
        // The market the game is settling on its worker thread right now (-1: none). The scanner leaves it alone.
        public static long inFlightUid = -1;
        static FWExchangeSpace inFlightEx;
        static int inFlightDays;
        static float inFlightSince;
        static volatile bool inFlightDone;
        static Exception inFlightError;
        public bool InFlight => inFlightUid >= 0;
        public int currentLeft;
        float nextKeepCheck;
        public bool paused;

        // Stations the player cancelled: "keep up to date" will not re-queue them until the player queues them again.
        readonly HashSet<long> suppressed = new HashSet<long>();

        public int Pending => queue.Count;
        public bool IsQueued(long uid) => queued.Contains(uid);
        public bool IsCancelled(long uid) => suppressed.Contains(uid);
        public event Action<long> MarketFinished;

        public void Enqueue(long uid)
        {
            suppressed.Remove(uid);
            if (queued.Add(uid)) queue.Enqueue(uid);
        }

        // Removes queued stations (the one being worked on stops before its next day). Days already settled stay settled:
        // the game has no way to undo them.
        public int Cancel(Predicate<long> which)
        {
            int n = 0;
            var keep = new List<long>();
            foreach (var id in queue)
            {
                // The market already on the game's worker thread finishes its catch-up; the rest are dropped.
                if (which(id)) { queued.Remove(id); suppressed.Add(id); n++; if (id == currentUid && id != inFlightUid) { currentUid = -1; currentLeft = 0; } }
                else keep.Add(id);
            }
            queue.Clear();
            foreach (var id in keep) queue.Enqueue(id);
            return n;
        }

        public void CancelAll() => Cancel(_ => true);

        // Session reset (another save loaded): forget everything.
        public void Clear()
        {
            queue.Clear(); queued.Clear(); suppressed.Clear(); currentUid = -1; currentLeft = 0; keepDoneDay = -1;
            inFlightUid = -1; inFlightEx = null;
        }

        static FWExchangeSpace Find(FWGameData gd, long uid)
        {
            foreach (var kv in MarketScanner.EnumerateExchanges(gd))
                if (kv.Key.uid == uid) return kv.Key;
            return null;
        }

        static int PendingOf(FWExchangeSpace ex)
        {
            try { return ex.pendingSettlementDays != null ? ex.pendingSettlementDays.Count : 0; } catch { return 0; }
        }

        public const long DayMM = 1440000L, HourMM = 60000L;
        static long keepDoneDay = -1;
        public static long GameHour(FWGameData gd) => (gd.elapsedMM % DayMM) / HourMM;

        // "Keep up to date" catch-ups run when they owe a day, or once a game day at the hour the player chose.
        static bool KeepFreshDue(FWGameData gd, Config cfg)
        {
            if (cfg.keepFreshHour < 0) return true;
            long day = gd.elapsedMM / DayMM;
            if (keepDoneDay == day || GameHour(gd) < cfg.keepFreshHour) return false;
            keepDoneDay = day;
            return true;
        }

        public void Tick(Config cfg)
        {
            var gd = MarketScanner.Game;
            if (gd == null) { if (queue.Count > 0) Clear(); return; }
            if (Time.unscaledTime >= nextKeepCheck)
            {
                nextKeepCheck = Time.unscaledTime + 5f;
                if (cfg.keepFresh.Count > 0 && KeepFreshDue(gd, cfg))
                    foreach (var kv in MarketScanner.EnumerateExchanges(gd))
                    {
                        var ex = kv.Key;
                        if (!cfg.keepFresh.Contains(ex.uid) || suppressed.Contains(ex.uid) || queued.Contains(ex.uid)) continue;
                        bool pending = false;
                        try { pending = ex.hasPendingSettlement(); } catch { }
                        if (pending || !ex.storeInitialized) { if (queued.Add(ex.uid)) queue.Enqueue(ex.uid); }
                    }
            }
            if (inFlightUid >= 0) { WatchInFlight(gd); return; }
            if (paused || queue.Count == 0) return;
            long id = queue.Peek();
            var target = Find(gd, id);
            if (target == null) { Drop(); return; }
            currentUid = id;
            try
            {
                currentLeft = PendingOf(target);
                if (currentLeft == 0 && target.storeInitialized) { Finish(id); return; }
                inFlightUid = id; inFlightEx = target; inFlightDays = currentLeft; inFlightSince = Time.unscaledTime;
                inFlightDone = false; inFlightError = null;
                long t0 = Perf.Now;
                target.ensureStoreInitializedAsync(ex => { inFlightDone = true; }, e => { inFlightError = e; inFlightDone = true; });
                Perf.settleMsTotal += Perf.Ms(Perf.Now - t0);
            }
            catch (Exception e)
            {
                lastError = DateTime.Now.ToString("HH:mm:ss") + " " + e.GetType().Name + ": " + e.Message;
                Debug.LogWarning("[TradeHelper] settlement uid=" + id + " failed: " + e);
                inFlightUid = -1;
                Drop();
            }
        }

        // The market on the game's worker thread: wait for its callback; if none comes (e.g. the trade session changed),
        // look at the market itself after a while.
        void WatchInFlight(FWGameData gd)
        {
            bool done = inFlightDone;
            if (!done && Time.unscaledTime - inFlightSince > 20f)
            {
                try { done = !inFlightEx.hasPendingSettlement() && inFlightEx.storeInitialized; } catch { }
            }
            if (!done && Time.unscaledTime - inFlightSince > 900f)
            {
                lastError = DateTime.Now.ToString("HH:mm:ss") + " 后台补算超过 15 分钟没有结束，已跳过";
                done = true;
            }
            if (!done) return;
            long id = inFlightUid;
            inFlightUid = -1; inFlightEx = null;
            if (inFlightError != null)
            {
                lastError = DateTime.Now.ToString("HH:mm:ss") + " " + inFlightError.GetType().Name + ": " + inFlightError.Message;
                Debug.LogWarning("[TradeHelper] settlement uid=" + id + " failed: " + inFlightError);
            }
            else
            {
                Perf.settledDays += inFlightDays;
                daysDoneTotal += inFlightDays;
            }
            if (queue.Count > 0 && queue.Peek() == id) Finish(id);
            else { marketsDone++; currentUid = -1; currentLeft = 0; try { MarketFinished?.Invoke(id); } catch { } }
        }

        void Finish(long id)
        {
            marketsDone++;
            Drop();
            try { MarketFinished?.Invoke(id); } catch { }
        }

        void Drop()
        {
            if (queue.Count == 0) return;
            queued.Remove(queue.Dequeue());
            currentUid = -1; currentLeft = 0;
        }
    }
}
