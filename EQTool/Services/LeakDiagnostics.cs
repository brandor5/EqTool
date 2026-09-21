#if LINUX
using EQTool.UI;
using EQTool.ViewModels;
using EQToolShared.Extensions;
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace EQTool.Services
{
    // Temporary instrumentation for the memory growth seen under Proton. Fork-only; this is a
    // measurement tool, not a feature, and should be deleted once the leak is identified.
    //
    // Samples every collection that could plausibly grow without bound, alongside RSS, and
    // writes a CSV next to the executable. Whichever column tracks memory is the leak. If no
    // column tracks it, the growth is not in an app collection at all, which points at the
    // runtime rather than at this code - equally useful to know.
    //
    // Everything here is wrapped so a diagnostic can never take the app down, and nothing
    // blocks on the UI thread: counts are plain int reads, and a torn one does not matter at
    // this resolution.
    public class LeakDiagnostics : IDisposable
    {
        private readonly SpellWindowViewModel spellWindowViewModel;
        private readonly TriggerTimerManager triggerTimerManager;
        private readonly PlayerTrackerService playerTrackerService;
        private readonly DPSWindowViewModel dpsWindowViewModel;
        private readonly LogEvents logEvents;
        private System.Timers.Timer timer;
        private string path;
        private DateTime started;

        public LeakDiagnostics(
            SpellWindowViewModel spellWindowViewModel,
            TriggerTimerManager triggerTimerManager,
            PlayerTrackerService playerTrackerService,
            DPSWindowViewModel dpsWindowViewModel,
            LogEvents logEvents)
        {
            this.spellWindowViewModel = spellWindowViewModel;
            this.triggerTimerManager = triggerTimerManager;
            this.playerTrackerService = playerTrackerService;
            this.dpsWindowViewModel = dpsWindowViewModel;
            this.logEvents = logEvents;
        }

        public void Start()
        {
            try
            {
                started = DateTime.Now;
                path = Paths.InExecutableDirectory("leak-diagnostics.csv");
                File.WriteAllText(path, string.Join(",", new[]
                {
                    "iso_time", "elapsed_s", "build",
                    "rss_mb", "gc_heap_mb", "gc0", "gc1", "gc2",
                    "spell_list", "active_timers", "active_counters",
                    "all_players", "players_in_zones", "dirty_players",
                    "dps_entities", "window_list", "log_event_handlers",
                    "overlay_chains", "overlay_bars", "overlay_messages",
                    "map_canvas_children", "map_players"
                }) + Environment.NewLine);

                timer = new System.Timers.Timer(30000);
                timer.Elapsed += (s, e) => Sample();
                timer.Enabled = true;
                Sample();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LeakDiagnostics failed to start: " + ex);
            }
        }

        // Resident set size straight from the kernel. Wine forwards the Windows working-set APIs
        // through its own bookkeeping, which does not match what /proc reports and what the
        // external watcher script is graphing; reading /proc directly keeps the two comparable.
        private static double RssMb()
        {
            try
            {
                foreach (var line in File.ReadAllLines("/proc/self/status"))
                {
                    if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1 && double.TryParse(parts[1], out var kb))
                        {
                            return kb / 1024.0;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        private static int Count(object owner, string fieldName)
        {
            try
            {
                if (owner == null)
                {
                    return -1;
                }
                var f = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f?.GetValue(owner) is ICollection c)
                {
                    return c.Count;
                }
            }
            catch { }
            return -1;
        }

        // Every `public event` compiles to a private delegate field. Summing their invocation
        // lists catches the classic subscribe-without-unsubscribe leak, where each new window
        // adds handlers that the old ones never gave back - and with 46 events on LogEvents,
        // reflection is the only sane way to count them.
        private static int EventHandlerCount(object owner)
        {
            var total = 0;
            try
            {
                var fields = owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var f in fields)
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(owner) is Delegate d)
                    {
                        total += d.GetInvocationList().Length;
                    }
                }
            }
            catch { }
            return total;
        }

        private static T FindWindow<T>() where T : class
        {
            try
            {
                return App.WindowList.ToList().OfType<T>().FirstOrDefault();
            }
            catch { }
            return null;
        }

        private void Sample()
        {
            try
            {
                var overlay = FindWindow<EventOverlay>();
                var map = FindWindow<MappingWindow>();
                object mapViewModel = null;
                try
                {
                    mapViewModel = map?.GetType()
                        .GetField("mapViewModel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(map);
                }
                catch { }

                // MapViewModel.Canvas is a private field, not a property, so look for both.
                var mapCanvasChildren = -1;
                try
                {
                    var canvasMember =
                        (object)mapViewModel?.GetType().GetField("Canvas", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mapViewModel)
                        ?? mapViewModel?.GetType().GetProperty("Canvas", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mapViewModel);
                    if (canvasMember is System.Windows.Controls.Canvas c)
                    {
                        mapCanvasChildren = c.Children.Count;
                    }
                }
                catch { }

                var row = new StringBuilder();
                _ = row.Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                    .Append((int)(DateTime.Now - started).TotalSeconds).Append(',')
                    .Append(App.BuildId).Append(',')
                    .Append(RssMb().ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                    .Append((GC.GetTotalMemory(false) / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                    .Append(GC.CollectionCount(0)).Append(',')
                    .Append(GC.CollectionCount(1)).Append(',')
                    .Append(GC.CollectionCount(2)).Append(',')
                    .Append(SpellListCount()).Append(',')
                    .Append(Count(triggerTimerManager, "activeTimers")).Append(',')
                    .Append(Count(triggerTimerManager, "activeCounters")).Append(',')
                    .Append(Count(playerTrackerService, "AllPlayers")).Append(',')
                    .Append(Count(playerTrackerService, "PlayersInZones")).Append(',')
                    .Append(Count(playerTrackerService, "DirtyPlayers")).Append(',')
                    .Append(DpsEntityCount()).Append(',')
                    .Append(WindowListCount()).Append(',')
                    .Append(EventHandlerCount(logEvents)).Append(',')
                    .Append(Count(overlay, "chainDatas")).Append(',')
                    .Append(Count(overlay, "timerBarDatas")).Append(',')
                    .Append(Count(overlay, "messageDatas")).Append(',')
                    .Append(mapCanvasChildren).Append(',')
                    .Append(Count(mapViewModel, "Players"));

                File.AppendAllText(path, row.ToString() + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LeakDiagnostics sample failed: " + ex);
            }
        }

        private int SpellListCount()
        {
            try { return spellWindowViewModel.SpellList.Count; } catch { return -1; }
        }

        private int DpsEntityCount()
        {
            try { return dpsWindowViewModel.EntityList.Count; } catch { return -1; }
        }

        private static int WindowListCount()
        {
            try { return App.WindowList.Count; } catch { return -1; }
        }

        public void Dispose()
        {
            try
            {
                timer?.Stop();
                timer?.Dispose();
                timer = null;
            }
            catch { }
        }
    }
}
#endif
