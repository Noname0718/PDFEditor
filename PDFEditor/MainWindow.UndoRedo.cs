using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;

namespace PDFEditor
{
    public partial class MainWindow
    {
        private readonly UndoRedoManager _undoRedoManager = new UndoRedoManager();

        // Ctrl+Z 핫키나 메뉴를 통해 호출. 실제 Undo는 UndoRedoManager가 담당한다.
        private bool PerformUndo()
        {
            Debug.WriteLine(">>> PerformUndo 호출");
            return _undoRedoManager.Undo(this);
        }

        // Ctrl+Y 핫키나 메뉴를 통해 호출. 실제 Redo는 UndoRedoManager가 담당한다.
        private bool PerformRedo()
        {
            Debug.WriteLine(">>> PerformRedo 호출");
            return _undoRedoManager.Redo(this);
        }

        private void PushUndoAction(IUndoRedoAction action)
        {
            _undoRedoManager.Push(action);
        }

        private void ClearHistory()
        {
            _undoRedoManager.Clear();
        }

        // Text/Shape가 InkCanvas.Children에 추가될 때 호출되어 Undo 스택에 기록한다.
        private void RegisterShapeAddition(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null)
                return;

            if (!TryGetPageIndex(canvas, out int pageIndex))
                return;

            PushUndoAction(new ShapeAddedAction(pageIndex, element));
        }

        // Text/Shape 제거 시 대응하는 Undo 액션을 기록한다.
        private void RegisterShapeRemoval(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null)
                return;

            if (!TryGetPageIndex(canvas, out int pageIndex))
                return;

            PushUndoAction(new ShapeRemovedAction(pageIndex, element));
        }

        // 복사-붙여넣기처럼 여러 요소가 한 번에 추가될 때 Composite Action으로 묶는다.
        private void RegisterGroupedAddition(InkCanvas canvas, List<UIElement> elements, List<Stroke> strokes)
        {
            if (canvas == null)
                return;

            if (!TryGetPageIndex(canvas, out int pageIndex))
                return;

            var actions = new List<IUndoRedoAction>();
            if (elements != null)
            {
                foreach (var element in elements)
                {
                    if (element == null)
                        continue;
                    actions.Add(new ShapeAddedAction(pageIndex, element));
                }
            }

            if (strokes != null)
            {
                foreach (var stroke in strokes)
                {
                    if (stroke == null)
                        continue;
                    Guid id = EnsureStrokeId(stroke);
                    var snapshot = CloneStrokeWithId(stroke);
                    actions.Add(new StrokeAddedAction(pageIndex, id, snapshot));
                }
            }

            if (actions.Count > 0)
            {
                PushUndoAction(actions.Count == 1 ? actions[0] : new CompositeAction(actions));
            }
        }

        // InkCanvas가 새 Stroke를 만든 직후 호출되어 StrokeAddedAction을 Push.
        private void InkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            if (!_undoRedoManager.IsRecording)
                return;
            if (!(sender is InkCanvas canvas))
                return;

            ActivateCanvasPage(canvas);

            if (!TryGetPageIndex(canvas, out int pageIndex))
                return;

            Debug.WriteLine($">>> StrokeCollected page={pageIndex}, strokeHash={e.Stroke?.GetHashCode()}");

            Guid strokeId = EnsureStrokeId(e.Stroke);
            var snapshot = CloneStrokeWithId(e.Stroke);
            PushUndoAction(new StrokeAddedAction(pageIndex, strokeId, snapshot));
        }

        // InkCanvas의 StrokeCollection 변동(삭제/붙여넣기)을 감지해 Diff 기반 액션을 Push.
        private void InkCanvas_StrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (!_undoRedoManager.IsRecording)
                return;
            if (!(sender is InkCanvas canvas))
                return;

            ActivateCanvasPage(canvas);

            if (!TryGetPageIndex(canvas, out int pageIndex))
                return;

            var removedSnapshots = new List<Stroke>();
            var removedIds = new List<Guid>();
            if (e.Removed != null)
            {
                foreach (var stroke in e.Removed)
                {
                    var snapshot = CloneStrokeWithId(stroke);
                    if (snapshot != null)
                    {
                        removedSnapshots.Add(snapshot);
                        var id = GetStrokeId(snapshot);
                        if (id.HasValue)
                            removedIds.Add(id.Value);
                    }
                }
            }

            if (removedSnapshots.Count == 0)
                return;

            var addedSnapshots = new List<Stroke>();
            var addedIds = new List<Guid>();
            if (e.Added != null)
            {
                foreach (var stroke in e.Added)
                {
                    addedIds.Add(EnsureStrokeId(stroke));
                    var snapshot = CloneStrokeWithId(stroke);
                    if (snapshot != null)
                    {
                        addedSnapshots.Add(snapshot);
                    }
                }
            }

            Debug.WriteLine($">>> StrokesChanged page={pageIndex}, Added={addedSnapshots.Count}, Removed={removedSnapshots.Count}");

            PushUndoAction(new StrokeCollectionChangedAction(pageIndex, removedIds, removedSnapshots, addedIds, addedSnapshots));
        }

        /// <summary>
        /// 모든 Undo/Redo 스택과 기록 일시 중지를 담당하는 헬퍼.
        /// MainWindow는 Push/Undo/Redo만 호출하고 내부 플래그를 직접 다루지 않는다.
        /// </summary>
        private sealed class UndoRedoManager
        {
            private readonly Stack<IUndoRedoAction> _undoStack = new Stack<IUndoRedoAction>();
            private readonly Stack<IUndoRedoAction> _redoStack = new Stack<IUndoRedoAction>();
            private int _suspendDepth = 0;

            public bool IsRecording => _suspendDepth == 0;

            // 대량 작업(주석 로드 등) 동안 히스토리 기록을 중단하기 위한 스코프 객체 반환
            public IDisposable PauseRecording()
            {
                _suspendDepth++;
                return new RecordingScope(this);
            }

            // 새 작업을 Undo 스택에 추가. 일시 중단 중이면 무시.
            public void Push(IUndoRedoAction action)
            {
                if (!IsRecording || action == null)
                    return;

                _undoStack.Push(action);
                _redoStack.Clear();
            }

            public bool Undo(MainWindow window)
            {
                if (_undoStack.Count == 0)
                    return false;

                var action = _undoStack.Pop();
                using (PauseRecording())
                {
                    action.Undo(window);
                }

                _redoStack.Push(action);
                return true;
            }

            public bool Redo(MainWindow window)
            {
                if (_redoStack.Count == 0)
                    return false;

                var action = _redoStack.Pop();
                using (PauseRecording())
                {
                    action.Redo(window);
                }

                _undoStack.Push(action);
                return true;
            }

            public void Clear()
            {
                _undoStack.Clear();
                _redoStack.Clear();
            }

            private void ResumeRecording()
            {
                if (_suspendDepth > 0)
                    _suspendDepth--;
            }

            private sealed class RecordingScope : IDisposable
            {
                private readonly UndoRedoManager _owner;
                private bool _disposed;

                public RecordingScope(UndoRedoManager owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    _owner.ResumeRecording();
                }
            }
        }

        private interface IUndoRedoAction
        {
            void Undo(MainWindow window);
            void Redo(MainWindow window);
        }

        private class StrokeAddedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly Guid _strokeId;
            private readonly Stroke _snapshot;

            public StrokeAddedAction(int pageIndex, Guid strokeId, Stroke snapshot)
            {
                _pageIndex = pageIndex;
                _strokeId = strokeId;
                _snapshot = snapshot;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                if (canvas == null)
                {
                    Debug.WriteLine($"StrokeAddedAction.Undo: canvas null for page={_pageIndex}");
                    return;
                }

                bool removed = window.TryRemoveStrokeById(canvas, _strokeId);
                Debug.WriteLine($"StrokeAddedAction.Undo: page={_pageIndex}, removed={removed}, strokeId={_strokeId}");
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                if (canvas == null)
                {
                    Debug.WriteLine($"StrokeAddedAction.Redo: canvas null for page={_pageIndex}");
                    return;
                }

                if (_snapshot != null)
                {
                    var clone = _snapshot.Clone();
                    window.EnsureStrokeId(clone);
                    canvas.Strokes.Add(clone);
                }
                Debug.WriteLine($"StrokeAddedAction.Redo: page={_pageIndex}, strokeId={_strokeId}");
            }
        }

        private class StrokeCollectionChangedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly List<Guid> _removedIds;
            private readonly List<Stroke> _removedSnapshots;
            private readonly List<Guid> _addedIds;
            private readonly List<Stroke> _addedSnapshots;

            public StrokeCollectionChangedAction(int pageIndex, List<Guid> removedIds, List<Stroke> removedSnapshots,
                List<Guid> addedIds, List<Stroke> addedSnapshots)
            {
                _pageIndex = pageIndex;
                _removedIds = removedIds ?? new List<Guid>();
                _removedSnapshots = CloneStrokeList(removedSnapshots);
                _addedIds = addedIds ?? new List<Guid>();
                _addedSnapshots = CloneStrokeList(addedSnapshots);
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                if (canvas == null)
                {
                    Debug.WriteLine($"StrokeCollectionChangedAction.Undo: canvas null for page={_pageIndex}");
                    return;
                }

                Debug.WriteLine($"StrokeCollectionChangedAction.Undo: page={_pageIndex}, removeAdded={_addedIds.Count}, addRemoved={_removedSnapshots.Count}");

                foreach (var id in _addedIds)
                {
                    window.TryRemoveStrokeById(canvas, id);
                }

                foreach (var stroke in _removedSnapshots)
                {
                    var clone = stroke?.Clone();
                    if (clone != null)
                    {
                        window.EnsureStrokeId(clone);
                        canvas.Strokes.Add(clone);
                    }
                }
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                if (canvas == null)
                {
                    Debug.WriteLine($"StrokeCollectionChangedAction.Redo: canvas null for page={_pageIndex}");
                    return;
                }

                Debug.WriteLine($"StrokeCollectionChangedAction.Redo: page={_pageIndex}, removeRemoved={_removedIds.Count}, addAdded={_addedSnapshots.Count}");

                foreach (var id in _removedIds)
                {
                    window.TryRemoveStrokeById(canvas, id);
                }

                foreach (var stroke in _addedSnapshots)
                {
                    var clone = stroke?.Clone();
                    if (clone != null)
                    {
                        window.EnsureStrokeId(clone);
                        canvas.Strokes.Add(clone);
                    }
                }
            }

            private static List<Stroke> CloneStrokeList(IEnumerable<Stroke> strokes)
            {
                var list = new List<Stroke>();
                if (strokes == null)
                    return list;

                foreach (var stroke in strokes)
                {
                    var clone = stroke?.Clone();
                    if (clone != null)
                        list.Add(clone);
                }
                return list;
            }
        }

        private class ShapeAddedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly UIElement _element;

            public ShapeAddedAction(int pageIndex, UIElement element)
            {
                _pageIndex = pageIndex;
                _element = element;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.RemoveShapeFromCanvas(canvas, _element);
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.AddShapeToCanvas(canvas, _element);
            }
        }

        private class ShapeRemovedAction : IUndoRedoAction
        {
            private readonly int _pageIndex;
            private readonly UIElement _element;

            public ShapeRemovedAction(int pageIndex, UIElement element)
            {
                _pageIndex = pageIndex;
                _element = element;
            }

            public void Undo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.AddShapeToCanvas(canvas, _element);
            }

            public void Redo(MainWindow window)
            {
                var canvas = window.GetInkCanvas(_pageIndex);
                window.RemoveShapeFromCanvas(canvas, _element);
            }
        }

        private class TextEditedAction : IUndoRedoAction
        {
            private readonly TextBox _textBox;
            private readonly string _oldText;
            private readonly string _newText;

            public TextEditedAction(int pageIndex, TextBox textBox, string oldText, string newText)
            {
                _textBox = textBox;
                _oldText = oldText ?? string.Empty;
                _newText = newText ?? string.Empty;
            }

            public void Undo(MainWindow window)
            {
                window?.ApplyTextContent(_textBox, _oldText);
            }

            public void Redo(MainWindow window)
            {
                window?.ApplyTextContent(_textBox, _newText);
            }
        }

        private class TextStyleChangedAction : IUndoRedoAction
        {
            private readonly TextBox _textBox;
            private readonly object _oldValue;
            private readonly object _newValue;
            private readonly Action<TextBox, object> _setter;

            public TextStyleChangedAction(int pageIndex, TextBox textBox, object oldValue, object newValue, Action<TextBox, object> setter)
            {
                _textBox = textBox;
                _setter = setter;
                _oldValue = CloneValue(oldValue);
                _newValue = CloneValue(newValue);
            }

            private static object CloneValue(object value)
            {
                if (value is Brush brush)
                {
                    var clone = brush.CloneCurrentValue();
                    if (clone.CanFreeze) clone.Freeze();
                    return clone;
                }

                return value;
            }

            public void Undo(MainWindow window)
            {
                window?.ApplyTextStyle(_textBox, _setter, _oldValue);
            }

            public void Redo(MainWindow window)
            {
                window?.ApplyTextStyle(_textBox, _setter, _newValue);
            }
        }

        private class CompositeAction : IUndoRedoAction
        {
            private readonly List<IUndoRedoAction> _actions;

            public CompositeAction(IEnumerable<IUndoRedoAction> actions)
            {
                _actions = actions != null ? new List<IUndoRedoAction>(actions) : new List<IUndoRedoAction>();
            }

            public void Undo(MainWindow window)
            {
                if (window == null) return;
                for (int i = _actions.Count - 1; i >= 0; i--)
                {
                    _actions[i].Undo(window);
                }
            }

            public void Redo(MainWindow window)
            {
                if (window == null) return;
                foreach (var action in _actions)
                {
                    action.Redo(window);
                }
            }
        }
    }
}
