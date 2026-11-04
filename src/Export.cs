using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace TradeHelper
{
    // Writes <persistentDataPath>/TradeHelper/latest.json so the data can also be read outside the game.
    public static class Export
    {
        static string S(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static string N(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        public static string Write(Snapshot snap, List<TradeIdea> ideas, List<SellIdea> sells, string dir)
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\":").Append(S(TradeHelperBehaviour.Version))
              .Append(",\"takenAt\":").Append(S(snap.takenAt.ToString("yyyy-MM-dd HH:mm:ss")))
              .Append(",\"credits\":").Append(snap.credits)
              .Append(",\"fleetGalaxyId\":").Append(S(snap.fleetGalaxyId))
              .Append(",\"markets\":[");
            bool first = true;
            foreach (var m in snap.markets)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"uid\":").Append(m.uid).Append(",\"name\":").Append(S(m.name))
                  .Append(",\"galaxyId\":").Append(S(m.galaxyId)).Append(",\"planet\":").Append(m.planet ? "true" : "false")
                  .Append(",\"initialized\":").Append(m.initialized ? "true" : "false")
                  .Append(",\"pendingDays\":").Append(m.pendingDays).Append(",\"tax\":").Append(N(m.tax)).Append(",\"fleetAu\":").Append(N(m.fleetAu))
                  .Append(",\"goods\":[");
                bool f2 = true;
                foreach (var q in m.goods)
                {
                    if (!f2) sb.Append(','); f2 = false;
                    sb.Append("{\"gid\":").Append(S(q.gid)).Append(",\"name\":").Append(S(q.name))
                      .Append(",\"type\":").Append(q.type).Append(",\"stock\":").Append(q.stock)
                      .Append(",\"buyable\":").Append(q.buyable ? "true" : "false")
                      .Append(",\"contraband\":").Append(q.contraband ? "true" : "false")
                      .Append(",\"exact\":").Append(q.exact ? "true" : "false")
                      .Append(",\"tax\":").Append(N(q.tax))
                      .Append(",\"buy1\":").Append(N(q.buy1))
                      .Append(",\"sell1\":").Append(N(q.sell1)).Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("],\"inventory\":[");
            first = true;
            foreach (var loc in snap.inventory)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":").Append(S(loc.name)).Append(",\"items\":{");
                bool f2 = true;
                foreach (var kv in loc.items)
                {
                    if (!f2) sb.Append(','); f2 = false;
                    sb.Append(S(kv.Key)).Append(':').Append(kv.Value);
                }
                sb.Append("}}");
            }
            sb.Append("],\"trades\":[");
            first = true;
            foreach (var t in ideas)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":").Append(S(t.name)).Append(",\"buy\":").Append(S(t.buy.market.name))
                  .Append(",\"sell\":").Append(S(t.sell.market.name)).Append(",\"qty\":").Append(t.qty)
                  .Append(",\"cost\":").Append(N(t.cost)).Append(",\"revenue\":").Append(N(t.revenue))
                  .Append(",\"profit\":").Append(N(t.Profit)).Append(",\"legAu\":").Append(N(t.legAu)).Append(",\"fleetAu\":").Append(N(t.fleetAu)).Append(",\"stale\":").Append(t.Stale ? "true" : "false").Append('}');
            }
            sb.Append("],\"sells\":[");
            first = true;
            foreach (var s in sells)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":").Append(S(s.name)).Append(",\"have\":").Append(s.have).Append(",\"qty\":").Append(s.qty)
                  .Append(",\"market\":").Append(S(s.market.market.name)).Append(",\"revenue\":").Append(N(s.revenue)).Append('}');
            }
            sb.Append("],\"errors\":[");
            for (int i = 0; i < snap.errors.Count; i++) { if (i > 0) sb.Append(','); sb.Append(S(snap.errors[i])); }
            sb.Append("]}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "latest.json");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
    }
}
