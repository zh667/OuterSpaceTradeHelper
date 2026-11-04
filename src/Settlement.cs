using System;
using System.Collections.Generic;
using UnityEngine;

namespace TradeHelper
{
    // Background market catch-up for the stations the player picked. Uses the game's own bounded entry point
    // FWExchangeSpace.ReleasePendingSettlementBatch (same work as opening the station's exchange, but a few
    // days per frame instead of all at once).
    public class Settlement
    {
        readonly Queue<long> queue = new Queue<long>();
        readonly HashSet<long> queued = new HashSet<long>();
        public string lastError;
        public int daysDoneTotal;
        float nextKeepCheck;

        public int Pending => queue.Count;
        public bool IsQueued(long uid) => queued.Contains(uid);

        public void Enqueue(long uid)
        {
            if (queued.Add(uid)) queue.Enqueue(uid);
        }

        static FWExchangeSpace Find(FWGameData gd, long uid)
        {
            foreach (var kv in MarketScanner.EnumerateExchanges(gd))
                if (kv.Key.uid == uid) return kv.Key;
            return null;
        }

        public void Tick(Config cfg)
        {
            var gd = MarketScanner.Game;
            if (gd == null) return;
            if (Time.unscaledTime >= nextKeepCheck)
            {
                nextKeepCheck = Time.unscaledTime + 5f;
                if (cfg.keepFresh.Count > 0)
                    foreach (var kv in MarketScanner.EnumerateExchanges(gd))
                    {
                        var ex = kv.Key;
                        if (!cfg.keepFresh.Contains(ex.uid) || !ex.storeInitialized) continue;
                        bool pending = false;
                        try { pending = ex.hasPendingSettlement(); } catch { }
                        if (pending) Enqueue(ex.uid);
                    }
            }
            if (queue.Count == 0) return;
            long id = queue.Peek();
            var target = Find(gd, id);
            if (target == null || !target.storeInitialized) { Drop(); return; }
            try
            {
                bool pending = target.hasPendingSettlement();
                if (!pending) { Drop(); return; }
                int done = target.ReleasePendingSettlementBatch(Math.Max(1, cfg.daysPerFrame), Math.Max(1.0, cfg.msPerFrame), false);
                daysDoneTotal += done;
                if (!target.hasPendingSettlement()) Drop();
                else if (done == 0)
                {
                    // Nothing progressed this frame (e.g. the world clock is mid-advance); move it to the back.
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

        void Drop()
        {
            if (queue.Count == 0) return;
            queued.Remove(queue.Dequeue());
        }
    }
}
