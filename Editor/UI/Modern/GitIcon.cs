using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    /// <summary>
    /// A small stroke icon drawn with Painter2D on a 16×16 grid. It takes its colour from the
    /// element's resolved USS <c>color</c>, so icons follow text colour, hover and theme rules.
    /// </summary>
    class GitIcon : VisualElement
    {
        public const string UssClassName = "gfu-icon";
        private static readonly CustomStyleProperty<float> StrokeProperty = new CustomStyleProperty<float>("--gfu-stroke");

        private string iconName;
        private float stroke = 1.4f;

        public GitIcon(string name, int size = 16)
        {
            AddToClassList(UssClassName);
            pickingMode = PickingMode.Ignore;
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            iconName = name;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyleResolved);
        }

        public string IconName
        {
            get => iconName;
            set
            {
                if (iconName == value)
                    return;
                iconName = value;
                MarkDirtyRepaint();
            }
        }

        private void OnStyleResolved(CustomStyleResolvedEvent evt)
        {
            if (evt.customStyle.TryGetValue(StrokeProperty, out var value))
                stroke = value;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext ctx)
        {
            if (string.IsNullOrEmpty(iconName) || !Shapes.TryGetValue(iconName, out var shape))
                return;
            var rect = contentRect;
            if (rect.width <= 0 || rect.height <= 0)
                return;

            var scale = Mathf.Min(rect.width, rect.height) / 16f;
            var offset = new Vector2(rect.x + (rect.width - 16f * scale) * 0.5f, rect.y + (rect.height - 16f * scale) * 0.5f);
            var p = ctx.painter2D;
            var color = resolvedStyle.color;
            p.strokeColor = color;
            p.fillColor = color;
            p.lineWidth = stroke * scale;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            shape(new Pen(p, scale, offset));
        }

        /// <summary>Helper that maps the 16×16 design grid to element pixels.</summary>
        internal struct Pen
        {
            private readonly Painter2D p;
            private readonly float s;
            private readonly Vector2 o;

            public Pen(Painter2D painter, float scale, Vector2 offset)
            {
                p = painter;
                s = scale;
                o = offset;
            }

            private Vector2 V(float x, float y) => new Vector2(o.x + x * s, o.y + y * s);

            public void Line(params float[] pts)
            {
                p.BeginPath();
                p.MoveTo(V(pts[0], pts[1]));
                for (var i = 2; i + 1 < pts.Length; i += 2)
                    p.LineTo(V(pts[i], pts[i + 1]));
                p.Stroke();
            }

            public void Poly(params float[] pts)
            {
                p.BeginPath();
                p.MoveTo(V(pts[0], pts[1]));
                for (var i = 2; i + 1 < pts.Length; i += 2)
                    p.LineTo(V(pts[i], pts[i + 1]));
                p.ClosePath();
                p.Stroke();
            }

            public void Circle(float cx, float cy, float r)
            {
                p.BeginPath();
                p.Arc(V(cx, cy), r * s, Angle.Degrees(0), Angle.Degrees(360));
                p.ClosePath();
                p.Stroke();
            }

            public void Dot(float cx, float cy, float r)
            {
                p.BeginPath();
                p.Arc(V(cx, cy), r * s, Angle.Degrees(0), Angle.Degrees(360));
                p.ClosePath();
                p.Fill();
            }

            /// <summary>Arc in degrees, 0 = +x, increasing clockwise (y points down).</summary>
            public void Arc(float cx, float cy, float r, float from, float to)
            {
                p.BeginPath();
                var a = from * Mathf.Deg2Rad;
                p.MoveTo(V(cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a)));
                p.Arc(V(cx, cy), r * s, Angle.Degrees(from), Angle.Degrees(to));
                p.Stroke();
            }

            public void Curve(float x0, float y0, float c1x, float c1y, float c2x, float c2y, float x1, float y1)
            {
                p.BeginPath();
                p.MoveTo(V(x0, y0));
                p.BezierCurveTo(V(c1x, c1y), V(c2x, c2y), V(x1, y1));
                p.Stroke();
            }

            public void Rect(float x, float y, float w, float h)
            {
                Poly(x, y, x + w, y, x + w, y + h, x, y + h);
            }
        }

        private static readonly Dictionary<string, Action<Pen>> Shapes = new Dictionary<string, Action<Pen>>
        {
            ["check"] = d => d.Line(3, 8.5f, 6.5f, 12, 13, 4.5f),
            ["chevron-down"] = d => d.Line(4.5f, 6.5f, 8, 10, 11.5f, 6.5f),
            ["chevron-right"] = d => d.Line(6.5f, 4.5f, 10, 8, 6.5f, 11.5f),
            ["chevron-left"] = d => d.Line(9.5f, 4.5f, 6, 8, 9.5f, 11.5f),
            ["download"] = d =>
            {
                d.Line(8, 2.5f, 8, 10.5f);
                d.Line(4.5f, 7.5f, 8, 11, 11.5f, 7.5f);
                d.Line(3, 13.5f, 13, 13.5f);
            },
            ["upload"] = d =>
            {
                d.Line(8, 11, 8, 3);
                d.Line(4.5f, 6, 8, 2.5f, 11.5f, 6);
                d.Line(3, 13.5f, 13, 13.5f);
            },
            ["sync"] = d =>
            {
                d.Arc(8, 8, 5.5f, 200, 340);
                d.Line(13.2f, 2.8f, 13.2f, 6.1f, 10, 6.1f);
                d.Arc(8, 8, 5.5f, 20, 160);
                d.Line(2.8f, 13.2f, 2.8f, 9.9f, 6, 9.9f);
            },
            ["lock"] = d =>
            {
                d.Rect(3.5f, 7, 9, 6.5f);
                d.Arc(8, 5, 2.5f, 180, 360);
                d.Line(5.5f, 5, 5.5f, 7);
                d.Line(10.5f, 5, 10.5f, 7);
            },
            ["unlock"] = d =>
            {
                d.Rect(3.5f, 7, 9, 6.5f);
                d.Arc(8, 5, 2.5f, 180, 330);
                d.Line(5.5f, 5, 5.5f, 7);
            },
            ["gear"] = d =>
            {
                d.Circle(8, 8, 2.2f);
                for (var i = 0; i < 8; i++)
                {
                    var a = i * Mathf.PI / 4f;
                    d.Line(8 + 4.2f * Mathf.Cos(a), 8 + 4.2f * Mathf.Sin(a), 8 + 6f * Mathf.Cos(a), 8 + 6f * Mathf.Sin(a));
                }
                d.Circle(8, 8, 4.2f);
            },
            ["search"] = d =>
            {
                d.Circle(7, 7, 4.5f);
                d.Line(10.5f, 10.5f, 13.5f, 13.5f);
            },
            ["plus"] = d =>
            {
                d.Line(8, 3, 8, 13);
                d.Line(3, 8, 13, 8);
            },
            ["more"] = d =>
            {
                d.Dot(3.5f, 8, 1.1f);
                d.Dot(8, 8, 1.1f);
                d.Dot(12.5f, 8, 1.1f);
            },
            ["warn"] = d =>
            {
                d.Poly(8, 2.2f, 14.2f, 13, 1.8f, 13);
                d.Line(8, 6.5f, 8, 9.3f);
                d.Dot(8, 11.3f, 0.8f);
            },
            ["info"] = d =>
            {
                d.Circle(8, 8, 5.8f);
                d.Line(8, 7.3f, 8, 11);
                d.Dot(8, 5.1f, 0.8f);
            },
            ["error"] = d =>
            {
                d.Circle(8, 8, 5.8f);
                d.Line(5.8f, 5.8f, 10.2f, 10.2f);
                d.Line(10.2f, 5.8f, 5.8f, 10.2f);
            },
            ["offline"] = d =>
            {
                d.Circle(8, 8, 5.8f);
                d.Line(3.9f, 3.9f, 12.1f, 12.1f);
            },
            ["clock"] = d =>
            {
                d.Circle(8, 8, 5.8f);
                d.Line(8, 4.8f, 8, 8, 10.2f, 9.4f);
            },
            ["undo"] = d =>
            {
                d.Line(5.5f, 3, 2.5f, 6, 5.5f, 9);
                d.Line(2.5f, 6, 9.5f, 6);
                d.Arc(9.5f, 10, 4, 270, 450);
                d.Line(9.5f, 14, 7, 14);
            },
            ["copy"] = d =>
            {
                d.Rect(5.5f, 5.5f, 8, 8);
                d.Line(10.5f, 5.5f, 10.5f, 2.5f, 2.5f, 2.5f, 2.5f, 10.5f, 5.5f, 10.5f);
            },
            ["external"] = d =>
            {
                d.Line(9, 2.5f, 13.5f, 2.5f, 13.5f, 7);
                d.Line(13.5f, 2.5f, 7.5f, 8.5f);
                d.Line(11.5f, 9.5f, 11.5f, 13.5f, 2.5f, 13.5f, 2.5f, 4.5f, 6.5f, 4.5f);
            },
            ["branch"] = d =>
            {
                d.Circle(4.5f, 3.5f, 1.5f);
                d.Circle(4.5f, 12.5f, 1.5f);
                d.Circle(11.5f, 5, 1.5f);
                d.Line(4.5f, 5, 4.5f, 11);
                d.Curve(11.5f, 6.5f, 11.5f, 9.5f, 4.5f, 8.5f, 4.5f, 11);
            },
            ["repo"] = d =>
            {
                d.Rect(3.5f, 2, 9, 12);
                d.Line(3.5f, 11, 12.5f, 11);
                d.Line(6, 2, 6, 11);
            },
            ["folder"] = d => d.Poly(2, 3.5f, 6.3f, 3.5f, 7.7f, 5, 14, 5, 14, 13, 2, 13),
            ["shield"] = d =>
            {
                d.Line(8, 1.8f, 13, 3.7f, 13, 7.7f);
                d.Curve(13, 7.7f, 13, 10.7f, 10.8f, 13, 8, 14.2f);
                d.Curve(8, 14.2f, 5.2f, 13, 3, 10.7f, 3, 7.7f);
                d.Line(3, 7.7f, 3, 3.7f, 8, 1.8f);
                d.Line(5.8f, 8, 7.4f, 9.6f, 10.4f, 6.5f);
            },
            ["close"] = d =>
            {
                d.Line(4, 4, 12, 12);
                d.Line(12, 4, 4, 12);
            },
            ["history"] = d =>
            {
                d.Arc(8, 8, 5.5f, 200, 520);
                d.Line(2.3f, 3.2f, 2.8f, 6.1f, 5.6f, 5.4f);
                d.Line(8, 5, 8, 8, 10, 9.3f);
            },
        };
    }
}
