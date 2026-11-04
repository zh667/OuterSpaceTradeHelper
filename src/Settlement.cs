using System;
using System.Collections.Generic;
using UnityEngine;

namespace TradeHelper
{
    // Background market catch-up for the stations the player picked. Uses the game's own bounded entry point
    // FWExchangeSpace.ReleasePendingSettlementBatch: the same work as opening the station's exchange, spread over
    // frames. A never-opened market builds its store inside the first settled day (FWExchangeSpace.settleNextDay
    // calls initializeStore); one that owes no days is initialised with GetItemsForUse, like opening it.
    public class Settlement
    {
        readonly Queue<long> queue = new Queue<long>();
        readonly HashSet<long> queued = new HashSet<long>();
        public string lastError;
        public int daysDoneTotal, marketsDone;
        public long currentUid = -1;
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
                if (which(id)) { queued.Remove(id); suppressed.Add(id); n++; if (id == currentUid) { currentUid = -1; currentLeft = 0; } }
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
            queue.Clear(); queued.Clear(); suppressed.Clear(); currentUid = -1; currentLeft = 0;
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

        public void Tick(Config cfg)
        {
            var gd = MarketScanner.Game;
            if (gd == null) { if (queue.Count > 0) Clear(); return; }
            if (Time.unscaledTime >= nextKeepCheck)
            {
                nextKeepCheck = Time.unscaledTime + 5f;
                if (cfg.keepFresh.Count > 0)
                    foreach (var kv in MarketScanner.EnumerateExchanges(gd))
                    {
                        var ex = kv.Key;
                        if (!cfg.keepFresh.Contains(ex.uid) || suppressed.Contains(ex.uid) || queued.Contains(ex.uid)) continue;
                        bool pending = false;
                        try { pending = ex.hasPendingSettlement(); } catch { }
                        if (pending || !ex.storeInitialized) { if (queued.Add(ex.uid)) queue.Enqueue(ex.uid); }
                    }
            }
            if (paused || queue.Count == 0) return;
            long id = queue.Peek();
            var target = Find(gd, id);
            if (target == null) { Drop(); return; }
            currentUid = id;
            try
            {
                int left = PendingOf(target);
                currentLeft = left;
                if (left == 0)
                {
                    if (!target.storeInitialized) target.GetItemsForUse(); // nothing owed: build the store like opening it
                    Finish(id);
                    return;
                }
                int done = target.ReleasePendingSettlementBatch(Math.Max(1, cfg.daysPerFrame), Math.Max(1.0, cfg.msPerFrame), false);
                daysDoneTotal += done;
                currentLeft = PendingOf(target);
                if (currentLeft == 0)
                {
                    if (!target.storeInitialized) target.GetItemsForUse();
                    Finish(id);
                }
                else if (done == 0)
                {
                    // No progress this frame (e.g. the world clock is mid-advance): move it to the back.
                    queue.Dequeue(); queue.Enqueue(id);
                }
            }
            catch (Exception e)
            {
                lastError = DateTime.Now.ToString("HH:mm:ss") + " " + e.GetType().Name + ": " + e.Message;
                Debug.LogWarning("[TradeHelper] settlement uid=" + id + " failed: " + e);
                Drop();
            }
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
