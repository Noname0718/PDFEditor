using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;


namespace PDFEditor.Ink
{
    internal sealed class PenCursorAdorner : Adorner
    {
        private bool _visible = false;
        private Point _position;
        private double _diameter = 3;

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
            _diameter = Math.Max(3, thickness);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (!_visible) return;

            double radius = _diameter / 2;
            var fill = new SolidColorBrush(Color.FromArgb(120, 255, 255, 120));
            drawingContext.DrawEllipse(fill, null, _position, radius, radius);
        }
    }
}
