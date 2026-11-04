using System.Threading;

namespace TradeHelper
{
    // The game's lock order for markets (FWTradeSession.Execute; checked by FWTradeSession.RequireSessionBeforeArchive and
    // FWExchangeSpace.EnsureSettledForUse): the trade session (FWTradeSession.SyncRoot), then the save
    // (FWGameData.ArchiveSyncRoot), then a market's own locks. The game's worker threads (catch-up, natural production)
    // take them in that order while they work. Reading markets on the main thread without the first two can take a
    // market lock and then wait for the save lock that a worker holds while the worker waits for that market lock: both
    // threads stop for good (0.4.2 froze in play this way; the isolated stress test froze within minutes).
    // So the mod takes the two locks in the game's order before it touches game objects, and only tries: when a worker
    // holds them, that frame's work is skipped and tried again next frame, so the frame never waits.
    public static class GameLock
    {
        public static int Misses;

        public static bool TryEnter(FWGameData gd)
        {
            if (gd == null) return false;
            object archive = gd.ArchiveSyncRoot;
            if (!Monitor.TryEnter(FWTradeSession.SyncRoot)) { Misses++; return false; }
            if (archive != null && !Monitor.TryEnter(archive)) { Monitor.Exit(FWTradeSession.SyncRoot); Misses++; return false; }
            return true;
        }

        // Blocking, in the same order (tests only).
        public static void Enter(FWGameData gd)
        {
            Monitor.Enter(FWTradeSession.SyncRoot);
            var archive = gd?.ArchiveSyncRoot;
            if (archive != null) Monitor.Enter(archive);
        }

        public static void Exit(FWGameData gd)
        {
            var archive = gd?.ArchiveSyncRoot;
            if (archive != null) Monitor.Exit(archive);
            Monitor.Exit(FWTradeSession.SyncRoot);
        }
    }
}
