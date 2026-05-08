using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

namespace autocad_final.UI
{
    public static class PluginMenu
    {
        private const string InitMenuText = "Initialize layers && blocks";
        private const string InitCommand = "AF_INITSTANDARDS ";

        private static bool _installed;
        private static readonly object Sync = new object();
        private static ContextMenuExtension _ctx;
        private static MenuItem _initItem;
        private static EventHandler _initClickHandler;

        public static void EnsureInstalled()
        {
            lock (Sync)
            {
                if (_installed) return;
                _installed = true;
            }

            try
            {
                if (_ctx != null) return;

                _ctx = new ContextMenuExtension { Title = "autocad-final" };
                _initItem = new MenuItem(InitMenuText);
                _initClickHandler = (_, __) =>
                {
                    try
                    {
                        var doc = Application.DocumentManager.MdiActiveDocument;
                        doc?.SendStringToExecute(InitCommand, true, false, false);
                    }
                    catch
                    {
                        // ignore
                    }
                };
                _initItem.Click += _initClickHandler;

                _ctx.MenuItems.Add(_initItem);
                Autodesk.AutoCAD.ApplicationServices.Application.AddDefaultContextMenuExtension(_ctx);
            }
            catch
            {
                // ignore (AutoCAD UI may not be ready in some hosts)
            }
        }

        public static void Uninstall()
        {
            lock (Sync)
            {
                _installed = false;
            }

            try
            {
                if (_initItem != null && _initClickHandler != null)
                    _initItem.Click -= _initClickHandler;
            }
            catch
            {
                // ignore unload errors
            }

            try
            {
                if (_ctx != null)
                    Autodesk.AutoCAD.ApplicationServices.Application.RemoveDefaultContextMenuExtension(_ctx);
            }
            catch
            {
                // ignore unload errors
            }

            _initClickHandler = null;
            _initItem = null;
            _ctx = null;
        }
    }
}

