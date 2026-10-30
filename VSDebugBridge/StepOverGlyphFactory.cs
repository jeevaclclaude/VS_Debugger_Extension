using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace VSDebugBridge
{
    /// <summary>
    /// Tag type that triggers a gutter glyph for step-over-filtered lines.
    /// One instance per tagged line — carries no data because the tagger
    /// already did the file/line lookup before emitting the tag.
    /// </summary>
    internal class StepOverTag : IGlyphTag { }

    /// <summary>
    /// Produces the WPF visual shown in the indicator margin next to each
    /// line marked "step over for Claude". Appears at the same margin layer
    /// as VS breakpoints (red dots), but uses a teal circle with "SO" text
    /// so the user can tell them apart at a glance.
    /// </summary>
    internal class StepOverGlyphFactory : IGlyphFactory
    {
        public UIElement GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
        {
            if (!(tag is StepOverTag)) return null;

            // Outer container — the glyph margin gives each glyph a 16×16 slot.
            var grid = new Grid { Width = 16, Height = 16 };

            // Teal filled circle — distinct from the red breakpoint dot and the
            // yellow execution arrow. Color chosen to match the "Claude" brand
            // turquoise loosely, and to be legible on both light and dark themes.
            grid.Children.Add(new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.FromRgb(0, 188, 180)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            // "SO" label — short enough to fit legibly inside the 14px circle.
            grid.Children.Add(new TextBlock
            {
                Text = "SO",
                FontSize = 5.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

            return grid;
        }
    }

    /// <summary>
    /// MEF export that wires StepOverGlyphFactory into every code editor view.
    /// VS discovers this at startup via MEF composition and registers the factory
    /// for any content type derived from "code" (which covers C#, VB, C++, etc.).
    /// </summary>
    [Export(typeof(IGlyphFactoryProvider))]
    [Name("StepOverGlyph")]
    [Order(After = "VsTextMarker")]
    [ContentType("code")]
    [TagType(typeof(StepOverTag))]
    internal sealed class StepOverGlyphFactoryProvider : IGlyphFactoryProvider
    {
        public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin)
        {
            return new StepOverGlyphFactory();
        }
    }
}
