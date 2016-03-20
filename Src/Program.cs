using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using RT.Generexes;
using RT.Util;
using RT.Util.CommandLine;
using RT.Util.ExtensionMethods;

namespace PovRounding
{
    class Program
    {
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            if (args.Length == 2 && args[0] == "--post-build-check")
                return Ut.RunPostBuildChecks(args[1], typeof(Program).Assembly, typeof(Ut).Assembly);

            CommandLine cmd;
            try
            {
                cmd = CommandLineParser.Parse<CommandLine>(args);
            }
            catch (CommandLineParseException pe)
            {
                pe.WriteUsageInfoToConsole();
                return 1;
            }

            string extraCode = null;
            if (cmd.ExtraCodeFile != null)
                extraCode = File.ReadAllText(cmd.ExtraCodeFile);

            var gp = cmd.GetGraphicsPath();
            Ut.Assert(gp.PathTypes.Length == gp.PathPoints.Length);
            var data = gp.PathTypes.Zip(gp.PathPoints, (pt, pp) => new { Type = (PathPointType) pt, Point = pp }).ToArray();
            Ut.Assert(data.Length > 0 && data[0].Type == PathPointType.Start);
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i].Type == PathPointType.Start)
                    Ut.Assert(i == 0 || data[i - 1].Type.HasFlag(PathPointType.CloseSubpath));
                if (data[i].Type.HasFlag(PathPointType.CloseSubpath))
                    Ut.Assert(i == data.Length - 1 || data[i + 1].Type == PathPointType.Start);
            }

            var start = data.CreateGenerex(inf => inf.Type == PathPointType.Start);
            var line = data.CreateGenerex(inf => (inf.Type & PathPointType.PathTypeMask) == PathPointType.Line);
            var bézier = data.CreateGenerex(inf => (inf.Type & PathPointType.PathTypeMask) == PathPointType.Bezier3);
            var end = data.CreateGenerex(inf => inf.Type.HasFlag(PathPointType.CloseSubpath));

            var regex = start.Process(m => m.Match[0].Point).ThenRaw(
                Generex.Ors(
                    line.Process(m => SegmentType.Line),
                    bézier.Times(3).Process(m => SegmentType.Bézier)
                )
                    .Process(m => new { Type = m.Result, Points = m.Match.Select(inf => inf.Point).ToArray() })
                    .Repeat()
                    .Then(end.LookBehind())
                    .Do(m => m.Match.SkipLast(1).All(inf => !inf.Type.HasFlag(PathPointType.CloseSubpath))),
                (startPoint, segment) => new { StartPoint = startPoint, Segment = segment }
            ).RepeatGreedy();

            var match = regex.MatchExact(data);
            if (match == null)
                throw new InvalidOperationException("The GraphicsPath data has not been recognized by Generex. This indicates a bug in the Generex grammar.");

            var curves = match.Result.ToArray();

            var getPoints = Ut.Lambda((bool close) => curves.Select(curve =>
            {
                var segments = curve.Segment.Concat(new { Type = SegmentType.Line, Points = new[] { curve.StartPoint } });
                if (close)
                    segments = segments.Concat(curve.Segment.First());
                return segments.Aggregate(
                    new { LastPoint = curve.StartPoint, Points = Enumerable.Empty<PointF>() },
                    (prev, next) =>
                        next.Type == SegmentType.Line && next.Points[0] == prev.LastPoint ? prev :
                        new
                        {
                            LastPoint = next.Points.Last(),
                            Points = prev.Points.Concat(next.Type == SegmentType.Bézier
                                ? prev.LastPoint.Concat(next.Points)
                                : new[] { prev.LastPoint, new PointF(prev.LastPoint.X * 2 / 3 + next.Points[0].X * 1 / 3, prev.LastPoint.Y * 2 / 3 + next.Points[0].Y * 1 / 3), new PointF(prev.LastPoint.X * 1 / 3 + next.Points[0].X * 2 / 3, prev.LastPoint.Y * 1 / 3 + next.Points[0].Y * 2 / 3), next.Points[0] }
                            )
                        }
                ).Points.ToArray();
            }).ToArray());
            var openPoints = getPoints(false);
            var closedPoints = getPoints(true);

            var source = @"
// Command line:
// {4}{5}

#declare {3} = union {{
    prism {{
        bezier_spline linear_sweep 0, {0}, {1}
        {2}
        rotate 90*x
        {6}
    }}".Fmt(
                /* {0} */ cmd.ExtrusionDepth,
                /* {1} */ openPoints.Sum(p => p.Length),
                /* {2} */ openPoints.SelectMany(p => p).Split(4)
                                    .Select(grp => grp.Select(p => "<{0}, {1}>".Fmt(p.X, p.Y)).JoinString(", "))
                                    .JoinString("," + Environment.NewLine)
                                    .Indent(8, false),
                /* {3} */ cmd.ObjectName,
                /* {4} */ CommandRunner.ArgsToCommandLine(args),
                /* {5} */ cmd.ExtraCodeFile == null ? null : "\n// Extra code file:\n" + File.ReadAllLines(cmd.ExtraCodeFile).JoinString("\n", prefix: "// "),
                /* {6} */ extraCode
            );

            source += openPoints.SelectMany(gr => gr.Split(4).Select(Enumerable.ToArray).ConsecutivePairs(true)).Select(pair =>
            {
                var pts = pair.Item1;
                var next = pair.Item2;

                var displaced = displace(pts, cmd.RoundingRadius);
                var displacedNext = displace(next, cmd.RoundingRadius);

                var code = "";

                if (!cmd.SkipFront)
                    code += patch(
                        "Front fillet",
                        Enumerable.Range(0, 4).Select(i => pts[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => combine(pts[i], displaced[i], cmd.RoundingFactor)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, (-cmd.RoundingRadius) * (1 - cmd.RoundingFactor))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, (-cmd.RoundingRadius))).JoinString(", "),
                        extraCode, cmd.Smoothness);

                code += patch(
                    "Side",
                    displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.RoundingRadius)).JoinString(", "),
                    displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.RoundingRadius * 1 / 3 - cmd.ExtrusionDepth * 1 / 3)).JoinString(", "),
                    displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, cmd.RoundingRadius * 1 / 3 - cmd.ExtrusionDepth * 2 / 3)).JoinString(", "),
                    displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, cmd.RoundingRadius - cmd.ExtrusionDepth)).JoinString(", "),
                    extraCode, cmd.Smoothness);

                if (!cmd.SkipBack)
                    code += patch(
                        "Back fillet",
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, cmd.RoundingRadius - cmd.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, combine(cmd.RoundingRadius - cmd.ExtrusionDepth, -cmd.ExtrusionDepth, cmd.RoundingFactor))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => combine(pts[i], displaced[i], cmd.RoundingFactor)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => pts[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.ExtrusionDepth)).JoinString(", "),
                        extraCode, cmd.Smoothness);

                Ut.Assert(pts[3] == next[0]);

                var angle1 = Math.Atan2(pts[3].Y - pts[2].Y, pts[3].X - pts[2].X);
                var angle2 = Math.Atan2(next[0].Y - next[1].Y, next[0].X - next[1].X);
                var totalAngle = angle1 - angle2;
                if (totalAngle < 0) totalAngle += Math.PI * 2;
                if (totalAngle >= Math.PI)
                    return code;

                var intersection = intersect(displaced[2], displaced[3], displacedNext[1], displacedNext[0]);
                if (float.IsNaN(intersection.X))
                    return code;

                var fillet = Ut.NewArray(
                    displaced[3],
                    combine(displaced[3], intersection, cmd.RoundingFactor),
                    combine(displacedNext[0], intersection, cmd.RoundingFactor),
                    displacedNext[0]
                );

                if (!cmd.SkipFront)
                    code += patch(
                        "Front corner fillet",
                        Enumerable.Range(0, 4).Select(i => next[0]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => combine(next[0], fillet[i], cmd.RoundingFactor)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.RoundingRadius * (1 - cmd.RoundingFactor))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.RoundingRadius)).JoinString(", "),
                        extraCode, cmd.Smoothness);

                code += patch(
                    "Side corner fillet",
                    Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.RoundingRadius)).JoinString(", "),
                    Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, combine(-cmd.RoundingRadius, cmd.RoundingRadius - cmd.ExtrusionDepth, 1f / 3f))).JoinString(", "),
                    Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, combine(-cmd.RoundingRadius, cmd.RoundingRadius - cmd.ExtrusionDepth, 2f / 3f))).JoinString(", "),
                    Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, cmd.RoundingRadius - cmd.ExtrusionDepth)).JoinString(", "),
                    extraCode, cmd.Smoothness);

                if (!cmd.SkipBack)
                    code += patch(
                        "Back corner fillet",
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, cmd.RoundingRadius - cmd.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, combine(cmd.RoundingRadius - cmd.ExtrusionDepth, -cmd.ExtrusionDepth, cmd.RoundingFactor))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => combine(next[0], fillet[i], cmd.RoundingFactor)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => next[0]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -cmd.ExtrusionDepth)).JoinString(", "),
                        extraCode, cmd.Smoothness);

                return code;

            }).JoinString(Environment.NewLine);

            source += Environment.NewLine + "}" + Environment.NewLine;

            File.WriteAllText(cmd.Filename, source);

            return 0;
        }

        enum SegmentType { Line, Bézier }

        private static PointF intersect(PointF f1, PointF t1, PointF f2, PointF t2)
        {
            var det = (f1.X - t1.X) * (f2.Y - t2.Y) - (f1.Y - t1.Y) * (f2.X - t2.X);
            if (det == 0)
                return new PointF(float.NaN, float.NaN);
            return new PointF(
                ((f1.X * t1.Y - f1.Y * t1.X) * (f2.X - t2.X) - (f1.X - t1.X) * (f2.X * t2.Y - f2.Y * t2.X)) / det,
                ((f1.X * t1.Y - f1.Y * t1.X) * (f2.Y - t2.Y) - (f1.Y - t1.Y) * (f2.X * t2.Y - f2.Y * t2.X)) / det
            );
        }

        private static PointF[] displace(PointF[] pts, float radius)
        {
            var ret = Ut.NewArray(
                displace(pts[0], pts[0], pts[1], radius),
                displace(pts[1], pts[0], pts[2], radius),
                displace(pts[2], pts[1], pts[3], radius),
                displace(pts[3], pts[2], pts[3], radius)
            );
            if (ret.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y)))
                throw new InvalidOperationException("The curve contains a Bézier curve in which one of the control points is equal to its adjacent endpoint. This is not allowed.");
            return ret;
        }

        private static PointF displace(PointF pt, PointF on, PointF right, float radius)
        {
            if (on == right)
                throw new InvalidOperationException("The curve contains a Bézier curve in which two points are equal ({0}). This is not allowed.".Fmt(on));
            return pt - normalize(on.Y - right.Y, right.X - on.X, radius);
        }

        private static SizeF normalize(float x, float y, float to)
        {
            var d = Math.Sqrt(x * x + y * y);
            return new SizeF((float) (x * to / d), (float) (y * to / d));
        }

        private static PointF combine(PointF one, PointF two, float ratioOfTwo)
        {
            return new PointF(combine(one.X, two.X, ratioOfTwo), combine(one.Y, two.Y, ratioOfTwo));
        }

        private static float combine(float one, float two, float ratioOfTwo)
        {
            return one * (1 - ratioOfTwo) + two * ratioOfTwo;
        }

        private static string patch(string comment, string row1, string row2, string row3, string row4, string extraCode, int smoothness)
        {
            return $@"
    // {comment}
    bicubic_patch {{
        type 1 flatness 0.0001
        u_steps {smoothness} v_steps {smoothness}
        {row1},
        {row2},
        {row3},
        {row4}
        rotate 180*x
        {extraCode}
    }}";
        }
    }
}
