using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Steamworks;

namespace WorkshopUpload
{
    // Puts a mod folder on the Steam Workshop through the Steam client the user is already logged in to (no password).
    // The game has upload code (UGCManager.createItem/updateItem) but no button for it; this does the same calls with the
    // game's own Steamworks.NET and steam_api64.dll. tools/workshop.py builds it and passes the arguments.
    //   WorkshopUpload check  --app <id> [--id <published id>]
    //   WorkshopUpload upload --app <id> (--id <published id> | --create) --content <dir> --preview <image>
    //                         --title <text> --desc-file <file> [--lang schinese] [--tags a,b]
    //                         [--visibility private|friends|unlisted|public] [--note <text>]
    // Prints PUBLISHED_ID=<id> after creating an item and RESULT=OK <url> when the upload went through.
    static class Program
    {
        static bool inited;

        static int Main(string[] argv)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (argv.Length == 0) { Console.WriteLine("usage: check|upload --app <id> ..."); return 64; }
            try { return Run(argv[0], Parse(argv)); }
            catch (Exception e) { Console.WriteLine("ERROR " + e.Message); return 1; }
            finally { if (inited) SteamAPI.Shutdown(); }
        }

        static Dictionary<string, string> Parse(string[] argv)
        {
            var o = new Dictionary<string, string>();
            for (int i = 1; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("--")) throw new ArgumentException("unexpected argument " + argv[i]);
                string k = argv[i].Substring(2);
                bool flag = i + 1 >= argv.Length || argv[i + 1].StartsWith("--");
                o[k] = flag ? "" : argv[++i];
            }
            return o;
        }

        static string Need(Dictionary<string, string> o, string k)
        {
            if (!o.TryGetValue(k, out var v) || v == "") throw new ArgumentException("missing --" + k);
            return v;
        }

        static int Run(string cmd, Dictionary<string, string> o)
        {
            uint app = uint.Parse(Need(o, "app"));
            var appId = new AppId_t(app);
            // Steam reads the app id from steam_appid.txt next to the exe (or these variables)
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt"), app.ToString());
            Environment.SetEnvironmentVariable("SteamAppId", app.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", app.ToString());
            var r = SteamAPI.InitEx(out string msg);
            if (r != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                Console.WriteLine("ERROR steam init " + r + ": " + msg + " (Steam must be running and logged in)");
                return 2;
            }
            inited = true;
            Console.WriteLine("steam user: " + SteamFriends.GetPersonaName() + " (" + SteamUser.GetSteamID() + "), owns app "
                              + app + ": " + SteamApps.BIsSubscribedApp(appId));

            if (cmd == "check")
            {
                if (o.TryGetValue("id", out var s) && s != "") Show(new PublishedFileId_t(ulong.Parse(s)));
                Console.WriteLine("RESULT=OK");
                return 0;
            }
            if (cmd != "upload") throw new ArgumentException("unknown command " + cmd);

            string content = Path.GetFullPath(Need(o, "content"));
            if (!File.Exists(Path.Combine(content, "Manifest", "manifest.xml")))
                throw new ArgumentException("no Manifest/manifest.xml in " + content + " (the game skips such items)");
            string preview = Path.GetFullPath(Need(o, "preview"));
            if (!File.Exists(preview)) throw new ArgumentException("no preview " + preview);
            if (new FileInfo(preview).Length >= 1024 * 1024) throw new ArgumentException("preview must be under 1 MB");
            string title = Need(o, "title");
            string desc = File.ReadAllText(Need(o, "desc-file"));
            if (title.Length > 128) throw new ArgumentException("title over 128 characters");
            if (desc.Length > 8000) throw new ArgumentException("description over 8000 characters");

            PublishedFileId_t id;
            if (o.ContainsKey("create"))
            {
                if (o.ContainsKey("id")) throw new ArgumentException("--create and --id together");
                var c = Wait<CreateItemResult_t>(SteamUGC.CreateItem(appId, EWorkshopFileType.k_EWorkshopFileTypeCommunity), 120, null);
                if (c.m_eResult != EResult.k_EResultOK) throw new Exception("create failed: " + c.m_eResult);
                id = c.m_nPublishedFileId;
                Console.WriteLine("PUBLISHED_ID=" + id);
                if (c.m_bUserNeedsToAcceptWorkshopLegalAgreement) Legal();
            }
            else id = new PublishedFileId_t(ulong.Parse(Need(o, "id")));

            var h = SteamUGC.StartItemUpdate(appId, id);
            if (h == UGCUpdateHandle_t.Invalid) throw new Exception("StartItemUpdate failed");
            Check(SteamUGC.SetItemUpdateLanguage(h, o.TryGetValue("lang", out var lang) && lang != "" ? lang : "schinese"), "language");
            Check(SteamUGC.SetItemTitle(h, title), "title");
            Check(SteamUGC.SetItemDescription(h, desc), "description");
            if (o.TryGetValue("tags", out var tags) && tags != "")
                Check(SteamUGC.SetItemTags(h, new List<string>(tags.Split(','))), "tags");
            Check(SteamUGC.SetItemContent(h, content), "content");
            Check(SteamUGC.SetItemPreview(h, preview), "preview");
            if (o.TryGetValue("visibility", out var vis) && vis != "") Check(SteamUGC.SetItemVisibility(h, Visibility(vis)), "visibility");

            o.TryGetValue("note", out var note);
            DateTime nextShow = DateTime.MinValue;
            var res = Wait<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(h, string.IsNullOrEmpty(note) ? null : note), 1800, () =>
            {
                if (DateTime.Now < nextShow) return;
                nextShow = DateTime.Now.AddSeconds(2);
                var st = SteamUGC.GetItemUpdateProgress(h, out ulong done, out ulong total);
                Console.WriteLine("  " + st + " " + done + "/" + total);
            });
            if (res.m_bUserNeedsToAcceptWorkshopLegalAgreement) Legal();
            if (res.m_eResult != EResult.k_EResultOK) throw new Exception("upload failed: " + res.m_eResult);
            Show(id);
            Console.WriteLine("RESULT=OK https://steamcommunity.com/sharedfiles/filedetails/?id=" + id);
            return 0;
        }

        static void Legal()
        {
            Console.WriteLine("LEGAL: the Workshop agreement is not accepted yet; the item stays hidden until it is: "
                              + "https://steamcommunity.com/sharedfiles/workshoplegalagreement");
        }

        static ERemoteStoragePublishedFileVisibility Visibility(string v)
        {
            switch (v)
            {
                case "public": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic;
                case "friends": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly;
                case "unlisted": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted;
                case "private": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate;
                default: throw new ArgumentException("visibility must be private, friends, unlisted or public");
            }
        }

        static void Check(bool ok, string what) { if (!ok) throw new Exception("setting the " + what + " failed"); }

        // what Steam now has for the item (owner, title, visibility, last update)
        static void Show(PublishedFileId_t id)
        {
            var q = SteamUGC.CreateQueryUGCDetailsRequest(new[] { id }, 1);
            try
            {
                var done = Wait<SteamUGCQueryCompleted_t>(SteamUGC.SendQueryUGCRequest(q), 60, null);
                if (done.m_eResult != EResult.k_EResultOK || done.m_unNumResultsReturned == 0 || !SteamUGC.GetQueryUGCResult(q, 0, out var d))
                {
                    Console.WriteLine("item " + id + ": no details (" + done.m_eResult + ")");
                    return;
                }
                var updated = DateTimeOffset.FromUnixTimeSeconds(d.m_rtimeUpdated).LocalDateTime;
                Console.WriteLine("item " + id + ": " + d.m_eResult + ", title \"" + d.m_rgchTitle + "\", " + d.m_eVisibility
                                  + ", owner " + d.m_ulSteamIDOwner + ", size " + d.m_nFileSize + ", updated " + updated);
            }
            finally { SteamUGC.ReleaseQueryUGCRequest(q); }
        }

        static T Wait<T>(SteamAPICall_t call, int seconds, Action tick) where T : struct
        {
            if (call == SteamAPICall_t.Invalid) throw new Exception(typeof(T).Name + ": the call was not started");
            T result = default; bool done = false, io = false;
            var cr = CallResult<T>.Create((d, failed) => { result = d; io = failed; done = true; });
            cr.Set(call);
            var end = DateTime.Now.AddSeconds(seconds);
            while (!done)
            {
                SteamAPI.RunCallbacks();
                tick?.Invoke();
                if (DateTime.Now > end) throw new TimeoutException(typeof(T).Name + " took over " + seconds + " s");
                Thread.Sleep(100);
            }
            cr.Dispose();
            if (io) throw new Exception(typeof(T).Name + ": Steam I/O failure");
            return result;
        }
    }
}
