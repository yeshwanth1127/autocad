using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace autocad_final.UI
{
    /// <summary>
    /// Modal form that lets the user browse, read, and delete past chat sessions.
    /// Sessions are listed newest-first on the left; messages appear on the right.
    /// </summary>
    internal sealed class ChatHistoryForm : Form
    {
        // ── Theme (matches palette) ───────────────────────────────────────────
        private static readonly Color C_Bg        = Color.FromArgb(20,  22,  28);
        private static readonly Color C_BgCard    = Color.FromArgb(28,  31,  40);
        private static readonly Color C_BgInput   = Color.FromArgb(14,  16,  22);
        private static readonly Color C_BgSection = Color.FromArgb(24,  26,  34);
        private static readonly Color C_BgHeader  = Color.FromArgb(16,  18,  24);
        private static readonly Color C_Fire      = Color.FromArgb(255, 90,  48);
        private static readonly Color C_Blue      = Color.FromArgb(56,  159, 255);
        private static readonly Color C_Green     = Color.FromArgb(52,  195, 110);
        private static readonly Color C_Red       = Color.FromArgb(255, 70,  70);
        private static readonly Color C_TextPrim  = Color.FromArgb(215, 222, 236);
        private static readonly Color C_TextSub   = Color.FromArgb(148, 160, 180);
        private static readonly Color C_TextMuted = Color.FromArgb(80,  92,  112);
        private static readonly Color C_Border    = Color.FromArgb(40,  44,  58);
        private static readonly Color C_Sel       = Color.FromArgb(36,  56,  90);

        // ── Controls ─────────────────────────────────────────────────────────
        private readonly ListBox     _sessionList;
        private readonly RichTextBox _messagePane;
        private readonly Label       _lblSessionInfo;
        private readonly Button      _btnDelete;

        private readonly ChatStore _store;

        // ── Constructor ───────────────────────────────────────────────────────
        public ChatHistoryForm(ChatStore store)
        {
            _store = store;

            Text            = "Chat History";
            Size            = new Size(900, 620);
            MinimumSize     = new Size(700, 450);
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = C_Bg;
            ForeColor       = C_TextPrim;
            Font            = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;

            // ── Title bar panel ───────────────────────────────────────────────
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 44,
                BackColor = C_BgHeader,
                Padding   = new Padding(0)
            };
            header.Paint += (s, e) =>
            {
                using (var pen = new Pen(C_Fire, 2f))
                    e.Graphics.DrawLine(pen, 0, header.Height - 2, header.Width, header.Height - 2);
            };
            var lblTitle = new Label
            {
                Text      = "Chat History",
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = C_TextPrim,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(14, 11)
            };
            header.Controls.Add(lblTitle);

            // ── Body: split left (sessions) / right (messages) ────────────────
            var body = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 2,
                RowCount        = 1,
                BackColor       = C_Bg,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100f));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // ── Left: session list ────────────────────────────────────────────
            var leftPanel = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 1,
                RowCount        = 3,
                BackColor       = C_BgSection,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            var lblSessions = new Label
            {
                Text      = "SESSIONS",
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = C_Blue,
                BackColor = Color.FromArgb(18, 26, 42),
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(10, 0, 0, 0)
            };

            _sessionList = new ListBox
            {
                Dock            = DockStyle.Fill,
                BackColor       = C_BgInput,
                ForeColor       = C_TextPrim,
                BorderStyle     = BorderStyle.None,
                Font            = new Font("Segoe UI", 8.5f),
                ItemHeight      = 20,
                DrawMode        = DrawMode.OwnerDrawFixed,
                SelectionMode   = SelectionMode.One,
                ScrollAlwaysVisible = false,
            };
            _sessionList.DrawItem  += SessionList_DrawItem;
            _sessionList.SelectedIndexChanged += (s, e) => LoadSelectedSession();

            _btnDelete = new Button
            {
                Text                    = "Delete Session",
                Dock                    = DockStyle.Fill,
                FlatStyle               = FlatStyle.Flat,
                BackColor               = Color.FromArgb(48, 20, 20),
                ForeColor               = C_Red,
                Font                    = new Font("Segoe UI", 8.5f),
                Cursor                  = Cursors.Hand,
                UseVisualStyleBackColor = false,
                Margin                  = new Padding(8, 6, 8, 6)
            };
            _btnDelete.FlatAppearance.BorderColor = Color.FromArgb(80, 30, 30);
            _btnDelete.FlatAppearance.BorderSize  = 1;
            PluginUiButtonSelection.Register(_btnDelete, PluginButtonChrome.Danger);
            _btnDelete.Click += (s, e) =>
            {
                PluginUiButtonSelection.NotifyClicked(_btnDelete);
                try { DeleteSession(s, e); }
                finally { PluginUiButtonSelection.ClearHighlight(); }
            };

            leftPanel.Controls.Add(lblSessions, 0, 0);
            leftPanel.Controls.Add(_sessionList, 0, 1);
            leftPanel.Controls.Add(_btnDelete,   0, 2);

            // ── Right: message viewer ─────────────────────────────────────────
            var rightPanel = new TableLayoutPanel
            {
                Dock            = DockStyle.Fill,
                ColumnCount     = 1,
                RowCount        = 2,
                BackColor       = C_BgSection,
                Padding         = new Padding(0),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _lblSessionInfo = new Label
            {
                Text      = "Select a session to view its messages",
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = C_TextMuted,
                BackColor = Color.FromArgb(18, 26, 42),
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(10, 0, 0, 0)
            };

            var pnlMsg = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = C_BgSection,
                Padding   = new Padding(8)
            };
            _messagePane = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                ReadOnly    = true,
                BackColor   = C_BgInput,
                ForeColor   = C_TextPrim,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 9f),
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                WordWrap    = true
            };
            pnlMsg.Controls.Add(_messagePane);

            rightPanel.Controls.Add(_lblSessionInfo, 0, 0);
            rightPanel.Controls.Add(pnlMsg,          0, 1);

            body.Controls.Add(leftPanel,  0, 0);
            body.Controls.Add(rightPanel, 1, 0);

            Controls.Add(body);
            Controls.Add(header);   // last so it paints on top

            // ── Load sessions on open ─────────────────────────────────────────
            LoadSessions();
        }

        // ── Session list ──────────────────────────────────────────────────────
        private void LoadSessions()
        {
            _sessionList.Items.Clear();
            try
            {
                var sessions = _store.GetSessions();
                foreach (var s in sessions)
                    _sessionList.Items.Add(s);
            }
            catch (Exception ex)
            {
                _messagePane.Text = "Error loading sessions: " + ex.Message;
            }

            _btnDelete.Enabled = _sessionList.Items.Count > 0;
        }

        private void SessionList_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var item = _sessionList.Items[e.Index] as SessionRow;
            if (item == null) return;

            bool selected = (e.State & DrawItemState.Selected) != 0;
            e.Graphics.FillRectangle(
                new SolidBrush(selected ? C_Sel : (e.Index % 2 == 0 ? C_BgInput : Color.FromArgb(18, 21, 30))),
                e.Bounds);

            string datePart  = item.CreatedAt.ToString("MMM d, HH:mm");
            string titlePart = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title;
            if (titlePart.Length > 28) titlePart = titlePart.Substring(0, 28) + "…";

            using (var fDate  = new Font("Segoe UI", 7.5f))
            using (var fTitle = new Font("Segoe UI", 8.5f, selected ? FontStyle.Bold : FontStyle.Regular))
            {
                e.Graphics.DrawString(datePart,  fDate,  new SolidBrush(C_TextMuted), e.Bounds.X + 8, e.Bounds.Y + 1);
                e.Graphics.DrawString(titlePart, fTitle, new SolidBrush(selected ? Color.White : C_TextPrim),
                    e.Bounds.X + 8, e.Bounds.Y + 12);
            }
        }

        // ── Message viewer ────────────────────────────────────────────────────
        private void LoadSelectedSession()
        {
            var session = _sessionList.SelectedItem as SessionRow;
            if (session == null) return;

            _lblSessionInfo.Text = session.CreatedAt.ToString("dddd, MMMM d yyyy  HH:mm") +
                                   (string.IsNullOrWhiteSpace(session.Title) ? string.Empty : "  —  " + session.Title);

            _messagePane.Clear();

            try
            {
                var messages = _store.GetMessages(session.Id);
                if (messages.Count == 0)
                {
                    Append(_messagePane, "(no messages in this session)", C_TextMuted, new Font("Segoe UI", 8.5f, FontStyle.Italic));
                    return;
                }

                foreach (var msg in messages)
                    RenderMessage(msg);
            }
            catch (Exception ex)
            {
                Append(_messagePane, "Error loading messages: " + ex.Message, C_Red, new Font("Segoe UI", 8.5f));
            }
        }

        private void RenderMessage(MessageRow msg)
        {
            switch (msg.Role)
            {
                case "user":
                    Append(_messagePane,
                        "You  " + msg.CreatedAt.ToString("HH:mm") + "\n",
                        Color.FromArgb(120, 160, 220), new Font("Segoe UI", 8f, FontStyle.Bold));
                    Append(_messagePane, msg.Content + "\n\n", C_TextPrim, new Font("Segoe UI", 9f));
                    break;

                case "assistant":
                    Append(_messagePane,
                        "── AI Response ─────────────────────────────────────\n",
                        Color.FromArgb(44, 50, 66), new Font("Segoe UI", 7.5f));
                    ResponseFormatter.AppendMarkdown(_messagePane, msg.Content);
                    Append(_messagePane, "\n", C_TextPrim, new Font("Segoe UI", 9f));
                    break;

                case "tool_call":
                    Append(_messagePane,
                        "  ⚙  " + msg.Content + "\n",
                        Color.FromArgb(80, 130, 200), new Font("Segoe UI", 8.5f));
                    break;

                case "tool_result":
                    // Truncate long JSON results in the history view
                    string display = msg.Content != null && msg.Content.Length > 300
                        ? msg.Content.Substring(0, 300) + "…"
                        : msg.Content ?? string.Empty;
                    Append(_messagePane,
                        "  ✓  " + display + "\n",
                        Color.FromArgb(52, 145, 80), new Font("Segoe UI", 8f));
                    break;

                default:
                    Append(_messagePane, msg.Content + "\n", C_TextSub, new Font("Segoe UI", 8.5f));
                    break;
            }
        }

        // ── Delete ────────────────────────────────────────────────────────────
        private void DeleteSession(object sender, EventArgs e)
        {
            var session = _sessionList.SelectedItem as SessionRow;
            if (session == null) return;

            var result = MessageBox.Show(
                "Delete session from " + session.CreatedAt.ToString("MMM d, HH:mm") + "?\nThis cannot be undone.",
                "Delete Session", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            try
            {
                _store.DeleteSession(session.Id);
                _messagePane.Clear();
                _lblSessionInfo.Text = "Select a session to view its messages";
                LoadSessions();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Delete failed: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Helper ────────────────────────────────────────────────────────────
        private static void Append(RichTextBox box, string text, Color color, Font font)
        {
            box.SelectionStart  = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor  = color;
            box.SelectionFont   = font;
            box.AppendText(text);
        }
    }
}
