using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace TradeHelper
{
    // Performance log: what the mod costs per frame (scan, background catch-up, panel), the game's frame time, and a
    // breakdown of every scan. Written as plain text to <data folder>/perf.log every 30 real seconds.
    public static class Perf
    {
        public enum Cat { Scan, Settle, Ui, Gui, Plans, Count }
        static readonly long[] frameTicks = new long[(int)Cat.Count];
        static readonly double[] sumMs = new double[(int)Cat.Count];
        static readonly double[] maxMs = new double[(int)Cat.Count];
        static int frames;
        static double frameSumMs, frameMaxMs, modFrameMaxMs;
        static float windowStart = -1;
        static int gc0Start = -1, gcMaxStart = -1;
        public static string LastSummary = "";
        public static string LastScan = "";
        public static int settledDays;
        public static double settleMsTotal;
        public static long cacheHits, cacheMisses, cacheChecks, cacheMismatch;
        public static string path;

        // Garbage: what the whole game allocates per window and how much of it the mod's own code allocated, plus the worst
        // frame right after a garbage collection versus the worst other frame (tells GC pauses from other stalls).
        static long lastMem = -1; static int lastGc = -1;
        static long allocAll, allocMod; static int allocModUnknown;
        static int gcFrames; static double gcFrameMax, otherFrameMax; static bool afterGc;
        public struct Alloc { public long mem; public int gc; }
        public static Alloc AllocBegin() => new Alloc { mem = GC.GetTotalMemory(false), gc = GC.CollectionCount(0) };
        public static void AllocEnd(Alloc a)
        {
            if (GC.CollectionCount(0) != a.gc) { allocModUnknown++; return; }
            long d = GC.GetTotalMemory(false) - a.mem;
            if (d > 0) allocMod += d;
        }

        public static long Now => Stopwatch.GetTimestamp();
        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public static void Add(Cat c, long startTicks) { frameTicks[(int)c] += Stopwatch.GetTimestamp() - startTicks; }

        // Called once per frame from Update.
        public static void EndFrame(Config cfg)
        {
            double modMs = 0;
            for (int i = 0; i < frameTicks.Length; i++)
            {
                double ms = Ms(frameTicks[i]);
                sumMs[i] += ms; if (ms > maxMs[i]) maxMs[i] = ms;
                modMs += ms;
                frameTicks[i] = 0;
            }
            if (modMs > modFrameMaxMs) modFrameMaxMs = modMs;
            double dt = Time.unscaledDeltaTime * 1000.0;
            frameSumMs += dt; if (dt > frameMaxMs) frameMaxMs = dt;
            frames++;
            long mem = GC.GetTotalMemory(false); int gc = GC.CollectionCount(0);
            if (lastGc >= 0)
            {
                // A collection shows up in this frame's time or, if it ran after Update, in the next one.
                if (gc != lastGc) { gcFrames++; if (dt > gcFrameMax) gcFrameMax = dt; afterGc = true; }
                else
                {
                    if (mem > lastMem) allocAll += mem - lastMem;
                    if (afterGc) { if (dt > gcFrameMax) gcFrameMax = dt; afterGc = false; }
                    else if (dt > otherFrameMax) otherFrameMax = dt;
                }
            }
            lastMem = mem; lastGc = gc;
            if (windowStart < 0) { windowStart = Time.unscaledTime; gc0Start = GC.CollectionCount(0); gcMaxStart = GC.CollectionCount(GC.MaxGeneration); }
            if (Time.unscaledTime - windowStart >= 30f) Flush(cfg);
        }

        static void Flush(Config cfg)
        {
            if (frames == 0) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  ").Append(frames).Append(" 帧  平均帧 ")
              .Append((frameSumMs / frames).ToString("0.0")).Append("ms（")
              .Append((1000.0 * frames / Math.Max(1, frameSumMs)).ToString("0")).Append(" fps） 最慢帧 ").Append(frameMaxMs.ToString("0")).Append("ms")
              .Append(" | 模组每帧平均 ");
            double modAvg = 0;
            for (int i = 0; i < (int)Cat.Count; i++) modAvg += sumMs[i] / frames;
            sb.Append(modAvg.ToString("0.00")).Append("ms 最多 ").Append(modFrameMaxMs.ToString("0.0")).Append("ms");
            string[] names = { "扫描", "补算", "面板", "热键", "计划" };
            for (int i = 0; i < (int)Cat.Count; i++)
                sb.Append(" | ").Append(names[i]).Append(" 平均 ").Append((sumMs[i] / frames).ToString("0.00")).Append(" 最多 ").Append(maxMs[i].ToString("0.0"));
            sb.Append(" | 补算 ").Append(settledDays).Append(" 天 共 ").Append(settleMsTotal.ToString("0")).Append("ms");
            sb.Append(" | 缓存 命中 ").Append(cacheHits).Append(" 未命中 ").Append(cacheMisses).Append(" 抽查 ").Append(cacheChecks).Append(" 不一致 ").Append(cacheMismatch);
            int g0 = GC.CollectionCount(0), gm = GC.CollectionCount(GC.MaxGeneration);
            sb.Append(" | 垃圾回收 ").Append(gc0Start < 0 ? 0 : g0 - gc0Start).Append(" 次（完整 ").Append(gcMaxStart < 0 ? 0 : gm - gcMaxStart).Append(" 次）");
            gc0Start = g0; gcMaxStart = gm;
            sb.Append("，回收那帧最慢 ").Append(gcFrameMax.ToString("0")).Append("ms，其他帧最慢 ").Append(otherFrameMax.ToString("0")).Append("ms");
            sb.Append(" | 新分配 全部 ").Append((allocAll / 1048576.0).ToString("0")).Append("MB 其中模组 ").Append((allocMod / 1048576.0).ToString("0.0")).Append("MB");
            if (allocModUnknown > 0) sb.Append("（另有 ").Append(allocModUnknown).Append(" 帧赶上回收没算）");
            allocAll = allocMod = 0; allocModUnknown = 0; gcFrames = 0; gcFrameMax = otherFrameMax = 0;
            sb.Append(" | 内存 ").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("0")).Append("MB");
            LastSummary = sb.ToString();
            Write(LastSummary, cfg);
            Array.Clear(sumMs, 0, sumMs.Length); Array.Clear(maxMs, 0, maxMs.Length);
            frames = 0; frameSumMs = frameMaxMs = modFrameMaxMs = 0; settledDays = 0; settleMsTotal = 0;
            cacheHits = cacheMisses = cacheChecks = cacheMismatch = 0;
            windowStart = Time.unscaledTime;
        }

        public static void Write(string line, Config cfg)
        {
            try
            {
                var dir = cfg.DataDir;
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "perf.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                {
                    var old = Path.Combine(dir, "perf.old.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[TradeHelper] perf log: " + e.Message); }
        }
    }
}
