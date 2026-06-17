using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;
using ModUpdater.Core;

namespace ModUpdater.UI
{
    /// <summary>
    /// Adds a "Mod Updater" tab to the userspace Dash (like ResoniteModSettings). Because it lives
    /// inside the dash, it's cursor-usable in desktop mode as well as VR. Built by Harmony-patching
    /// UserspaceScreensManager so it regenerates whenever the dash is (re)built.
    /// </summary>
    public static class UpdaterScreen
    {
        private static RadiantDashScreen _screen;
        private static ScrollRect _scroll;
        private static readonly colorX DarkButton = new colorX(0.13f, 0.13f, 0.16f);

        [HarmonyPatch(typeof(UserspaceScreensManager))]
        internal static class Patch
        {
            [HarmonyPostfix]
            [HarmonyPatch("SetupDefaults")]
            public static void SetupDefaults(UserspaceScreensManager __instance) => TryBuild(__instance);

            [HarmonyPostfix]
            [HarmonyPatch("OnLoading")]
            public static void OnLoading(UserspaceScreensManager __instance) => TryBuild(__instance);

            // RadiantDashScreen.BuildBackground is protected; reverse-patch lets us call it so the
            // screen's content rect is set up to fill (and gets the standard dash background).
            [HarmonyReversePatch]
            [HarmonyPatch(typeof(RadiantDashScreen), "BuildBackground", new[] { typeof(UIBuilder), typeof(bool) })]
            public static void BuildScreenBackground(RadiantDashScreen instance, UIBuilder ui, bool nest = true)
                => throw new NotImplementedException("stub");
        }

        private static void TryBuild(UserspaceScreensManager mgr)
        {
            try
            {
                if (mgr.World != Userspace.UserspaceWorld) return;
                if (_screen != null && !_screen.IsRemoved) return;

                var dash = mgr.Slot.GetComponentInParents<RadiantDash>();
                if (dash == null) return;

                // Single word so the dash tab label fits on one line (like Home/Settings/etc.).
                _screen = dash.AttachScreen("Updater", RadiantUI_Constants.Hero.PURPLE,
                                            OfficialAssets.Graphics.Icons.Dash.Tools);
                _screen.Slot.OrderOffset = 256;       // after Settings, before Exit
                _screen.Slot.PersistentSelf = false;  // don't save into the dash

                BuildContent();
            }
            catch (Exception ex) { ResoniteMod.Error("dash screen build failed: " + ex); }
        }

        private static void BuildContent()
        {
            var ui = new UIBuilder(_screen.ScreenCanvas);
            RadiantUI_Constants.SetupDefaultStyle(ui);
            Patch.BuildScreenBackground(_screen, ui);     // fills the screen rect + standard background
            ui.NestInto(ui.Empty("Content"));

            // Header on top, scrollable list below. Each region gets its OWN UIBuilder so there is no
            // shared nest stack to corrupt (the shared one was the source of the null-Current crash).
            ui.SplitVertically(0.18f, out RectTransform header, out RectTransform content, 0f);
            BuildHeader(new UIBuilder(header));
            BuildList(new UIBuilder(content));
        }

        private static void BuildHeader(UIBuilder ui)
        {
            RadiantUI_Constants.SetupDefaultStyle(ui);
            ui.Style.ButtonColor = DarkButton;   // darker Check now / Auto-download buttons
            ui.VerticalLayout(6f, 6f, Alignment.TopCenter, forceExpandWidth: true, forceExpandHeight: false);

            int updated = ModUpdaterMod.LastResults.Count(m => m.Status == ModStatus.Updated || m.Status == ModStatus.Reverted);
            int outdated = ModUpdaterMod.LastResults.Count(m => m.Status == ModStatus.Outdated);

            ui.Style.MinHeight = ui.Style.PreferredHeight = 34f;
            ui.Text($"<b>Mod Updater</b>   {ModUpdaterMod.LastResults.Count} mods, {updated} staged, {outdated} outdated", bestFit: true);

            ui.Style.MinHeight = ui.Style.PreferredHeight = 24f;
            ui.Style.TextColor = colorX.Orange;
            ui.Text("<size=70%>Tip: set forked / locally-modified mods to Hold so they're never overwritten.</size>");
            ui.Style.TextColor = RadiantUI_Constants.TEXT_COLOR;

            ui.Style.MinHeight = ui.Style.PreferredHeight = 40f;
            ui.Button("Check now").LocalPressed += (b, d) => OnCheckNow();

            bool auto = ModUpdaterMod.Instance?.AutoDownloadEnabled ?? false;
            ui.Button(auto ? "Auto-download: ON" : "Auto-download: OFF")
              .LocalPressed += (b, d) => { ModUpdaterMod.Instance?.SetAutoDownload(!auto); Refresh(); };
        }

        private static void BuildList(UIBuilder ui)
        {
            RadiantUI_Constants.SetupDefaultStyle(ui);
            _scroll = ui.ScrollArea<Image>(Alignment.TopCenter, out _, out _);
            ui.VerticalLayout(4f, 4f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);

            var results = ModUpdaterMod.LastResults;
            ui.Style.MinHeight = ui.Style.PreferredHeight = 40f;
            if (results.Count == 0)
            {
                ui.Text("No check has run yet — press \"Check now\".");
                return;
            }

            // Each mod is a row box; build its horizontal content in its OWN builder (no shared nest stack).
            int i = 0;
            foreach (var info in results.OrderBy(SortRank).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                ui.Style.MinHeight = ui.Style.PreferredHeight = 46f;
                // Opaque alternating shading so the busy world behind the dash doesn't bleed through.
                var bg = (i++ % 2 == 0) ? new colorX(0.10f, 0.10f, 0.13f, 0.93f) : new colorX(0.15f, 0.15f, 0.19f, 0.93f);
                var rowBg = ui.Image(bg);
                BuildRow(new UIBuilder(rowBg.RectTransform), info);
            }
        }

        private static void BuildRow(UIBuilder ui, ModInfo info)
        {
            RadiantUI_Constants.SetupDefaultStyle(ui);
            ui.Style.ButtonColor = DarkButton;   // darker field / arrow buttons
            ui.HorizontalLayout(6f, 6f, Alignment.MiddleLeft);

            // Name (left, flexible, colored by status). Problem states show their reason in parentheses.
            ui.Style.FlexibleWidth = 1f;
            ui.Style.TextColor = StatusColor(info.Status);
            string note = ShowsNote(info.Status) && !string.IsNullOrEmpty(info.Detail) ? $" <size=70%>({info.Detail})</size>" : "";
            ui.Text($"<b>{info.Name}</b>{note}", alignment: Alignment.MiddleLeft);
            ui.Style.TextColor = RadiantUI_Constants.TEXT_COLOR;
            ui.Style.FlexibleWidth = -1f;

            // Version transition.
            ui.Style.MinWidth = 190f;
            ui.Text($"<size=85%>{info.InstalledVersion} → {info.LatestVersion ?? "?"}</size>", alignment: Alignment.MiddleCenter);

            // Notify-only (the loader itself): no version picker, no auto-stage — just point at GitHub.
            if (info.NotifyOnly)
            {
                ui.Style.MinWidth = 200f;
                bool hasUpdate = info.Status == ModStatus.Outdated;
                if (hasUpdate)
                    ui.Button("update on GitHub", (colorX?)new colorX(0.12f, 0.34f, 0.18f))
                      .LocalPressed += (b, d) => Notify.Toast($"Update MonkeyLoader from {info.Link}", colorX.Cyan);
                else
                {
                    var b = ui.Button("up to date", (colorX?)new colorX(0.10f, 0.10f, 0.12f));
                    b.Enabled = false;
                }
                return;
            }

            // Version field (click cycles forward) + arrows.
            ui.Style.MinWidth = 130f;
            string pre = info.PrereleaseTags != null && info.PrereleaseTags.Contains(info.Pin) ? " (pre)" : "";
            ui.Button($"{PinLabel(info.Pin)}{pre}").LocalPressed += (b, d) => CyclePin(info, +1);

            ui.Style.MinWidth = 44f;
            ui.Button("<").LocalPressed += (b, d) => CyclePin(info, -1);
            ui.Button(">").LocalPressed += (b, d) => CyclePin(info, +1);

            // Update / Downgrade — visible always, enabled only when there's a change to apply.
            ui.Style.MinWidth = 150f;
            bool canUpdate = info.Status == ModStatus.Outdated;
            string label = info.Status switch
            {
                ModStatus.Updated => "staged",
                ModStatus.Reverted => "staged",
                ModStatus.Outdated => info.IsDowngrade ? "Downgrade" : "Update now",
                _ => "Update now",
            };
            if (canUpdate)
            {
                // Accent so actionable updates stand out: orange for a downgrade, green for an update (dark).
                colorX accent = info.IsDowngrade ? new colorX(0.42f, 0.25f, 0.08f) : new colorX(0.12f, 0.34f, 0.18f);
                ui.Button(label, (colorX?)accent).LocalPressed += (b, d) => ModUpdaterMod.Instance?.UpdateOne(info);
            }
            else
            {
                var disabled = ui.Button(label, (colorX?)new colorX(0.10f, 0.10f, 0.12f));
                disabled.Enabled = false;   // truly non-clickable (not just greyed)
            }

            // Revert (only when a backup exists), compact.
            if (info.HasBackup)
            {
                ui.Style.MinWidth = 48f;
                ui.Button("↺").LocalPressed += (b, d) => { ModUpdaterMod.Instance?.Revert(info); Refresh(); };
            }
        }

        /// <summary>Rebuild the screen content in place (marshaled to the userspace thread by callers if needed).</summary>
        public static void Refresh()
        {
            try
            {
                if (_screen == null || _screen.IsRemoved) return;
                // Preserve the scroll position across the rebuild so changing a setting doesn't jump to top.
                float2? pos = _scroll != null && !_scroll.IsRemoved ? _scroll.NormalizedPosition.Value : (float2?)null;

                _screen.ScreenCanvas.Slot.DestroyChildren();
                BuildContent();   // assigns the new _scroll

                if (pos.HasValue && _scroll != null)
                {
                    var p = pos.Value;
                    _scroll.NormalizedPosition.Value = p;
                    // Re-apply after layout settles (content size isn't final on the same frame).
                    _screen.ScreenCanvas.World?.RunInUpdates(3, () => { if (_scroll != null && !_scroll.IsRemoved) _scroll.NormalizedPosition.Value = p; });
                }
            }
            catch (Exception ex) { ResoniteMod.Error("dash screen refresh failed: " + ex); }
        }

        private static void OnCheckNow()
        {
            Notify.Toast("ModUpdater: checking…", colorX.Cyan);
            Task.Run(async () =>
            {
                var res = await ModUpdaterMod.Instance.RunCheckAsync().ConfigureAwait(false);
                ModUpdaterMod.MarshalToUserspace(() => { Notify.Summary(res); Refresh(); });
            });
        }

        private static void CyclePin(ModInfo info, int dir)
        {
            var opts = VersionOptions(info);
            int idx = opts.FindIndex(o => string.Equals(o, info.Pin, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            idx = ((idx + dir) % opts.Count + opts.Count) % opts.Count;
            info.Pin = opts[idx];
            ModUpdaterMod.Instance?.SetPin(info.Name, info.Pin);
            UpdateEngine.EvaluateTarget(info);   // re-evaluate status/target for the new pin (no network)
            Refresh();
        }

        private static List<string> VersionOptions(ModInfo info)
        {
            var opts = new List<string> { Pins.Latest, Pins.Hold };
            if (info.AvailableTags != null)
                foreach (var t in info.AvailableTags)
                    if (!opts.Contains(t)) opts.Add(t);
            if (!string.IsNullOrWhiteSpace(info.Pin) && !opts.Contains(info.Pin)) opts.Add(info.Pin);
            return opts;
        }

        private static string PinLabel(string pin)
        {
            if (string.Equals(pin, Pins.Latest, StringComparison.OrdinalIgnoreCase)) return "Latest";
            if (string.Equals(pin, Pins.Hold, StringComparison.OrdinalIgnoreCase)) return "Hold";
            return string.IsNullOrEmpty(pin) ? "Latest" : pin;
        }

        private static int SortRank(ModInfo m) => m.Status switch
        {
            ModStatus.Updated => 0,
            ModStatus.Reverted => 0,
            ModStatus.Outdated => 1,
            ModStatus.Ambiguous => 2,
            ModStatus.ManualOnly => 2,
            ModStatus.Error => 3,
            ModStatus.Unknown => 4,
            ModStatus.NoSource => 5,
            ModStatus.Held => 6,
            ModStatus.Pinned => 7,
            _ => 8,
        };

        // Statuses whose Detail explains a problem worth showing next to the name.
        private static bool ShowsNote(ModStatus s) =>
            s is ModStatus.Error or ModStatus.NoSource or ModStatus.ManualOnly or ModStatus.Ambiguous or ModStatus.Unknown;

        private static colorX StatusColor(ModStatus s) => s switch
        {
            ModStatus.Updated => colorX.Green,
            ModStatus.Reverted => colorX.Orange,
            ModStatus.Outdated => colorX.Yellow,
            ModStatus.UpToDate => colorX.Cyan,
            ModStatus.Pinned => colorX.Azure,
            ModStatus.Held => colorX.Gray,
            ModStatus.Error => colorX.Red,
            ModStatus.Ambiguous => colorX.Orange,
            ModStatus.ManualOnly => colorX.Orange,
            ModStatus.Skipped => colorX.Gray,
            ModStatus.NoSource => colorX.Gray,
            _ => colorX.LightGray,
        };
    }
}
