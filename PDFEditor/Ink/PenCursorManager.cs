using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace PDFEditor.Ink
{
    /// <summary>
    /// 펜/형광펜 입력 시 InkCanvas 위에 명확한 커서 원을 표시한다.
    /// </summary>
    public class PenCursorManager
    {
        private class Entry
        {
            public InkCanvas Canvas;
            public PenCursorAdorner Adorner;
            public AdornerLayer Layer;
        }

        private readonly Dictionary<InkCanvas, Entry> _entries = new Dictionary<InkCanvas, Entry>();
        private double _thickness = 3.0;
        private bool _enabled = true;

        public void AttachCanvas(InkCanvas canvas)
        {
            if (canvas == null || _entries.ContainsKey(canvas))
                return;

            RoutedEventHandler loadedHandler = null;
            loadedHandler = (sender, args) =>
            {
                canvas.Loaded -= loadedHandler;
                InitializeCanvasEntry(canvas);
            };

            if (canvas.IsLoaded)
            {
                InitializeCanvasEntry(canvas);
            }
            else
            {
                canvas.Loaded += loadedHandler;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            foreach (Entry entry in _entries.Values)
            {
                if (!enabled)
                {
                    entry.Adorner.Hide();
                }

                UpdateCursorVisibility(entry.Canvas);
            }
        }

        public void SetThickness(double thickness)
        {
            _thickness = thickness;
            foreach (Entry entry in _entries.Values)
            {
                entry.Adorner.UpdateThickness(thickness);
            }
        }

        public void Clear()
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Canvas.MouseEnter -= Canvas_MouseEnter;
                entry.Canvas.MouseMove -= Canvas_MouseMove;
                entry.Canvas.MouseLeave -= Canvas_MouseLeave;

                entry.Canvas.StylusEnter -= Canvas_StylusEnter;
                entry.Canvas.StylusMove -= Canvas_StylusMove;
                entry.Canvas.StylusLeave -= Canvas_StylusLeave;

                entry.Layer.Remove(entry.Adorner);
                entry.Canvas.UseCustomCursor = false;
                entry.Canvas.ClearValue(InkCanvas.CursorProperty);
            }

            _entries.Clear();
        }

        private void Canvas_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_enabled) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_enabled) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.Hide();
            }
        }

        private void Canvas_StylusEnter(object sender, StylusEventArgs e)
        {
            if (!_enabled) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        private void Canvas_StylusMove(object sender, StylusEventArgs e)
        {
            if (!_enabled) return;
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.UpdatePosition(e.GetPosition(canvas));
            }
        }

        private void Canvas_StylusLeave(object sender, StylusEventArgs e)
        {
            InkCanvas canvas = sender as InkCanvas;
            Entry entry;
            if (canvas != null && _entries.TryGetValue(canvas, out entry))
            {
                entry.Adorner.Hide();
            }
        }

        private void UpdateCursorVisibility(InkCanvas canvas)
        {
            if (_enabled)
            {
                canvas.UseCustomCursor = true;
                canvas.Cursor = Cursors.None;
            }
            else
            {
                canvas.UseCustomCursor = false;
                canvas.ClearValue(InkCanvas.CursorProperty);
            }
        }

        private void InitializeCanvasEntry(InkCanvas canvas)
        {
            if (canvas == null)
                return;

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(canvas);
            if (layer == null)
                return;

            var adorner = new PenCursorAdorner(canvas);
            adorner.UpdateThickness(_thickness);
            if (!_enabled) adorner.Hide();

            layer.Add(adorner);

            var entry = new Entry
            {
                Canvas = canvas,
                Adorner = adorner,
                Layer = layer
            };

            _entries[canvas] = entry;

            canvas.MouseEnter += Canvas_MouseEnter;
            canvas.MouseMove += Canvas_MouseMove;
            canvas.MouseLeave += Canvas_MouseLeave;

            canvas.StylusEnter += Canvas_StylusEnter;
            canvas.StylusMove += Canvas_StylusMove;
            canvas.StylusLeave += Canvas_StylusLeave;

            UpdateCursorVisibility(canvas);
        }
    }
}
