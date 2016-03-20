using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RT.Generexes;
using RT.Util;
using RT.Util.CommandLine;
using RT.Util.Consoles;
using RT.Util.ExtensionMethods;

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

    [CommandLine]
    public abstract class CommandLine
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

        [Option("-e", "--extra"), DocumentationLiteral("Specifies a text file containing extra POV-ray code to be added to the declaration of every single object generated.")]
        public string ExtraCodeFile = null;

        [Option("-sf", "--skip-front"), DocumentationRhoML("Do not generate the rounding at the front of the object. (The resulting object will look disconnected.)")]
        public bool SkipFront = false;
        [Option("-sb", "--skip-back"), DocumentationRhoML("Do not generate the rounding at the back of the object. (The resulting object will look disconnected.)")]
        public bool SkipBack = false;

        [Option("-s", "--smoothness"), DocumentationRhoML("Specifies the smoothness of the curve. For normal purposes, 4 (the default) is enough. If you are taking a close-up shot of the edge of the object, increase to 6.")]
        public int Smoothness = 4;

        private static void PostBuildCheck(IPostBuildReporter rep)
        {
            CommandLineParser.PostBuildStep<CommandLine>(rep, null);
        }

        public abstract GraphicsPath GetGraphicsPath();
    }

    [CommandName("text"), DocumentationLiteral("Specifies that the input curve is generated from a line of text.")]
    public sealed class RenderText : CommandLine
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
    public sealed class RenderPolygon : CommandLine, ICommandLineValidatable
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

    [CommandName("svgpath"), DocumentationRhoML("Specifies that the input curve is a path specified in SVG syntax. (Currently the only commands supported are M, L, C and Z/z.)")]
    public sealed class RenderSvgPath : CommandLine, ICommandLineValidatable
    {
        [Option("-p"), DocumentationRhoML(@"Specifies the curve in SVG path syntax. Must be present unless {option}-f{} and {option}-i{} are used.")]
        public string SvgPath;

        [Option("-f"), DocumentationRhoML(@"Specifies an SVG file to read path data from. {option}-i{} must be specified as well.")]
        public string Filename;

        [Option("-i"), DocumentationRhoML(@"Specifies the ID of the path element in the SVG file identified by {option}-f{}, which must be specified as well.")]
        public string Id;

        [Option("-w"), DocumentationRhoML(@"Specifies to use Winding fill mode (default is Alternate).")]
        public bool UseWindingFill;

        [Ignore]
        public GraphicsPath ParsedPath;

        public override GraphicsPath GetGraphicsPath()
        {
            return ParsedPath;
        }

        public ConsoleColoredString Validate()
        {
            if (SvgPath == null && (Filename == null || Id == null))
                return CommandLineParser.Colorize(RhoML.Parse("If {option}-p{} is not used, {option}-f{} and {option}-i{} must both be present."));
            if (SvgPath != null && (Filename != null || Id != null))
                return CommandLineParser.Colorize(RhoML.Parse("If {option}-p{} is used, neither {option}-f{} nor {option}-i{} must be present."));

            Matrix matrix = new Matrix();
            if (Filename != null)
            {
                if (!File.Exists(Filename))
                    return CommandLineParser.Colorize(RhoML.Parse("The specified file, {{h}}{0}{{}}, does not exist.".Fmt(Filename)));
                var xml = XDocument.Parse(File.ReadAllText(Filename));
                var element = xml.Descendants().FirstOrDefault(el => el.AttributeI("id").NullOr(id => id.Value == Id) == true);
                if (element == null)
                    return CommandLineParser.Colorize(RhoML.Parse("An XML element with the specified ID, {{h}}{0}{{}}, does not exist in the specified file.".Fmt(Id)));
                SvgPath = element.AttributeI("d")?.Value;
                if (SvgPath == null)
                    return CommandLineParser.Colorize(RhoML.Parse("The XML element with the specified ID, {{h}}{0}{{}}, does not have the {{h}}d{{}} attribute.".Fmt(Id)));

                var tr = element.AttributeI("transform")?.Value;
                Match m;
                if (tr != null && (m = Regex.Match(tr, @"^\s*matrix\s*\(\s*(-?\d*(?:\d|\.\d+)),\s*(-?\d*(?:\d|\.\d+)),\s*(-?\d*(?:\d|\.\d+)),\s*(-?\d*(?:\d|\.\d+)),\s*(-?\d*(?:\d|\.\d+)),\s*(-?\d*(?:\d|\.\d+))\s*\)\s*$")).Success)
                {
                    matrix = new Matrix(float.Parse(m.Groups[1].Value), float.Parse(m.Groups[2].Value), float.Parse(m.Groups[3].Value), float.Parse(m.Groups[4].Value), float.Parse(m.Groups[5].Value), float.Parse(m.Groups[6].Value));
                    System.Console.WriteLine("Using matrix!");
                }
            }

            PointF? prev = null;
            bool figure = false;
            SvgPath = SvgPath.Trim();
            var num = @"-?\d*(?:\d|\.\d+)(?=$|\s|,)";
            ParsedPath = new GraphicsPath(UseWindingFill ? FillMode.Winding : FillMode.Alternate);
            var index = 0;
            char? prevCommand = null;
            var commands = "MLCZz";
            while (index < SvgPath.Length)
            {
                var match = Regex.Match(SvgPath.Substring(index), @"^(?:
                    [ML]{1}(?:\s|,)*({0})(?:\s|,)+({0})(?:\s|,)*|
                    [C]{2}(?:\s|,)*({0})(?:\s|,)+({0})(?:\s|,)+({0})(?:\s|,)+({0})(?:\s|,)+({0})(?:\s|,)+({0})(?:\s|,)*|
                    [Zz](?:\s|,)*
                )".Fmt(
                    /* {0} */ num,
                    /* {1} */ prevCommand == 'M' || prevCommand == 'L' ? "?" : "",
                    /* {2} */ prevCommand == 'C' ? "?" : ""
                ), RegexOptions.IgnorePatternWhitespace);
                if (!match.Success)
                    return "The specified path data does not conform to the expected syntax at index {0}.".Fmt(index);
                index += match.Length;
                if (match.Value[0] != 'M' && prev.Value == null)
                    return "The path data cannot start with any command other than M.";
                if (commands.Contains(match.Value[0]))
                    prevCommand = match.Value[0];
                switch (prevCommand)
                {
                    case 'M':
                        if (figure)
                            ParsedPath.CloseFigure();
                        prev = new PointF(float.Parse(match.Groups[1].Value), float.Parse(match.Groups[2].Value));
                        break;

                    case 'L':
                        var p = new PointF(float.Parse(match.Groups[1].Value), float.Parse(match.Groups[2].Value));
                        ParsedPath.AddLine(prev.Value, p);
                        prev = p;
                        figure = true;
                        break;

                    case 'C':
                        var p1 = new PointF(float.Parse(match.Groups[3].Value), float.Parse(match.Groups[4].Value));
                        var p2 = new PointF(float.Parse(match.Groups[5].Value), float.Parse(match.Groups[6].Value));
                        var p3 = new PointF(float.Parse(match.Groups[7].Value), float.Parse(match.Groups[8].Value));
                        ParsedPath.AddBezier(prev.Value, p1, p2, p3);
                        prev = p3;
                        figure = true;
                        break;

                    case 'Z':
                    case 'z':
                        if (figure)
                        {
                            ParsedPath.CloseFigure();
                            figure = false;
                        }
                        break;

                    default:
                        return "The specified path data does not conform to the expected syntax. (2)";
                }
            }
            if (figure)
                ParsedPath.CloseFigure();
            ParsedPath.Transform(matrix);
            return null;
        }
    }
}
