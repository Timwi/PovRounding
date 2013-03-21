using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using RT.Generexes;
using RT.Util;
using RT.Util.Drawing;
using RT.Util.ExtensionMethods;

namespace PovRounding
{
    class Program
    {
        sealed class LineOfText
        {
            public string Filename;
            public string ObjectName;
            public string Text;
            public float ExtrusionDepth = 6;
            public float RoundingRadius = 1;
            public string Font;
            public FontStyle Style;
        }

        static readonly LineOfText[] LinesOfText = Ut.NewArray(
            new LineOfText { Filename = "Kio.pov", ObjectName = "Kio", Text = "KIO", Font = "Berlin Sans FB", Style = 0, ExtrusionDepth = 15, RoundingRadius = 3 },
            new LineOfText { Filename = "Estas.pov", ObjectName = "Estas", Text = "ESTAS...?", Font = "Berlin Sans FB", Style = 0, ExtrusionDepth = 15, RoundingRadius = 3 },
            new LineOfText { Filename = "Kiu.pov", ObjectName = "Kiu", Text = "KIU", Font = "Georgia", Style = 0, ExtrusionDepth = 15, RoundingRadius = 3 },
            new LineOfText { Filename = "Estis.pov", ObjectName = "Estis", Text = "ESTIS...?", Font = "Georgia", Style = 0, ExtrusionDepth = 15, RoundingRadius = 3 },
            new LineOfText { Filename = "Kiu2.pov", ObjectName = "Kiu2", Text = "KIU", Font = "SketchFlow Print", Style = 0, ExtrusionDepth = 15, RoundingRadius = 3 },
            new LineOfText { Filename = "Estas2.pov", ObjectName = "Estas2", Text = "ESTAS...?", Font = "SketchFlow Print", Style = 0, ExtrusionDepth = 15, RoundingRadius = 3 }
        ).Concat(
            Enumerable.Range(1, 5).Select(i => (200 * i).ToString()).Select(num =>
                new LineOfText { Filename = "Num{0}.pov".Fmt(num), ObjectName = "Num{0}".Fmt(num), Text = num, Font = "ITC Korinna Std", Style = 0, ExtrusionDepth = 12, RoundingRadius = 3 }
            )
        ).ToArray();

        const string DestinationDirectory = @"D:\Daten\Upload\Jeopardy";

        static void Main(string[] args)
        {
            foreach (var textInf in LinesOfText)
            {
                PointF[] points = null;
                byte[] types = null;

                GraphicsUtil.DrawBitmap(1024, 768, g =>
                {
                    g.Clear(Color.Black);
                    var gp = new GraphicsPath();
                    gp.AddString(textInf.Text, new FontFamily(textInf.Font), (int) textInf.Style, 64f, new PointF(0, 0), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    g.FillPath(Brushes.White, gp);
                    points = gp.PathPoints;
                    types = gp.PathTypes;
                });

                var data = Enumerable.Range(0, points.Length).Select(i => new { Type = (PathPointType) types[i], Point = points[i] }).ToArray();
                Ut.Assert(data.Length > 0 && data[0].Type == PathPointType.Start);
                foreach (var stuff in data.Select((inf, i) => new { Inf = inf, I = i, Valid = inf.Type == PathPointType.Start ? (i == 0 || data[i - 1].Type.HasFlag(PathPointType.CloseSubpath)) : inf.Type.HasFlag(PathPointType.CloseSubpath) ? (i == data.Length - 1 || data[i + 1].Type == PathPointType.Start) : true }))
                    Ut.Assert(stuff.Valid);

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
                    Debugger.Break();

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
#declare {3} = union {{
    prism {{
        bezier_spline linear_sweep 0, {0}, {1}
        {2}
        rotate 90*x
    }}".Fmt(
                    textInf.ExtrusionDepth,
                    openPoints.Sum(p => p.Length),
                    openPoints.SelectMany(p => p).Split(4)
                        .Select(grp => grp.Select(p => "<{0}, {1}>".Fmt(p.X, p.Y)).JoinString(", "))
                        .JoinString("," + Environment.NewLine)
                        .Indent(8, false),
                    textInf.ObjectName
                );

                source += openPoints.SelectMany(gr => gr.Split(4).Select(Enumerable.ToArray).ConsecutivePairs(true)).Select(pair =>
                {
                    var pts = pair.Item1;
                    var next = pair.Item2;

                    var displaced = displace(pts, textInf.RoundingRadius);
                    var displacedNext = displace(next, textInf.RoundingRadius);

                    if (displaced.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y)))
                        Debugger.Break();

                    var code = "";

                    code += patch(
                        "Front fillet",
                        Enumerable.Range(0, 4).Select(i => pts[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => comb(pts[i], displaced[i], .76f)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, (-textInf.RoundingRadius) * (1 - .76f))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, (-textInf.RoundingRadius))).JoinString(", "));

                    code += patch(
                        "Side",
                        displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.RoundingRadius)).JoinString(", "),
                        displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.RoundingRadius * 1 / 3 - textInf.ExtrusionDepth * 1 / 3)).JoinString(", "),
                        displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, textInf.RoundingRadius * 1 / 3 - textInf.ExtrusionDepth * 2 / 3)).JoinString(", "),
                        displaced.Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, textInf.RoundingRadius - textInf.ExtrusionDepth)).JoinString(", "));

                    code += patch(
                        "Back fillet",
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, textInf.RoundingRadius - textInf.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => displaced[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, comb(textInf.RoundingRadius - textInf.ExtrusionDepth, -textInf.ExtrusionDepth, .76f))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => comb(pts[i], displaced[i], .76f)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => pts[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.ExtrusionDepth)).JoinString(", "));

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
                        comb(displaced[3], intersection, .76f),
                        comb(displacedNext[0], intersection, .76f),
                        displacedNext[0]
                    );

                    code += patch(
                        "Front corner fillet",
                        Enumerable.Range(0, 4).Select(i => next[0]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => comb(next[0], fillet[i], .76f)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, 0)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.RoundingRadius * (1 - .76f))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.RoundingRadius)).JoinString(", "));

                    code += patch(
                        "Side corner fillet",
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.RoundingRadius)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, comb(-textInf.RoundingRadius, textInf.RoundingRadius - textInf.ExtrusionDepth, 1f / 3f))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, comb(-textInf.RoundingRadius, textInf.RoundingRadius - textInf.ExtrusionDepth, 2f / 3f))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, textInf.RoundingRadius - textInf.ExtrusionDepth)).JoinString(", "));

                    code += patch(
                        "Back corner fillet",
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, textInf.RoundingRadius - textInf.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => fillet[i]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, comb(textInf.RoundingRadius - textInf.ExtrusionDepth, -textInf.ExtrusionDepth, .76f))).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => comb(next[0], fillet[i], .76f)).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.ExtrusionDepth)).JoinString(", "),
                        Enumerable.Range(0, 4).Select(i => next[0]).Select(p => "<{0}, {1}, {2}>".Fmt(p.X, p.Y, -textInf.ExtrusionDepth)).JoinString(", "));

                    return code;

                }).JoinString(Environment.NewLine);

                source += Environment.NewLine + "}" + Environment.NewLine;

                File.WriteAllText(Path.Combine(DestinationDirectory, textInf.Filename), source);
            }
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
            return Ut.NewArray(
                displace(pts[0], pts[0], pts[1], radius),
                displace(pts[1], pts[0], pts[2], radius),
                displace(pts[2], pts[1], pts[3], radius),
                displace(pts[3], pts[2], pts[3], radius)
            );
        }

        private static PointF displace(PointF pt, PointF on, PointF right, float radius)
        {
            return pt - normalize(on.Y - right.Y, right.X - on.X, radius);
        }

        private static SizeF normalize(float x, float y, float to)
        {
            var d = Math.Sqrt(x * x + y * y);
            return new SizeF((float) (x * to / d), (float) (y * to / d));
        }

        private static PointF comb(PointF one, PointF two, float ratioOfTwo)
        {
            return new PointF(comb(one.X, two.X, ratioOfTwo), comb(one.Y, two.Y, ratioOfTwo));
        }

        private static float comb(float one, float two, float ratioOfTwo)
        {
            return one * (1 - ratioOfTwo) + two * ratioOfTwo;
        }

        private static string patch(string comment, string row1, string row2, string row3, string row4)
        {
            return @"
    // {0}
    bicubic_patch {{
        type 1 flatness 0.001
        u_steps 4 v_steps 4
        {1},
        {2},
        {3},
        {4}
        rotate 180*x
    }}".Fmt(comment, row1, row2, row3, row4);
        }
    }
}
