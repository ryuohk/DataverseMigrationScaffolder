using System.Text;
using System.Windows.Forms;

namespace DataverseMigrationScaffolder
{
    /// <summary>
    /// Tooltip helpers. A WinForms tooltip only wraps at the screen edge, so long explanations
    /// are word-wrapped here; explicit line breaks in the text are kept.
    /// </summary>
    internal static class Tips
    {
        private const int Width = 90;

        /// <summary>A tooltip that stays up long enough to read a paragraph.</summary>
        public static ToolTip New()
        {
            return new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 100 };
        }

        /// <summary>Gives every control (typically a field and its label) the same tooltip. Child parts
        /// such as a NumericUpDown's text box get it too; a tooltip isn't inherited.</summary>
        public static void Set(ToolTip tip, string text, params Control[] controls)
        {
            var wrapped = Wrap(text);
            foreach (var control in controls) Apply(tip, wrapped, control);
        }

        private static void Apply(ToolTip tip, string text, Control control)
        {
            tip.SetToolTip(control, text);
            foreach (Control child in control.Controls) Apply(tip, text, child);
        }

        public static string Wrap(string text)
        {
            var result = new StringBuilder();
            foreach (var paragraph in text.Split('\n'))
            {
                if (result.Length > 0) result.Append('\n');
                var line = 0;
                // Continuation lines of a "- " item are indented to line up with its text.
                var indent = paragraph.StartsWith("- ") ? "  " : "";
                foreach (var word in paragraph.Split(' '))
                {
                    if (line > 0 && line + 1 + word.Length > Width)
                    {
                        result.Append('\n').Append(indent);
                        line = indent.Length;
                    }
                    else if (line > 0)
                    {
                        result.Append(' ');
                        line++;
                    }
                    result.Append(word);
                    line += word.Length;
                }
            }
            return result.ToString();
        }
    }
}
