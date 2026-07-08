using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace DataverseMigrationScaffolder
{
    /// <summary>
    /// Column header cell with a checkbox that checks/unchecks every currently
    /// visible (i.e. filtered) row. Clicking anywhere on the header toggles it.
    /// </summary>
    public class CheckBoxHeaderCell : DataGridViewColumnHeaderCell
    {
        public bool Checked { get; private set; }

        /// <summary>Raised after the header checkbox is toggled by the user; argument is the new state.</summary>
        public event EventHandler<bool> CheckedChanged;

        /// <summary>Sets the displayed state WITHOUT raising CheckedChanged (used to mirror row state).</summary>
        public void SetState(bool value)
        {
            if (Checked == value) return;
            Checked = value;
            if (DataGridView != null) DataGridView.InvalidateCell(this);
        }

        protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
            DataGridViewElementStates dataGridViewElementState, object value, object formattedValue, string errorText,
            DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle,
            DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, dataGridViewElementState, value, formattedValue,
                errorText, cellStyle, advancedBorderStyle, paintParts);

            var glyph = CheckBoxRenderer.GetGlyphSize(graphics, CheckBoxState.UncheckedNormal);
            var location = new Point(
                cellBounds.X + 4,
                cellBounds.Y + (cellBounds.Height - glyph.Height) / 2);

            CheckBoxRenderer.DrawCheckBox(graphics, location,
                Checked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);
        }

        protected override void OnMouseClick(DataGridViewCellMouseEventArgs e)
        {
            Checked = !Checked;
            if (DataGridView != null) DataGridView.InvalidateCell(this);
            var handler = CheckedChanged;
            if (handler != null) handler(this, Checked);
            base.OnMouseClick(e);
        }
    }
}
