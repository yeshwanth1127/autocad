using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using autocad_final.Commands;
using autocad_final.Licensing;
using autocad_final.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final
{
    /// <summary>
    /// Plugin entry: loads the dockable sprinkler palette and registers SHOWSPRINKLERPALETTE.
    /// </summary>
    public class SprinklerPaletteExtensionApplication : IExtensionApplication
    {
        private static PaletteSet _paletteSet;
        private static readonly object DocumentHooksSync = new object();
        private static readonly HashSet<Document> HookedDocuments = new HashSet<Document>();
        private static bool _documentEventsAttached;

        public void Initialize()
        {
            AcApp.Idle -= OnIdleShowPaletteOnce;

            if (TrialExpiry.IsExpired())
            {
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    doc?.Editor?.WriteMessage("\n" + TrialExpiry.ExpiredUserMessage + "\n");
                }
                catch
                {
                    // ignore
                }
                return;
            }

            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor?.WriteMessage("\n[autocad-final] Loaded. Opening palette...\n");
            }
            catch
            {
                // ignore
            }

            AttachDocumentCommandEvents();
            try { PluginMenu.EnsureInstalled(); } catch { /* ignore */ }
            try { EnsurePaletteControlCreated(); } catch { /* palette optional */ }
            RequestShowPaletteOnIdle();
        }

        private static void AttachDocumentCommandEvents()
        {
            lock (DocumentHooksSync)
            {
                if (_documentEventsAttached)
                    return;
                _documentEventsAttached = true;
                var dm = AcApp.DocumentManager;
                dm.DocumentCreated += OnDocumentCreated;
                dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
                foreach (Document d in dm)
                    HookDocumentCommands(d);
            }
        }

        private static void DetachDocumentCommandEvents()
        {
            lock (DocumentHooksSync)
            {
                if (!_documentEventsAttached)
                    return;
                _documentEventsAttached = false;
                var dm = AcApp.DocumentManager;
                dm.DocumentCreated -= OnDocumentCreated;
                dm.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                Document[] copy;
                lock (HookedDocuments)
                {
                    copy = new Document[HookedDocuments.Count];
                    HookedDocuments.CopyTo(copy);
                    HookedDocuments.Clear();
                }

                foreach (var d in copy)
                    UnhookDocumentCommands(d);
            }
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) =>
            HookDocumentCommands(e?.Document);

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e) =>
            UnhookDocumentCommands(e?.Document);

        private static void HookDocumentCommands(Document doc)
        {
            if (doc == null) return;
            lock (HookedDocuments)
            {
                if (!HookedDocuments.Add(doc))
                    return;
            }

            doc.CommandEnded += OnDocumentCommandEnded;
            doc.CommandCancelled += OnDocumentCommandFinished;
        }

        private static void UnhookDocumentCommands(Document doc)
        {
            if (doc == null) return;
            lock (HookedDocuments)
            {
                if (!HookedDocuments.Remove(doc))
                    return;
            }

            doc.CommandEnded -= OnDocumentCommandEnded;
            doc.CommandCancelled -= OnDocumentCommandFinished;
        }

        private static void OnDocumentCommandEnded(object sender, EventArgs e)
        {
            OnDocumentCommandFinished(sender, e);

            if (IsCommand(e, "NETLOAD") && !TrialExpiry.IsExpired())
                RequestShowPaletteOnIdle();

            if (!TrialExpiry.IsExpired())
                ScheduleZoneShaftTableRefresh(sender as Document, TryGetGlobalCommandName(e));
        }

        private static void OnDocumentCommandFinished(object sender, EventArgs e)
        {
            try { PluginUiButtonSelection.ClearHighlight(); }
            catch { /* ignore */ }
        }

        private static string TryGetGlobalCommandName(EventArgs e)
        {
            try
            {
                var prop = e?.GetType().GetProperty("GlobalCommandName");
                var value = prop?.GetValue(e, null) as string;
                if (string.IsNullOrWhiteSpace(value))
                    return null;
                return value.Trim().TrimStart('.', '_');
            }
            catch
            {
                return null;
            }
        }

        private static bool IsCommand(EventArgs e, string commandName)
        {
            if (e == null || string.IsNullOrWhiteSpace(commandName))
                return false;

            var value = TryGetGlobalCommandName(e);
            return value != null && string.Equals(value, commandName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Zone workflows call into Publish during the command; we also refresh after CommandEnded on Idle so the
        /// grid catches late commits. Palette buttons use the same commands, so duplicate rows can occur — palette
        /// dedupes by clearing assignment-only duplicates is not implemented; rare duplicate row is acceptable.
        /// </summary>
        private static bool ShouldRefreshZoneShaftTableAfterCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd))
                return false;
            switch (cmd.ToUpperInvariant())
            {
                case "ZONECREATION1":
                case "ZONECREATION2":
                case "CREATEZONES2":
                case "FIXZONES":
                case "SPRINKLERFIXZONES":
                    return true;
                default:
                    return false;
            }
        }

        private static void ScheduleZoneShaftTableRefresh(Document doc, string cmd)
        {
            if (doc == null || !ShouldRefreshZoneShaftTableAfterCommand(cmd))
                return;

            EventHandler idleHandler = null;
            idleHandler = (_, __) =>
            {
                AcApp.Idle -= idleHandler;
                try
                {
                    AssignShaftToZoneCommand.PublishZoneShaftAssignmentScan(doc.Database);
                }
                catch
                {
                    // ignore
                }
            };
            AcApp.Idle += idleHandler;
        }

        /// <summary>Creates the palette (and <see cref="SprinklerPaletteControl"/> event subscriptions) without showing it.</summary>
        private static void EnsurePaletteControlCreated()
        {
            if (_paletteSet != null)
                return;
            _paletteSet = CreatePaletteSet();
            try { _paletteSet.Visible = false; } catch { /* ignore */ }
        }

        private static void RequestShowPaletteOnIdle()
        {
            AcApp.Idle -= OnIdleShowPaletteOnce;
            AcApp.Idle += OnIdleShowPaletteOnce;
        }

        private static void OnIdleShowPaletteOnce(object sender, EventArgs e)
        {
            AcApp.Idle -= OnIdleShowPaletteOnce;
            ShowPalette();
        }

        private static PaletteSet CreatePaletteSet()
        {
            var paletteSet = new PaletteSet("autocad-final — Sprinkler")
            {
                DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
                Style =
                    PaletteSetStyles.ShowAutoHideButton |
                    PaletteSetStyles.ShowCloseButton |
                    PaletteSetStyles.ShowPropertiesMenu,
                MinimumSize = new System.Drawing.Size(640, 480),
                Size = new System.Drawing.Size(720, 580)
            };
            paletteSet.Add("Building area", new SprinklerPaletteControl());
            return paletteSet;
        }

        private static void ShowPalette()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsurePaletteControlCreated();

                    _paletteSet.Visible = true;
                    try { _paletteSet.Activate(0); } catch { /* ignore */ }
                    return;
                }
                catch
                {
                    DisposePalette();
                    if (attempt > 0)
                        throw;
                }
            }
        }

        private static void DisposePalette()
        {
            var paletteSet = _paletteSet;
            _paletteSet = null;
            if (paletteSet == null)
                return;

            try
            {
                paletteSet.Visible = false;
            }
            catch
            {
                // ignore unload errors
            }

            try
            {
                paletteSet.Dispose();
            }
            catch
            {
                // ignore unload errors
            }
        }

        public void Terminate()
        {
            try
            {
                AcApp.Idle -= OnIdleShowPaletteOnce;
            }
            catch
            {
                // ignore
            }

            try
            {
                DetachDocumentCommandEvents();
            }
            catch
            {
                // ignore
            }

            try
            {
                PluginMenu.Uninstall();
            }
            catch
            {
                // ignore unload errors
            }

            try
            {
                DisposePalette();
            }
            catch
            {
                // ignore unload errors
            }
        }

        [CommandMethod("SHOWSPRINKLERPALETTE", CommandFlags.Modal)]
        public void ShowSprinklerPalette()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                if (!TrialGuard.EnsureActive(doc.Editor)) return;
                ShowPalette();
            }
            catch
            {
                // ignore
            }
        }
    }
}
