using System.Drawing;
using System.Windows.Forms;

namespace DataverseMigrationScaffolder
{
    /// <summary>
    /// Edits the list of field logical names excluded from dependency ranking,
    /// one per line (commas also accepted). Applies to all tables.
    /// </summary>
    public class ExclusionsDialog : Form
    {
        private readonly TextBox _txtBody;

        public string Result { get; private set; }

        public ExclusionsDialog(string current)
        {
            Text = "Dependency ranking exclusions";
            Size = new Size(460, 420);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;

            var lblHelp = new Label
            {
                Dock = DockStyle.Top,
                Height = 50,
                Text = "Field logical names to EXCLUDE from dependency ranking on all tables, " +
                       "one per line (e.g. ownerid, createdby). The columns are still " +
                       "generated; their lookups just don't influence tier ordering."
            };

            // XML settings serialization normalizes \r\n to \n, which a WinForms TextBox
            // renders as one concatenated line - normalize back to \r\n on load.
            var normalized = (current ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

            _txtBody = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,   // Enter inserts a newline instead of triggering OK
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                Text = normalized
            };

            var pnlButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 40
            };

            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            pnlButtons.Controls.Add(btnOk);
            pnlButtons.Controls.Add(btnCancel);

            Controls.Add(_txtBody);
            Controls.Add(pnlButtons);
            Controls.Add(lblHelp);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            btnOk.Click += (s, e) => Result = _txtBody.Text;
        }
    }
}
