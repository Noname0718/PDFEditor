using System.Security.Cryptography.X509Certificates;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;

namespace PDFEditor.Ink
{
    /// <summary>
    /// 필기 도구 종류 정의 (펜/형광펜/지우개)
    /// </summary>
    public enum DrawTool
    {
        Pen, 
        Highlighter,
        Eraser
    }
    public class InkToolManager
    {
        public DrawTool CurrentTool { get; private set; } = DrawTool.Pen;

        public Color CurrentColor { get; private set; } = Colors.Black;
        public double CurrentThickness { get; private set; } = 3.0;

        /// <summary>
        /// InkCanvas의 EditingMode를 주어진 도구에 맞게 전환한다.
        /// </summary>
        public void SetTool(DrawTool tool, InkCanvas inkCanvas)
        {
            CurrentTool = tool;

            if (inkCanvas == null) return;

            switch (tool)
            {
                case DrawTool.Pen:
                case DrawTool.Highlighter:
                    inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                    UpdateDrawingAttributes(inkCanvas);
                    break;
                case DrawTool.Eraser:
                    inkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
                    inkCanvas.EraserShape = new RectangleStylusShape(10, 10); // 지우개 크기 고정
                    break;
            }
        }
        /// <summary>
        /// 현재 색상을 저장하고 필요한 InkCanvas에 즉시 반영한다.
        /// </summary>
        public void SetColor(Color color, InkCanvas inkCanvas)
        {
            CurrentColor = color;
            if (inkCanvas == null) return;

            if (CurrentTool != DrawTool.Eraser)
            {
                    UpdateDrawingAttributes(inkCanvas);
            }
        }
            
        /// <summary>
        /// 선 두께를 변경하고 펜/형광펜 모드에서만 적용한다.
        /// </summary>
        public void SetThickness(double thickness, InkCanvas inkCanvas)
        {
            CurrentThickness = thickness;
            if (inkCanvas == null) return;

            if (CurrentTool != DrawTool.Eraser)
            {
                    UpdateDrawingAttributes(inkCanvas);
            }
        }

        /// <summary>
        /// DrawingAttributes를 세팅하여 InkCanvas.DefaultDrawingAttributes에 넣는다.
        /// 형광펜일 때는 투명도와 IsHighlighter를 따로 조정한다.
        /// </summary>
        private void UpdateDrawingAttributes(InkCanvas inkCanvas)
        {
            if (inkCanvas == null) return;

            var attributes = new DrawingAttributes
            {
                Width = CurrentThickness,
                Height = CurrentThickness,
                FitToCurve = true,
                IgnorePressure = true
            };
            if (CurrentTool == DrawTool.Pen)
            {
                attributes.Color = CurrentColor;
                attributes.IsHighlighter = false;
            }
            else if (CurrentTool == DrawTool.Highlighter)
            {
                Color c = CurrentColor;
                c.A = 120; // 투명도 설정 (형광펜 효과)
                attributes.IsHighlighter = true;
                attributes.Color = c;
            }
            
            inkCanvas.DefaultDrawingAttributes = attributes;
        }

    }
}
