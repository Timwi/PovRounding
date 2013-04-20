using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using RT.Generexes;
using RT.Util;
using RT.Util.CommandLine;
using RT.Util.Consoles;

namespace PovRounding
{
    public enum CmdFontStyle
    {
        [Option("-b", "--bold"), DocumentationLiteral("Uses boldface.")]
        Bold = 1,
        [Option("-i", "--italics"), DocumentationLiteral("Uses italics.")]
        Italic = 2,

        //Underline = 4,
        //Strikeout = 8,
    }

    public sealed class CommandLine
    {
        [Option("-o", "--output"), IsMandatory, DocumentationLiteral("Specifies where to write the POV-ray source to.")]
        public string Filename;

        [Option("-n", "--name"), IsMandatory, DocumentationLiteral("Specifies what to call the POV-ray object.")]
        public string ObjectName;

        [Option("-d", "--depth"), DocumentationLiteral("Specifies the extrusion depth. (Default is 6.)")]
        public float ExtrusionDepth = 6;

        [Option("-r", "--radius"), DocumentationLiteral("Specifies the rounding radius. (Default is 1.)")]
        public float RoundingRadius = 1;

        [Option("-f", "--factor"), DocumentationLiteral("Specifies the Bézier factor. (Default is 0.76. A value of 0.55228475 (which is sqrt(2)**4//3) gives a rounding close to circular. A value near 0 produces a near-flat bevel.)")]
        public float RoundingFactor = 0.76f;

        [IsPositional, IsMandatory, DocumentationLiteral("Specifies how the source curve is generated.")]
        public Render RenderCommand;

        private static void PostBuildCheck(IPostBuildReporter rep)
        {
            CommandLineParser<CommandLine>.PostBuildStep(rep, null);
        }
    }

    [CommandGroup]
    public abstract class Render
    {
        public abstract GraphicsPath GetGraphicsPath();
    }

    [CommandName("text"), DocumentationLiteral("Specifies that the input curve is generated from a line of text.")]
    public sealed class RenderText : Render
    {
        [IsPositional, IsMandatory, DocumentationLiteral("Specifies the text to render.")]
        public string Text;

        [Option("-f", "--font"), IsMandatory, DocumentationLiteral("Specifies the name of the font to use.")]
        public string Font;

        [Option("-s", "--size"), DocumentationLiteral("Specifies the font size to use. (Default is 64.)")]
        public float FontSize = 64;

        [EnumOptions(EnumBehavior.MultipleValues)]
        public CmdFontStyle Style;

        public override GraphicsPath GetGraphicsPath()
        {
            var gp = new GraphicsPath();
            gp.AddString(Text, new FontFamily(Font), (int) Style, FontSize, new PointF(0, 0), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            return gp;
        }
    }

    [CommandName("polygon"), DocumentationLiteral("Specifies that the input curve is a polygon.")]
    public sealed class RenderPolygon : Render, ICommandLineValidatable
    {
        [IsPositional, IsMandatory, DocumentationLiteral(@"Specifies the polygon to render in the following format: """"(x1,y1),(x2,y2),...,(xn,yn)"""".")]
        public string Polygon;

        [Ignore]
        public PointF[] PolygonParsed;

        public override GraphicsPath GetGraphicsPath()
        {
            var gp = new GraphicsPath();
            gp.AddPolygon(PolygonParsed);
            return gp;
        }

        public ConsoleColoredString Validate()
        {
            var point = new Stringerex("(").Then(Stringerexes.Number).Then(",").ThenRaw(Stringerexes.Number, (x, y) => new PointF((float) x, (float) y)).Then(")");
            var listOfPoints = point.RepeatWithSeparatorGreedy(",");
            var result = listOfPoints.RawMatchExact(Polygon);
            if (result == null)
                return "The specified polygon does not conform to the expected syntax.";
            PolygonParsed = result.ToArray();
            return null;
        }
    }
}
