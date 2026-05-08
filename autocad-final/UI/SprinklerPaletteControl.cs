using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using autocad_final.AreaWorkflow;
using autocad_final.Commands;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace autocad_final.UI
{
    /// <summary>
    /// Hosts sprinkler commands inside a <see cref="Autodesk.AutoCAD.Windows.PaletteSet"/>.
    /// Dark professional theme with grouped sections: design tools above, results grid below.
    /// </summary>
    public class SprinklerPaletteControl : UserControl
    {
        // ── Theme colours ────────────────────────────────────────────────────────
        private static readonly Color C_Bg        = Color.FromArgb(20, 22, 28);
        private static readonly Color C_BgCard    = Color.FromArgb(28, 31, 40);
        private static readonly Color C_BgSection = Color.FromArgb(24, 26, 34);
        private static readonly Color C_BgInput   = Color.FromArgb(14, 16, 22);
        private static readonly Color C_BgHeader  = Color.FromArgb(16, 18, 24);
        private static readonly Color C_Fire      = Color.FromArgb(255, 90, 48);
        private static readonly Color C_Blue      = Color.FromArgb(56, 159, 255);
        private static readonly Color C_Green     = Color.FromArgb(52, 195, 110);
        private static readonly Color C_TextPrim  = Color.FromArgb(215, 222, 236);
        private static readonly Color C_TextSub   = Color.FromArgb(148, 160, 180);
        private static readonly Color C_TextMuted = Color.FromArgb(80, 92, 112);
        private static readonly Color C_Border    = Color.FromArgb(40, 44, 58);
        private static readonly Color C_BorderMid = Color.FromArgb(56, 62, 78);

        // ── Controls ─────────────────────────────────────────────────────────────
        private readonly DataGridView _resultsGrid;
        private int _rowNo = 1;

        // ── Constructor ───────────────────────────────────────────────────────────
        public SprinklerPaletteControl()
        {
            Dock      = DockStyle.Fill;
            BackColor = C_Bg;
            Padding   = new Padding(0);

            var root = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 1,
                RowCount        = 2,
                BackColor       = C_Bg,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // ════════════════════════════════════════════════════════════════════
            // HEADER
            // ════════════════════════════════════════════════════════════════════
            var headerPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = C_BgHeader,
                Padding   = new Padding(0)
            };
            headerPanel.Paint += (s, e) =>
            {
                var p = (Panel)s;
                using (var pen = new Pen(C_Fire, 2f))
                    e.Graphics.DrawLine(pen, 0, p.Height - 2, p.Width, p.Height - 2);
                using (var b = new SolidBrush(C_Fire))
                    e.Graphics.FillRectangle(b, 0, 10, 3, 28);
            };

            var lblTitle = new Label
            {
                Text      = "Fire Sprinkler Design",
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = C_TextPrim,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(18, 7)
            };
            var lblSub = new Label
            {
                Text      = "AutoCAD Design Engine",
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = C_TextMuted,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(19, 28)
            };

            headerPanel.Controls.AddRange(new Control[] { lblTitle, lblSub });

            // ════════════════════════════════════════════════════════════════════
            // BODY  — single column, full width
            // ════════════════════════════════════════════════════════════════════
            var body = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 1,
                RowCount        = 2,
                BackColor       = C_Bg,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // design tools
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // results table

            // ── Design tools ─────────────────────────────────────────────────────
            var designSection = new Panel
            {
                Dock         = DockStyle.Top,
                BackColor    = C_Bg,
                Padding      = new Padding(8, 8, 8, 4),
                AutoSize     = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            var lblDesignSection = MakeSectionLabel("DESIGN TOOLS");

            var designDefs = new (string Text, Action Action)[]
            {
                ("Initialize layers & blocks", InitializeStandardsPaletteAction.Run),
                ("Define floor area — points", RunPointsArea),
                ("Place sprinklers", PlaceSprinklersPaletteAction.Run),
                ("Create zones",                   ZoneCreation2PaletteAction.Run),
                ("Assign shaft to zone",       RunAssignShaftToZone),
                ("Route main pipe",            RunRouteMainPipe),
                ("Redesign from trunk",        RunRedesignFromTrunk),
                ("Route branch pipes",         RunRouteBranchPipes),
                ("Route branch pipe 2",        RunRouteBranchPipes2),
                ("Place reducers",             RunPlaceReducers),
                ("Reducers (connector route)", RunPlaceReducersConnectorFirst),
                ("Label main pipe",            RunLabelMainPipe),
                ("Delete all on layer",        RunDeleteAllOnLayer),
            };

            var designStack = new TableLayoutPanel
            {
                Dock            = DockStyle.Top,
                ColumnCount     = 1,
                RowCount        = designDefs.Length + 1,
                BackColor       = C_Bg,
                AutoSize        = true,
                AutoSizeMode    = AutoSizeMode.GrowAndShrink,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            };
            designStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            designStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            for (int ri = 0; ri < designDefs.Length; ri++)
                designStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            designStack.Controls.Add(lblDesignSection, 0, 0);

            for (int i = 0; i < designDefs.Length; i++)
            {
                var btn    = CreateDesignButton(designDefs[i].Text);
                var action = designDefs[i].Action;
                btn.Margin = new Padding(0, 0, 0, 2);
                btn.Click += (_, __) =>
                {
                    PluginUiButtonSelection.NotifyClicked(btn);
                    action();
                };
                designStack.Controls.Add(btn, 0, i + 1);
            }

            designSection.Controls.Add(designStack);

            // ── Results section ──────────────────────────────────────────────────
            var resultsSection = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 1,
                RowCount        = 2,
                BackColor       = C_Bg,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            };
            resultsSection.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            resultsSection.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var pnlResultsHdr = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 28, 20),
                Padding   = new Padding(0)
            };
            pnlResultsHdr.Paint += (s, e) =>
            {
                var p = (Panel)s;
                using (var pen = new Pen(C_Green, 2f))
                    e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
                using (var b = new SolidBrush(C_Green))
                    e.Graphics.FillRectangle(b, 0, 5, 3, 16);
            };
            var lblResultsTitle = new Label
            {
                Text      = "RESULTS TABLE",
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = C_Green,
                BackColor = Color.Transparent,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 0, 0, 2)
            };
            pnlResultsHdr.Controls.Add(lblResultsTitle);

            _resultsGrid = new DataGridView
            {
                Dock                        = DockStyle.Fill,
                AllowUserToAddRows          = false,
                AllowUserToDeleteRows       = false,
                RowHeadersVisible           = false,
                ReadOnly                    = false,
                AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect                 = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                ScrollBars                  = ScrollBars.Both,
                BackgroundColor             = C_BgInput,
                GridColor                   = C_Border,
                BorderStyle                 = BorderStyle.None,
                EnableHeadersVisualStyles   = false,
            };
            ApplyDataGridTheme(_resultsGrid);
            ConfigureResultsGrid();

            var pnlGrid = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = C_Bg,
                Padding   = new Padding(8, 0, 8, 8)
            };
            pnlGrid.Controls.Add(_resultsGrid);

            resultsSection.Controls.Add(pnlResultsHdr,  0, 0);
            resultsSection.Controls.Add(pnlGrid,        0, 1);

            body.Controls.Add(designSection,  0, 0);
            body.Controls.Add(resultsSection, 0, 1);

            ZoneAreaCommand.ZoneAreaCompleted += OnZoneAreaCompleted;
            AssignShaftToZoneCommand.ZoneAssignmentsUpdated += OnZoneAssignmentsUpdated;

            root.Controls.Add(headerPanel, 0, 0);
            root.Controls.Add(body,        0, 1);
            Controls.Add(root);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ZoneAreaCommand.ZoneAreaCompleted -= OnZoneAreaCompleted;
                AssignShaftToZoneCommand.ZoneAssignmentsUpdated -= OnZoneAssignmentsUpdated;
            }

            base.Dispose(disposing);
        }

        // ── Theme helpers ─────────────────────────────────────────────────────────
        private static Label MakeSectionLabel(string text) => new Label
        {
            Text      = text,
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = C_TextMuted,
            BackColor = Color.Transparent,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Padding   = new Padding(0, 0, 0, 2),
            Margin    = new Padding(0)
        };

        private Button CreateDesignButton(string text)
        {
            var btn = new Button
            {
                Text                    = text,
                Dock                    = DockStyle.Fill,
                FlatStyle               = FlatStyle.Flat,
                BackColor               = C_BgCard,
                ForeColor               = C_TextPrim,
                Font                    = new Font("Segoe UI", 8.5f),
                TextAlign               = ContentAlignment.MiddleLeft,
                Padding                 = new Padding(8, 0, 4, 0),
                Margin                  = new Padding(0, 0, 3, 3),
                Cursor                  = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            PluginUiButtonSelection.Register(btn, PluginButtonChrome.Primary);
            return btn;
        }

        private static Button MakeSmallButton(string text)
        {
            var btn = new Button
            {
                Text                    = text,
                Dock                    = DockStyle.Fill,
                FlatStyle               = FlatStyle.Flat,
                BackColor               = C_BgCard,
                ForeColor               = C_TextSub,
                Font                    = new Font("Segoe UI", 8.5f),
                Cursor                  = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            btn.FlatAppearance.BorderColor        = C_Border;
            btn.FlatAppearance.BorderSize         = 1;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 40, 54);
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(26, 30, 42);
            PluginUiButtonSelection.Register(btn, PluginButtonChrome.Secondary);
            return btn;
        }

        private static void ApplyDataGridTheme(DataGridView grid)
        {
            grid.DefaultCellStyle.BackColor          = C_BgCard;
            grid.DefaultCellStyle.ForeColor          = C_TextPrim;
            grid.DefaultCellStyle.SelectionBackColor = C_Blue;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font               = new Font("Segoe UI", 8.5f);

            grid.ColumnHeadersDefaultCellStyle.BackColor          = C_BgSection;
            grid.ColumnHeadersDefaultCellStyle.ForeColor          = C_TextSub;
            grid.ColumnHeadersDefaultCellStyle.Font               = new Font("Segoe UI", 8f, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = C_BgSection;

            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(24, 27, 36);
        }

        // ── Command runners ───────────────────────────────────────────────────────
        private void RunPolygonArea()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                if (PolygonAreaCommand.TryRun(doc, out PolygonMetrics metrics))
                {
                    AddResultRow(metrics);
                    EditorWritePolygonNetArea.Run(doc.Editor, metrics.Area);
                }
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunZoneArea()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                ZoneAreaCommand.Run(doc, ZoneAreaCommand.ZoningMode.EqualAreaStrips);
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunZoneTest()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                ZoneTestCommand.Run(doc);
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunFixZoneBoundary()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                FixZoneBoundaryCommand.Run(doc);
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunZoneImplementation2()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new ZoneAreaCommand().SprinklerZoneAreaImplementation2();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunAssignShaftToZone()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new AssignShaftToZoneCommand().AssignShaftToZone();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunRouteMainPipe()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new RouteMainPipeCommand().RouteMainPipe();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunSprinklerDesign()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Run directly so the command line does not echo the command name.
                        new SprinklerDesignCommand().SprinklerDesign();
                    }
                    catch (Exception ex)
                    {
                        PaletteCommandErrorUi.Show(ex, doc);
                    }
                }));
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunApplySprinklers()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new ApplySprinklersCommand().ApplySprinklers();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunCheckSprinklersAndFix()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new CheckSprinklersAndFixCommand().CheckSprinklersAndFix();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunFixOnSlant()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new FixOnSlantCommand().FixOnSlant();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunAttachBranches()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new AttachBranchesCommand().AttachBranches();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunRedesignFromTrunk()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new RedesignFromTrunkCommand().RedesignFromTrunk();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunRouteBranchPipes()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Run directly so the command line does not echo the command name.
                new AttachBranchesCommand().RouteBranchPipes();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }


        private void RunPlaceReducers()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new AttachBranchesCommand().PlaceReducers();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunPlaceReducersConnectorFirst()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new AttachBranchesCommand().PlaceReducersConnectorFirst();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }
        private void RunRouteBranchPipes2()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new AttachBranchesCommand().RouteBranchPipes2();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunLabelMainPipe()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new AttachBranchesCommand().LabelMainPipe();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void RunDeleteAllOnLayer()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                new DeleteAllOnLayerCommand().DeleteAllOnLayer();
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        private void OnZoneAreaCompleted(PolygonMetrics metrics)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnZoneAreaCompleted(metrics))); }
                catch (ObjectDisposedException) { }
                return;
            }
            AddResultRow(metrics);
        }

        private void OnZoneAssignmentsUpdated(PolygonMetrics metrics)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => OnZoneAssignmentsUpdated(metrics))); }
                catch (ObjectDisposedException) { }
                return;
            }
            AddResultRow(metrics);
        }

        private void RunPointsArea()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show("No active drawing. Open or create a drawing first.",
                    "autocad-final", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                if (PointsAreaCommand.TryRun(doc, out PolygonMetrics metrics))
                {
                    AddResultRow(metrics);
                    EditorWritePolygonNetArea.Run(doc.Editor, metrics.Area);
                }
            }
            catch (Exception ex)
            {
                PaletteCommandErrorUi.Show(ex, doc);
            }
        }

        // ── Results grid ──────────────────────────────────────────────────────────
        private void ConfigureResultsGrid()
        {
            _resultsGrid.Columns.Clear();

            var colNo = new DataGridViewTextBoxColumn
            {
                Name         = "No",
                HeaderText   = "#",
                ReadOnly     = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width        = 36,
                MinimumWidth = 32
            };
            _resultsGrid.Columns.Add(colNo);

            void AddFill(string name, string header, float weight, int minW,
                         bool readOnly = false, bool wrap = false)
            {
                var c = new DataGridViewTextBoxColumn
                {
                    Name         = name,
                    HeaderText   = header,
                    ReadOnly     = readOnly,
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    MinimumWidth = minW,
                    FillWeight   = weight
                };
                if (wrap) c.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
                _resultsGrid.Columns.Add(c);
            }

            AddFill("RoomType",   "Feature Type",     120f,  72);
            AddFill("Layer",      "Layer",              70f,  52);
            AddFill("Color",      "Color",              55f,  44);
            AddFill("Area",       "Area",               70f,  52, readOnly: true);
            AddFill("Perimeter",  "Perimeter",          75f,  56, readOnly: true);
            AddFill("ShaftCount", "No. of Shafts",      70f,  72, readOnly: true);
            AddFill("ShaftCoords","Shaft Coordinates", 110f,  80, readOnly: true);
            AddFill("ZonesList",  "Zone boundary",     130f,  88, readOnly: true, wrap: true);
            AddFill("ZoneShaftAssign", "Zone → Shaft",  110f,  86, readOnly: true, wrap: true);
            AddFill("Height",     "Height (in m²)",     65f,  72);

            _resultsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        }

        private void AddResultRow(PolygonMetrics metrics)
        {
            int idx = _resultsGrid.Rows.Add();
            var row = _resultsGrid.Rows[idx];
            row.Cells["No"].Value         = _rowNo++;
            row.Cells["RoomType"].Value   = metrics.RoomName ?? string.Empty;
            row.Cells["Layer"].Value      = metrics.Layer    ?? string.Empty;
            row.Cells["Color"].Value      = string.Empty;
            row.Cells["Area"].Value       = metrics.Area.ToString("F2");
            row.Cells["Perimeter"].Value  = metrics.Perimeter.ToString("F2");
            row.Cells["ShaftCount"].Value = metrics.ShaftCount.ToString();
            row.Cells["ShaftCoords"].Value= metrics.ShaftCoordinates ?? string.Empty;
            row.Cells["ZonesList"].Value  = FormatZonesList(metrics);
            row.Cells["ZoneShaftAssign"].Value = FormatZoneShaftAssignment(metrics);
            row.Cells["Height"].Value     = string.Empty;
        }

        private void ClearTableContents()
        {
            _resultsGrid.Rows.Clear();
            _rowNo = 1;
        }

        private void SubmitTable() { /* placeholder */ }

        private static string FormatZonesList(PolygonMetrics metrics)
        {
            if (metrics.ZoneTable == null || metrics.ZoneTable.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < metrics.ZoneTable.Count; i++)
            {
                var z = metrics.ZoneTable[i];
                if (i > 0) sb.Append("\r\n");
                sb.Append(string.IsNullOrWhiteSpace(z.Name) ? "Zone " + (i + 1) : z.Name);
            }
            return sb.ToString();
        }

        private static string FormatZoneShaftAssignment(PolygonMetrics metrics)
        {
            if (metrics?.ZoneTable == null || metrics.ZoneTable.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < metrics.ZoneTable.Count; i++)
            {
                var z = metrics.ZoneTable[i];
                if (i > 0) sb.Append("\r\n");

                sb.Append(string.IsNullOrWhiteSpace(z.Name) ? "Zone " + (i + 1) : z.Name);
                sb.Append(" → ");

                if (!string.IsNullOrEmpty(z.AssignedShaftName))
                    sb.Append(z.AssignedShaftName).Append(" (manual)");
                else if (!z.ZoneOwnerIndex.HasValue)
                    sb.Append("—");
                else if (z.ZoneOwnerIndex.Value < 0)
                    sb.Append("uncovered");
                else
                    sb.Append("Shaft ").Append((z.ZoneOwnerIndex.Value + 1).ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
