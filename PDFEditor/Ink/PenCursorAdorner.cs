using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;


namespace PDFEditor.Ink
{
    internal sealed class PenCursorAdorner : Adorner
    {
        internal enum CursorVisual
        {
            Pen,
            Eraser
        }

        private bool _visible = false;
        private Point _position;
        private double _diameter = 3;
        private CursorVisual _visual = CursorVisual.Pen;

        public PenCursorAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public void UpdatePosition(Point position)
        {
            _position = position;
            _visible = true;
            InvalidateVisual();
        }

        public void Hide()
        {
            _visible = false;
            InvalidateVisual();
        }

        public void UpdateThickness(double thickness)
        {
            _diameter = Math.Max(2, thickness);
            InvalidateVisual();
        }

        public void SetVisual(CursorVisual visual)
        {
            _visual = visual;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (!_visible) return;

            double radius = _diameter / 2;
            switch (_visual)
            {
                case CursorVisual.Pen:
                    drawingContext.DrawEllipse(
                        new SolidColorBrush(Color.FromArgb(140, 240, 240, 100)),
                        new Pen(Brushes.DarkOliveGreen, 1),
                        _position,
                        radius,
                        radius);
                    break;
                case CursorVisual.Eraser:
                    var rect = new Rect(_position.X - radius, _position.Y - radius, _diameter, _diameter);
                    drawingContext.DrawRectangle(
                        new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                        new Pen(Brushes.Gray, 1),
                        rect);
                    break;
            }
        }
    }
}
