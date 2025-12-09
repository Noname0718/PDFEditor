using Microsoft.Win32;
using PDFEditor.Ink;
using PDFEditor.Shapes;
using PDFEditor.Text;
using PdfiumViewer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Xml;
using System.Xml.Linq;

namespace PDFEditor
{
    /// <summary>
    /// 메인 창은 다음 역할을 담당한다.
    ///  1) PdfiumViewer로 PDF를 페이지 단위로 렌더링하고 InkCanvas를 페이지별로 구성
    ///  2) 펜/도형/텍스트 등의 도구 선택과 상태를 공유하고 히스토리를 기록
    ///  3) 주석 저장/불러오기/내보내기 및 줌/페이지 전환과 같은 UI 상호작용 관리
    /// 각 기능은 별도의 *Manager* 클래스(TextToolManager 등)에서 실제 동작을 수행하고,
    /// MainWindow는 서로 다른 도구가 동시에 충돌하지 않도록 조율하는 허브 역할을 한다.
    /// </summary>
    public partial class MainWindow : Window
    {
        // ----------------------------------------------------------------------------------------------------
        //  PDF/페이지 상태 관리 필드
        // ----------------------------------------------------------------------------------------------------
        private PdfDocument _pdf;                                  // 현재 열려 있는 PDF 문서 핸들 (PdfiumViewer)
        private int _currentPage = 0;                              // 현재 보여지는 페이지 (0-based index)
        private double _baseScale = 1.0;                           // 폭 맞춤 계산 시 기준 배율
        private bool _fitWidthReady = false;                      // _baseScale 계산이 완료되었는지 여부
        private double _pageImageWidth = 0;                       // 첫 페이지의 렌더링 폭 (줌 기준)
        private readonly Dictionary<int, InkCanvas> _pageInkCanvases = new Dictionary<int, InkCanvas>(); // 페이지별 InkCanvas 참조
        private readonly Dictionary<InkCanvas, int> _inkCanvasPageIndex = new Dictionary<InkCanvas, int>(); // InkCanvas→페이지 매핑
        private readonly List<System.Drawing.SizeF> _pageSizesPoints = new List<System.Drawing.SizeF>(); // PDF 좌표계 크기(72dpi)
        private string _currentPdfPath;                            // 현재 열려 있는 PDF 경로 (주석 저장 시 기본값)
        private string _currentAnnotationPath;                     // 마지막으로 저장/불러온 주석 파일 경로

        // ----------------------------------------------------------------------------------------------------
        //  도구/커서/텍스트 상태 관리 필드
        // ----------------------------------------------------------------------------------------------------
        private readonly InkToolManager _inkTool = new InkToolManager();            // 펜/형광펜/지우개
        private readonly ShapeToolManager _shapeTool = new ShapeToolManager();      // 도형 생성 및 삭제
        private readonly SelectionToolManager _selectionTool = new SelectionToolManager(); // InkCanvas 선택
        private readonly TextToolManager _textTool = new TextToolManager();         // 텍스트 박스 생성/편집
        private readonly PenCursorManager _penCursorManager = new PenCursorManager();       // 펜 커서 미리보기
        private readonly AreaEraserManager _areaEraserManager = new AreaEraserManager();   // 도형/텍스트 일괄 지우개
        private double _textDefaultFontSize = 18;                    // 새 텍스트 박스 기본 폰트 크기
        private Color _textDefaultFontColor = Colors.Black;          // 새 텍스트 박스 기본 글자색
        private const double MinTextFontSize = 8;                    // UI에서 허용하는 글꼴 최소값
        private const double MaxTextFontSize = 96;                   // UI에서 허용하는 글꼴 최대값
        private const double TextFontStep = 2;                       // 버튼 조절 시 증가/감소량
        private static readonly Guid StrokeIdProperty = new Guid("F1F5A601-6CF2-4A0F-9BD2-1A96D4BB3F25"); // Stroke GUID 저장 키

        // ----------------------------------------------------------------------------------------------------
        //  Undo/Redo 등 기록 장치
        // ----------------------------------------------------------------------------------------------------
        private readonly Stack<IUndoRedoAction> _undoStack = new Stack<IUndoRedoAction>();
        private readonly Stack<IUndoRedoAction> _redoStack = new Stack<IUndoRedoAction>();
        private bool _recordHistory = true;                         // 대량 작업 시 히스토리 중단 플래그
        private class CopiedShapeInfo
        {
            public string Xaml;
            public double Left;
            public double Top;
        }

        private List<CopiedShapeInfo> _copiedShapes = new List<CopiedShapeInfo>();
        private List<Stroke> _copiedStrokes = new List<Stroke>();
        /// <summary>
        /// 생성자에서 창 이벤트와 각 도구 매니저의 콜백을 모두 연결한다.
        ///  - SizeChanged/PreviewKeyDown 등 윈도우 레벨 이벤트
        ///  - 각 매니저(TextToolManager 등)의 이벤트를 MainWindow 핸들러에 연결
        ///  - 기본 펜 두께/커서 상태/텍스트 스타일 초기화
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            this.SizeChanged += MainWindow_SizeChanged;
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            PdfScrollViewer.PreviewMouseWheel += PdfScrollViewer_PreviewMouseWheel;
            _shapeTool.ShapeCreated += ShapeTool_ShapeCreated;
            _shapeTool.ShapeRemoved += ShapeTool_ShapeRemoved;
            _selectionTool.TextElementDoubleClicked += SelectionTool_TextElementDoubleClicked;
            _penCursorManager.SetThickness(ThicknessSlider?.Value ?? 3.0);
            _penCursorManager.SetEnabled(true);
            _textTool.TextBoxCreated += TextTool_TextBoxCreated;
            _textTool.TextCommitted += TextTool_TextCommitted;
            _textTool.TextBoxRemoved += TextTool_TextBoxRemoved;
            _textTool.ActiveTextBoxChanged += TextTool_ActiveTextBoxChanged;
            _textTool.EditingStateChanged += TextTool_EditingStateChanged;
            _areaEraserManager.ElementErased += AreaEraser_ElementErased;
            _areaEraserManager.SetRadius(ThicknessSlider?.Value ?? 3.0);
            InitializeTextToolDefaults();
            UpdateShapeToolAppearance(GetSelectedDrawingColor(), GetSelectedThickness());
        }

        /// <summary>
        /// 창 크기가 바뀌면 현재 페이지 위치가 틀어질 수 있으므로 다시 스크롤.
        /// </summary>
        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_pdf == null) return;
            ScrollToPage(_currentPage);
        }
        /// <summary>
        /// PDF 파일 선택 후 PdfiumViewer로 로드하고 UI 초기화.
        /// </summary>
        private void OpenPdf_Click(object sender, RoutedEventArgs e)
        {
            ShowOpenPdfDialog();
        }

        /// <summary>
        /// 표준 OpenFileDialog를 띄워 PDF를 선택한 뒤 LoadPdfFromPath를 호출한다.
        /// 이 메서드는 버튼/단축키 등 다양한 UI 진입점을 공유하기 위해 분리되어 있다.
        /// </summary>
        private void ShowOpenPdfDialog()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF Files|*.pdf"
            };

            if (dlg.ShowDialog() == true)
            {
                LoadPdfFromPath(dlg.FileName);
            }
        }

        /// <summary>
        /// PDF가 열려 있을 때 현재 작업 내용을 .pdfanno 파일로 내보내기 위한 SaveFileDialog를 연다.
        /// 파일이 선택되면 실제 직렬화는 <see cref="SaveAnnotationsToFile(string)"/>가 담당한다.
        /// </summary>
        private void SaveAnnotation_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null)
            {
                MessageBox.Show("먼저 PDF를 열어주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "PDF 주석 파일|*.pdfanno",
                FileName = !string.IsNullOrWhiteSpace(_currentAnnotationPath)
                    ? System.IO.Path.GetFileName(_currentAnnotationPath)
                    : System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(_currentPdfPath) ?? "annotations", ".pdfanno")
            };

            if (dlg.ShowDialog() == true)
            {
                SaveAnnotationsToFile(dlg.FileName);
            }
        }

        /// <summary>
        /// .pdfanno 파일을 선택해 현재 PDF에 덮어쓰는 OpenFileDialog 핸들러.
        /// PDF가 닫혀 있다면 LoadAnnotationsFromFile 내부에서 오류 메시지를 안내한다.
        /// </summary>
        private void LoadAnnotation_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF 주석 파일|*.pdfanno"
            };

            if (dlg.ShowDialog() == true)
            {
                LoadAnnotationsFromFile(dlg.FileName);
            }
        }

        /// <summary>
        /// 사용자가 선택한 경로를 검증하고 PdfiumViewer로 문서를 로드한 뒤
        /// 페이지 렌더링/도구 상태 초기화/폭 맞춤 계산까지 한 번에 처리한다.
        /// </summary>
        private void LoadPdfFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show("PDF 파일을 찾을 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                _pdf?.Dispose();
                _pdf = PdfDocument.Load(path);
                _currentPdfPath = path;
                _currentAnnotationPath = null;
                _currentPage = 0;

                RenderAllPages();
                UpdatePageInfo();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FitWidthToViewer();
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                string defaultAnnotation = System.IO.Path.ChangeExtension(path, ".pdfanno");
                if (File.Exists(defaultAnnotation))
                {
                    LoadAnnotationsFromFile(defaultAnnotation, suppressPdfReload: true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("PDF를 열 수 없습니다: " + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 프로젝트 전반에서 공유하는 "전체 지우기" 버튼 핸들러.
        /// 모든 페이지의 InkCanvas에서 직렬화 가능한 요소(Text/Shape)와 스트로크를 제거하고
        /// Undo/Redo 스택까지 비워 일관된 상태를 유지한다.
        /// </summary>
        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null || _pageInkCanvases.Count == 0)
            {
                MessageBox.Show("지울 주석이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show("모든 페이지의 필기/도형/텍스트를 삭제할까요?", "전체 지우기",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            _recordHistory = false;
            try
            {
                foreach (var canvas in _pageInkCanvases.Values)
                {
                    canvas.Strokes.Clear();

                    var removable = canvas.Children
                        .OfType<UIElement>()
                        .Where(IsSerializableElement)
                        .ToList();

                    foreach (var element in removable)
                    {
                        canvas.Children.Remove(element);
                        _textTool.NotifyElementRemoved(element);
                    }
                }
                ClearHistory();
            }
            finally
            {
                _recordHistory = true;
            }
        }

        /// <summary>
        /// 현재 페이지 컬렉션을 이미지로 Flatten해 새 PDF로 저장하는 버튼 핸들러.
        /// 내부적으로 <see cref="CaptureAnnotatedPages"/>로 화면 이미지를 가져와 <see cref="WriteFlattenedPdf"/>로 출력한다.
        /// </summary>
        private void ExportAnnotatedPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null)
            {
                MessageBox.Show("먼저 PDF를 열어주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pages = CaptureAnnotatedPages();
            if (pages.Count == 0)
            {
                MessageBox.Show("내보낼 페이지가 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "PDF Files|*.pdf",
                FileName = System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(_currentPdfPath) ?? "annotated", ".annotated.pdf")
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    WriteFlattenedPdf(dlg.FileName, pages);
                    MessageBox.Show("주석이 포함된 PDF를 저장했습니다.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("PDF 내보내기에 실패했습니다: " + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 화면에 표시된 InkCanvas와 백그라운드 이미지를 합성해 페이지별 비트맵을 만든다.
        /// 1) PdfiumViewer로 원본 페이지를 렌더링하고
        /// 2) 도형/텍스트/잉크가 포함된 가상 Grid에 붙인 뒤
        /// 3) RenderTargetBitmap으로 픽셀 데이터를 추출하여 JPEG 스트림으로 변환한다.
        /// </summary>
        private List<AnnotatedPageImage> CaptureAnnotatedPages()
        {
            var images = new List<AnnotatedPageImage>();
            if (_pdf == null)
                return images;

            double renderDpi = 200; // balance quality vs size
            for (int pageIndex = 0; pageIndex < _pdf.PageCount; pageIndex++)
            {
                var baseImage = RenderPdfPageImage(pageIndex, (int)renderDpi);
                if (baseImage == null)
                    continue;

                System.Drawing.SizeF sizePoints;
                if (pageIndex < _pageSizesPoints.Count)
                {
                    sizePoints = _pageSizesPoints[pageIndex];
                }
                else
                {
                    double dpiX = baseImage.DpiX <= 0 ? 96.0 : baseImage.DpiX;
                    double dpiY = baseImage.DpiY <= 0 ? 96.0 : baseImage.DpiY;
                    sizePoints = new System.Drawing.SizeF((float)(baseImage.PixelWidth / dpiX * 72.0), (float)(baseImage.PixelHeight / dpiY * 72.0));
                }
                double width = sizePoints.Width / 72.0 * 96.0;
                double height = sizePoints.Height / 72.0 * 96.0;

                var container = new Grid
                {
                    Width = width,
                    Height = height,
                    Background = Brushes.White
                };
                container.Children.Add(new Image
                {
                    Source = baseImage,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill
                });

                if (_pageInkCanvases.TryGetValue(pageIndex, out InkCanvas originalCanvas))
                {
                    var cloneCanvas = CloneCanvasForExport(originalCanvas);
                    var viewbox = new Viewbox
                    {
                        Width = width,
                        Height = height,
                        Stretch = Stretch.Fill,
                        Child = cloneCanvas
                    };
                    container.Children.Add(viewbox);
                }

                // Grid는 Viewbox 포함 자식들을 모두 렌더링해야 하므로 Arrange/Measure를 직접 호출한다.
                container.Measure(new Size(width, height));
                container.Arrange(new Rect(0, 0, width, height));
                container.UpdateLayout();

                double dpi = renderDpi;
                int pixelWidth = (int)Math.Ceiling(width / 96.0 * dpi);
                int pixelHeight = (int)Math.Ceiling(height / 96.0 * dpi);
                if (pixelWidth <= 0 || pixelHeight <= 0)
                    continue;

                var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
                rtb.Render(container);

                byte[] data;
                var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    data = ms.ToArray();
                }

                double widthPoints = sizePoints.Width;
                double heightPoints = sizePoints.Height;
                images.Add(new AnnotatedPageImage
                {
                    PixelWidth = pixelWidth,
                    PixelHeight = pixelHeight,
                    WidthPoints = widthPoints,
                    HeightPoints = heightPoints,
                    ImageBytes = data
                });
            }

            return images;
        }

        /// <summary>
        /// PdfiumViewer가 반환한 GDI+ 이미지(Stream) 를 WPF BitmapSource로 변환한다.
        /// 내보내기에서 여러 dpi 값을 사용할 수 있으므로 매 호출마다 dpi를 전달받는다.
        /// </summary>
        private BitmapSource RenderPdfPageImage(int pageIndex, int dpi)
        {
            if (_pdf == null) return null;
            using (var img = _pdf.Render(pageIndex, dpi, dpi, true))
            {
                return ImageToImageSource(img);
            }
        }

        /// <summary>
        /// CaptureAnnotatedPages에서 얻은 이미지를 순수 PDF 명세로 직접 작성한다.
        /// 외부 PDF 라이브러리를 다시 참조하지 않고 Catalog/Pages/Page 객체와 이미지 스트림을 수동으로 기록한다.
        /// </summary>
        private void WriteFlattenedPdf(string path, List<AnnotatedPageImage> pages)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var writer = new StreamWriter(fs, Encoding.ASCII);
                writer.WriteLine("%PDF-1.4");
                writer.Flush();

                var offsets = new List<long>();
                int nextObj = 1;
                int catalogId = nextObj++;
                int pagesId = nextObj++;
                var pageInfos = new List<(AnnotatedPageImage Page, int ImageId, int ContentId, int PageId, string ImageName)>();

                for (int i = 0; i < pages.Count; i++)
                {
                    pageInfos.Add((pages[i], nextObj++, nextObj++, nextObj++, $"Im{i + 1}"));
                }

                void WriteObject(Action<BinaryWriter> bodyWriter)
                {
                    offsets.Add(fs.Position);
                    using (var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true))
                    {
                        bodyWriter(bw);
                        bw.Flush();
                    }
                }

                var culture = CultureInfo.InvariantCulture;

                // Image objects
                foreach (var info in pageInfos)
                {
                    var page = info.Page;
                    WriteObject(bw =>
                    {
                        bw.Write(Encoding.ASCII.GetBytes($"{info.ImageId} 0 obj\n"));
                        bw.Write(Encoding.ASCII.GetBytes($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.ImageBytes.Length} >>\n"));
                        bw.Write(Encoding.ASCII.GetBytes("stream\n"));
                        bw.Write(page.ImageBytes);
                        bw.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
                    });
                }

                // Content streams
                foreach (var info in pageInfos)
                {
                    var page = info.Page;
                    string content = string.Format(culture, "q {0:0.###} 0 0 {1:0.###} 0 0 cm /{2} Do Q\n", page.WidthPoints, page.HeightPoints, info.ImageName);
                    byte[] contentBytes = Encoding.ASCII.GetBytes(content);
                    WriteObject(bw =>
                    {
                        bw.Write(Encoding.ASCII.GetBytes($"{info.ContentId} 0 obj\n"));
                        bw.Write(Encoding.ASCII.GetBytes($"<< /Length {contentBytes.Length} >>\nstream\n"));
                        bw.Write(contentBytes);
                        bw.Write(Encoding.ASCII.GetBytes("endstream\nendobj\n"));
                    });
                }

                // Page objects
                foreach (var info in pageInfos)
                {
                    var page = info.Page;
                    string pageObj = string.Format(culture,
                        "{0} 0 obj\n<< /Type /Page /Parent {1} 0 R /MediaBox [0 0 {2:0.###} {3:0.###}] /Resources << /XObject << /{4} {5} 0 R >> >> /Contents {6} 0 R >>\nendobj\n",
                        info.PageId, pagesId, page.WidthPoints, page.HeightPoints, info.ImageName, info.ImageId, info.ContentId);
                    WriteObject(bw => bw.Write(Encoding.ASCII.GetBytes(pageObj)));
                }

                // Pages object
                string kids = string.Join(" ", pageInfos.Select(p => $"{p.PageId} 0 R"));
                WriteObject(bw =>
                {
                    string body = $"{pagesId} 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageInfos.Count} >>\nendobj\n";
                    bw.Write(Encoding.ASCII.GetBytes(body));
                });

                // Catalog object
                WriteObject(bw =>
                {
                    string body = $"{catalogId} 0 obj\n<< /Type /Catalog /Pages {pagesId} 0 R >>\nendobj\n";
                    bw.Write(Encoding.ASCII.GetBytes(body));
                });

                long xrefPosition = fs.Position;
                using (var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true))
                {
                    bw.Write(Encoding.ASCII.GetBytes($"xref\n0 {nextObj}\n"));
                    bw.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
                    foreach (var offset in offsets)
                    {
                        bw.Write(Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0:0000000000} 00000 n \n", offset)));
                    }
                    bw.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {nextObj} /Root {catalogId} 0 R >>\nstartxref\n{xrefPosition}\n%%EOF"));
                }
            }
        }

        /// <summary>
        /// 현재까지 각 페이지에 누적된 잉크/도형/텍스트 정보를 XML 구조로 직렬화해 저장한다.
        ///  - InkCanvas.Strokes는 StrokeCollection을 MemoryStream에 저장 후 Base64로 저장
        ///  - InkCanvas.Children 중 TextBox/Shape만 XAML 그대로 보관
        /// </summary>
        private void SaveAnnotationsToFile(string path)
        {
            try
            {
                var root = new XElement("PdfAnnotations",
                    new XAttribute("Version", "1"),
                    new XElement("PdfPath", _currentPdfPath ?? string.Empty));

                var pagesElement = new XElement("Pages");

                foreach (var kvp in _pageInkCanvases)
                {
                    int pageIndex = kvp.Key;
                    InkCanvas canvas = kvp.Value;
                    var pageElement = new XElement("Page", new XAttribute("Index", pageIndex));

                    string strokes = SerializeStrokes(canvas.Strokes);
                    if (!string.IsNullOrEmpty(strokes))
                    {
                        pageElement.Add(new XElement("Strokes", strokes));
                    }

                    var elementsElement = SerializeElements(canvas);
                    if (elementsElement != null)
                    {
                        pageElement.Add(elementsElement);
                    }

                    if (pageElement.HasElements)
                    {
                        pagesElement.Add(pageElement);
                    }
                }

                root.Add(pagesElement);
                var doc = new XDocument(root);
                doc.Save(path);
                _currentAnnotationPath = path;
                MessageBox.Show("주석을 저장했습니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("주석을 저장할 수 없습니다: " + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// .pdfanno 파일을 읽어 InkCanvas별로 잉크/도형/텍스트를 복원한다.
        /// suppressPdfReload=true이면 PDF 본문은 그대로 두고 현재 페이지 구조에만 주석을 덮어쓴다.
        /// </summary>
        private void LoadAnnotationsFromFile(string path, bool suppressPdfReload = false)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show("주석 파일을 찾을 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null)
                    throw new InvalidDataException("잘못된 주석 파일입니다.");

                string pdfPath = root.Element("PdfPath")?.Value;
                if (!suppressPdfReload && !string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath))
                {
                    LoadPdfFromPath(pdfPath);
                }
                else if (_pdf == null)
                {
                    MessageBox.Show("주석과 연결된 PDF를 먼저 열어야 합니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var pageNodes = root.Element("Pages")?.Elements("Page");
                if (pageNodes == null)
                {
                    MessageBox.Show("주석 파일에 저장된 내용이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _currentAnnotationPath = path;
                _recordHistory = false;
                try
                {
                    foreach (var pageNode in pageNodes)
                    {
                        int pageIndex = (int?)pageNode.Attribute("Index") ?? -1;
                        if (!_pageInkCanvases.TryGetValue(pageIndex, out InkCanvas canvas))
                            continue;

                        string strokesData = pageNode.Element("Strokes")?.Value;
                        var strokes = DeserializeStrokes(strokesData);
                        if (strokes != null)
                        {
                            foreach (var stroke in strokes)
                            {
                                canvas.Strokes.Add(stroke);
                                EnsureStrokeId(stroke);
                            }
                        }

                        var elementsNode = pageNode.Element("Elements");
                        if (elementsNode != null)
                        {
                            foreach (var elementNode in elementsNode.Elements("Element"))
                            {
                                string xaml = elementNode.Value;
                                var element = DeserializeElement(xaml);
                                if (element == null)
                                    continue;
                                canvas.Children.Add(element);
                                if (element is TextBox textBox)
                                {
                                    _textTool.RegisterExistingTextBox(canvas, textBox);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _recordHistory = true;
                }

                MessageBox.Show("주석을 불러왔습니다.", "불러오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("주석을 불러올 수 없습니다: " + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// InkCanvas의 StrokeCollection을 메모리 스트림에 쓰고 Base64 문자열로 반환한다.
        /// 빈 컬렉션이면 null을 반환하여 XML 문서가 불필요하게 커지는 것을 방지한다.
        /// </summary>
        private string SerializeStrokes(StrokeCollection strokes)
        {
            if (strokes == null || strokes.Count == 0)
                return null;

            using (var ms = new MemoryStream())
            {
                strokes.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>
        /// Base64 문자열을 StrokeCollection으로 되돌린다.
        /// 저장된 내용이 없으면 null, 데이터가 손상되면 예외를 호출자에게 전달한다.
        /// </summary>
        private StrokeCollection DeserializeStrokes(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return null;

            byte[] bytes = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(bytes))
            {
                return new StrokeCollection(ms);
            }
        }

        /// <summary>
        /// InkCanvas.Children 중에서 직렬화 대상(TextBox/Shape)만 골라 XAML을 CDATA로 포함시킨다.
        /// 비어 있는 경우 null을 반환해 상위 XML에서 엘리먼트를 생략한다.
        /// </summary>
        private XElement SerializeElements(InkCanvas canvas)
        {
            var elements = new XElement("Elements");
            foreach (UIElement child in canvas.Children)
            {
                if (!IsSerializableElement(child))
                    continue;
                string xaml = XamlWriter.Save(child);
                elements.Add(new XElement("Element", new XCData(xaml)));
            }
            return elements.HasElements ? elements : null;
        }

        /// <summary>
        /// XAML 문자열을 다시 UIElement 인스턴스로 복원한다.
        /// TextBox/Shape 외에는 저장하지 않으므로 해당 타입으로 다시 생성되는 것이 보장된다.
        /// </summary>
        private UIElement DeserializeElement(string xaml)
        {
            if (string.IsNullOrWhiteSpace(xaml))
                return null;

            using (var stringReader = new StringReader(xaml))
            using (var xmlReader = XmlReader.Create(stringReader))
            {
                return XamlReader.Load(xmlReader) as UIElement;
            }
        }

        /// <summary>
        /// InkCanvas 자식 중 Text/Shape인지 검사하여 직렬화 여부를 결정한다.
        /// 태그 값으로 도구에서 생성한 요소만 걸러내고, 페이지 프레임/스크롤 보조 요소는 제외한다.
        /// </summary>
        private bool IsSerializableElement(UIElement element)
        {
            if (element is TextBox textBox)
            {
                return Equals(textBox.Tag as string, TextToolManager.TextElementTag);
            }

            if (element is Shape shape)
            {
                return Equals(shape.Tag, ShapeToolManager.ShapeElementTag);
            }

            return false;
        }

        /// <summary>
        /// PdfiumViewer가 제공하는 페이지 이미지를 반복 렌더링하여
        ///  - 페이지별 Image + InkCanvas + Border 구조를 만들고
        ///  - 각 InkCanvas를 도구 매니저에 등록한 뒤 Dictionaries에 저장한다.
        /// 이 과정에서 줌 기준(_pageImageWidth)과 PDF 좌표 크기(_pageSizesPoints)도 함께 수집한다.
        /// </summary>
        private void RenderAllPages()
        {
            PagesPanel.Children.Clear();
            _pageInkCanvases.Clear();
            _penCursorManager.Clear();
            _inkCanvasPageIndex.Clear();
            _textTool.Clear();
            _pageSizesPoints.Clear();
            ClearHistory();
            _copiedShapes.Clear();
            _copiedStrokes.Clear();

            // 줌 초기화
            ZoomTransform.ScaleX = 1.0;
            ZoomTransform.ScaleY = 1.0;
            _baseScale = 1.0;
            _fitWidthReady = false;

            if (_pdf == null) return;

            int pageCount = _pdf.PageCount;
            float dpi = 300f;   // PPT 글씨까지 선명하게 보이도록

            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                using (var img = _pdf.Render(pageIndex, dpi, dpi, false))
                {
                    var bitmapSource = ImageToImageSource(img);

                    // 첫 페이지 이미지 폭 저장
                    if (pageIndex == 0)
                    {
                        _pageImageWidth = bitmapSource.PixelWidth;
                    }

                    System.Drawing.SizeF pageSizePoints;
                    if (_pdf.PageSizes != null && pageIndex < _pdf.PageSizes.Count)
                    {
                        pageSizePoints = _pdf.PageSizes[pageIndex];
                    }
                    else
                    {
                        pageSizePoints = new System.Drawing.SizeF(
                            (float)(bitmapSource.Width / 96.0 * 72.0),
                            (float)(bitmapSource.Height / 96.0 * 72.0));
                    }
                    _pageSizesPoints.Add(pageSizePoints);

                    double pageWidth = bitmapSource.Width;
                    double pageHeight = bitmapSource.Height;

                    var wpfImage = new Image
                    {
                        Source = bitmapSource,
                        Width = pageWidth,
                        Height = pageHeight,
                        Stretch = Stretch.Fill,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    RenderOptions.SetBitmapScalingMode(wpfImage, BitmapScalingMode.HighQuality);

                    var ink = new InkCanvas
                    {
                        Background = Brushes.Transparent,
                        Width = pageWidth,
                        Height = pageHeight,
                        ClipToBounds = true //필기 밖으로 나가는 부분 자르기
                    };
                    AttachCanvasManagers(ink); // 입력 이벤트/툴 매니저 초기화

                    var pageGrid = new Grid 
                    {
                        Width = pageWidth,
                        Height = pageHeight,
                        ClipToBounds = true
                    };
                    pageGrid.Children.Add(wpfImage);
                    pageGrid.Children.Add(ink);

                    var pageBorder = new Border
                    {
                        Width = pageWidth,
                        Height = pageHeight,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    if (TryFindResource("PdfPageCardStyle") is Style cardStyle)
                    {
                        pageBorder.Style = cardStyle;
                    }
                    else
                    {
                        pageBorder.Background = Brushes.White;
                        pageBorder.BorderBrush = Brushes.LightGray;
                        pageBorder.BorderThickness = new Thickness(0.5);
                        pageBorder.Margin = new Thickness(0, 0, 0, 10);
                    }
                    pageBorder.RequestBringIntoView += SuppressBringIntoView;

            pageBorder.Child = pageGrid;

            PagesPanel.Children.Add(pageBorder);

                    _pageInkCanvases[pageIndex] = ink;
                    _inkCanvasPageIndex[ink] = pageIndex;
                }
            }
            _penCursorManager.SetEnabled(true);

            ScrollToPage(0);
        }

        /// <summary>
        /// PdfiumViewer가 제공하는 System.Drawing.Image를 BitmapImage로 변환한다.
        /// InkCanvas 위에 덮을 Image 컨트롤은 WPF BitmapSource만 지원하므로 메모리 스트림을 경유한다.
        /// </summary>
        private BitmapImage ImageToImageSource(System.Drawing.Image image)
        {
            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                return bitmapImage;
            }
        }

        /// <summary>
        /// 현재 페이지 표시 텍스트 업데이트 (ex: 3 / 12)
        /// </summary>
        private void UpdatePageInfo()
        {
            if (_pdf == null)
            {
                PageInfoText.Text = "";
                return;
            }

            PageInfoText.Text = $"{_currentPage + 1} / {_pdf.PageCount}";
            PageInput.Text = (_currentPage + 1).ToString();
        }

        /// <summary>
        /// 특정 페이지 인덱스를 현재 페이지로 설정하고 페이지 정보 및 InkCanvas 상태를 갱신한다.
        /// PdfScrollViewer의 실제 스크롤 이동은 Border.RequestBringIntoView 억제 로직으로 제어한다.
        /// </summary>
        private void ScrollToPage(int pageIndex)
        {
            if (_pdf == null) return;
            if (pageIndex < 0 || pageIndex >= _pdf.PageCount) return;

            _currentPage = pageIndex;
            UpdatePageInfo();

            // 페이지가 바뀌면 InkCanvas 레퍼런스도 달라지므로 현재 선택된 도구를 다시 적용한다.
            _inkTool.SetTool(_inkTool.CurrentTool, GetCurrentInkCanvas());

            ScrollCurrentPageIntoView();
        }

        /// <summary>
        /// 다음 페이지 버튼 클릭 시 한 페이지 앞으로 이동한다.
        /// 실제 스크롤 이동 대신 페이지 인덱스 및 InkCanvas 상태만 갱신한다.
        /// </summary>
        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;
            ScrollToPage(_currentPage + 1);
        }

        /// <summary>
        /// 이전 페이지 버튼 클릭 시 한 페이지 뒤로 이동한다.
        /// </summary>
        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;
            ScrollToPage(_currentPage - 1);
        }

        /// <summary>
        /// 페이지 번호 입력 후 이동 버튼.
        /// 잘못된 번호면 아무 작업도 하지 않는다(추가 검증/메시지 필요 시 여기서 처리).
        /// </summary>
        private void GoToPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pdf == null) return;

            if (int.TryParse(PageInput.Text, out int pageNumber))
            {
                // 사용자는 1부터 입력하니까 0-based로 변환
                int targetIndex = pageNumber - 1;
                ScrollToPage(targetIndex);
            }
            else
            {
                MessageBox.Show("페이지 번호를 올바르게 입력하세요.", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        /// <summary>
        /// 줌 슬라이더 변경 시 ApplyZoom 호출.
        /// </summary>
        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            if (!_fitWidthReady) return;

            double percent = e.NewValue;
            ApplyZoom(percent);
        }

        /// <summary>
        /// 줌 텍스트 박스에서 Enter 입력 시 슬라이더 값 변경.
        /// </summary>
        private void ZoomTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!_fitWidthReady) return;

            if (double.TryParse(ZoomTextBox.Text, out double percent))
            {
                if (percent < ZoomSlider.Minimum) percent = ZoomSlider.Minimum;
                if (percent > ZoomSlider.Maximum) percent = ZoomSlider.Maximum;

                ZoomSlider.Value = percent;   // 슬라이더를 통해 ApplyZoom 호출
            }
        }

        /// <summary>
        /// ScrollViewer 폭에 맞춰 기준 배율(_baseScale)을 계산하고 슬라이더 초기화.
        /// </summary>
        private void FitWidthToViewer()
        {
            if (_pdf == null) return;
            if (PagesPanel.Children.Count == 0) return;
            if (_pageImageWidth <= 0) return;     // 🔴 이미지 폭이 안 잡힌 경우

            // 레이아웃 갱신
            PdfScrollViewer.UpdateLayout();
            PagesPanel.UpdateLayout();

            double viewportWidth = PdfScrollViewer.ViewportWidth;
            if (viewportWidth <= 0) return;

            // 좌우 여백(스크롤바, Border padding 등) 약간 빼줌
            double effectiveViewport = viewportWidth;
            if (effectiveViewport <= 0) return;

            const double baseZoomFactor = 1.5; //1.0 = 딱 맞음, 1.2 = 20% 확대

            // ✅ "폭에 맞추기"에 해당하는 기준 배율 = 화면폭 / 이미지폭
            _baseScale = (effectiveViewport / _pageImageWidth)*baseZoomFactor;
            if (_baseScale <= 0) _baseScale = 1.0;

            _fitWidthReady = true;

            // 슬라이더/텍스트를 100%로 초기화
            ZoomSlider.Minimum = 50;
            ZoomSlider.Maximum = 300;
            ZoomSlider.Value = 100;
            ZoomTextBox.Text = "100";

            ApplyZoom(100);   // 100% = "폭에 맞추기"
        }

        /// <summary>
        /// 줌 슬라이더 값(%)을 받아 StackPanel LayoutTransform에 반영.
        /// </summary>
        private void ApplyZoom(double percent)
        {
            if (!_fitWidthReady) return;      // 기준 배율 아직 없음
            if (ZoomTransform == null) return;

            // 100%일 때 baseScale = 폭에 맞는 크기
            double scale = _baseScale * (percent / 100.0);

            ZoomTransform.ScaleX = scale;
            ZoomTransform.ScaleY = scale;

            if (ZoomTextBox != null)
                ZoomTextBox.Text = ((int)percent).ToString();
        }

        //현재 페이지 InkCanvas 가져오기
        private InkCanvas GetCurrentInkCanvas()
        {
            if (_pageInkCanvases.TryGetValue(_currentPage, out InkCanvas inkCanvas))
            {
                return inkCanvas;
            }

            return null;
        }

        private InkCanvas GetInkCanvas(int pageIndex)
        {
            if (_pageInkCanvases.TryGetValue(pageIndex, out InkCanvas inkCanvas))
            {
                return inkCanvas;
            }
            return null;
        }

        private bool TryGetPageIndex(InkCanvas canvas, out int pageIndex)
        {
            if (canvas != null && _inkCanvasPageIndex.TryGetValue(canvas, out pageIndex))
            {
                return true;
            }
            pageIndex = -1;
            return false;
        }

        private bool TryGetPageIndex(TextBox textBox, out int pageIndex)
        {
            pageIndex = -1;
            if (textBox == null)
                return false;

            InkCanvas canvas;
            if (_textTool.TryGetOwnerCanvas(textBox, out canvas))
            {
                return TryGetPageIndex(canvas, out pageIndex);
            }

            return false;
        }
        /// <summary>
        /// 필기/도형/지우개 토글 버튼 공용 핸들러.
        /// 필기 도구 선택 시 모든 페이지 InkCanvas에 EditingMode를 일괄 적용한다.
        /// 도형 도구 선택 시 ShapeToolManager가 직접 InkCanvas 이벤트를 잡는다.
        /// </summary>
        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            var clicked = sender as ToggleButton;
            if (clicked == null) return;

            // 1) 모든 버튼 체크 해제
            if (SelectButton != null) SelectButton.IsChecked = false;
            if (PenButton != null) PenButton.IsChecked = false;
            if (HighlighterButton != null) HighlighterButton.IsChecked = false;
            if (EraserButton != null) EraserButton.IsChecked = false;
            if (RectButton != null) RectButton.IsChecked = false;
            if (EllipseButton != null) EllipseButton.IsChecked = false;
            if (LineButton != null) LineButton.IsChecked = false;
            if (TriangleButton != null) TriangleButton.IsChecked = false;
            if (TextButton != null) TextButton.IsChecked = false;

            clicked.IsChecked = true;

            string tag = clicked.Tag as string ?? "";
            bool isTextTool = tag == "Text";
            _textTool.SetActive(isTextTool);
            if (!isTextTool)
            {
                HideTextToolbar();
            }
            UpdateCursorMode(tag);
            _areaEraserManager.SetEnabled(tag == "Eraser");

            // 🔹 1) 선택 도구
            if (tag == "Select")
            {
                // 펜/도형 모드는 모두 비활성화
                _shapeTool.SetShape(ShapeType.None);
                _shapeTool.SetShapeEraseMode(false);
                _selectionTool.SetEnabled(true);
                foreach (InkCanvas canvas in _pageInkCanvases.Values)
                {
                    canvas.EditingMode = InkCanvasEditingMode.None;
                }

                // InkCanvas 필기/지우개 모두 끄기 (선택만 할 수 있게)
                return;
            }
            else
            {
                _selectionTool.SetEnabled(false);
            }

            if (isTextTool)
            {
                _shapeTool.SetShape(ShapeType.None);
                _shapeTool.SetShapeEraseMode(false);

                foreach (InkCanvas canvas in _pageInkCanvases.Values)
                {
                    canvas.EditingMode = InkCanvasEditingMode.None;
                }

                return;
            }

            // 🔹 2) 펜 / 형광펜 / 지우개
            if (tag == "Pen" || tag == "Highlighter" || tag == "Eraser")
            {
                // 도형 그리기 모드는 끄기
                _shapeTool.SetShape(ShapeType.None);

                // 도형 지우개 모드는 "지우개일 때만" 켜기
                _shapeTool.SetShapeEraseMode(tag == "Eraser");

                foreach (InkCanvas canvas in _pageInkCanvases.Values)
                {
                    if (tag == "Pen")
                        _inkTool.SetTool(DrawTool.Pen, canvas);
                    else if (tag == "Highlighter")
                        _inkTool.SetTool(DrawTool.Highlighter, canvas);
                    else if (tag == "Eraser")
                        _inkTool.SetTool(DrawTool.Eraser, canvas);
                }

                return;
            }

            // 🔹 3) 도형 도구들 (사각형/원/선/삼각형)
            foreach (InkCanvas canvas in _pageInkCanvases.Values)
            {
                // 도형 그릴 때는 InkCanvas 필기 막기
                canvas.EditingMode = InkCanvasEditingMode.None;
            }

            // 도형 선택 시에는 도형 지우개 모드 끔
            _shapeTool.SetShapeEraseMode(false);

            switch (tag)
            {
                case "Rectangle":
                    _shapeTool.SetShape(ShapeType.Rectangle);
                    break;
                case "Ellipse":
                    _shapeTool.SetShape(ShapeType.Ellipse);
                    break;
                case "Line":
                    _shapeTool.SetShape(ShapeType.Line);
                    break;
                case "Triangle":
                    _shapeTool.SetShape(ShapeType.Triangle);
                    break;
                default:
                    _shapeTool.SetShape(ShapeType.None);
                    break;
            }
        }

        /// <summary>
        /// 색상 콤보 선택 시 InkToolManager/ShapeToolManager에 현재 색상을 반영한다.
        /// </summary>
        private void ColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_inkTool == null)
                return;

            ComboBoxItem item = ColorComboBox.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null)
                return;

            string colorName = item.Tag.ToString();
            Color color = (Color)ColorConverter.ConvertFromString(colorName);

            _inkTool.SetColor(color, null);
            UpdateShapeToolAppearance(color, GetSelectedThickness());
            ApplyDrawingSettingsToAllCanvases();
        }

        private void TextColorToolbarButton_Click(object sender, RoutedEventArgs e)
        {
            if (TextColorToolbarButton?.ContextMenu == null)
                return;

            TextColorToolbarButton.ContextMenu.PlacementTarget = TextColorToolbarButton;
            TextColorToolbarButton.ContextMenu.Placement = PlacementMode.Bottom;
            TextColorToolbarButton.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        private void TextColorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag != null)
            {
                Color color = (Color)ColorConverter.ConvertFromString(item.Tag.ToString());
                ApplyTextColor(color);
            }
        }

        private void TextFontIncreaseButton_Click(object sender, RoutedEventArgs e)
        {
            AdjustActiveTextFontSize(TextFontStep);
        }

        private void TextFontDecreaseButton_Click(object sender, RoutedEventArgs e)
        {
            AdjustActiveTextFontSize(-TextFontStep);
        }

        private void AdjustActiveTextFontSize(double delta)
        {
            var active = _textTool.GetActiveTextBox();
            if (active == null)
                return;

            double current = active.FontSize;
            if (double.IsNaN(current) || current <= 0)
                current = _textDefaultFontSize;

            double newSize = current + delta;
            newSize = Math.Max(MinTextFontSize, Math.Min(MaxTextFontSize, newSize));
            ApplyFontSizeToTextBox(active, newSize);
        }

        private void ApplyFontSizeToTextBox(TextBox textBox, double size)
        {
            if (textBox == null || size <= 0)
                return;

            double previous = double.IsNaN(textBox.FontSize) ? _textDefaultFontSize : textBox.FontSize;
            if (Math.Abs(previous - size) < 0.1)
                return;

            _textDefaultFontSize = size;
            _textTool.SetDefaultFontSize(size);
            _textTool.ApplyFontSize(textBox, size);
            RegisterTextStyleChange(textBox, previous, size, (tb, value) => tb.FontSize = (double)value);
            _textTool.RefreshLayout(textBox);
            RestoreTextEditingFocus();
        }

        private void ApplyTextColor(Color color)
        {
            _textDefaultFontColor = color;
            _textTool.SetDefaultForeground(new SolidColorBrush(color));

            var active = _textTool.GetActiveTextBox();
            if (active == null)
                return;

            Brush oldBrush = CloneBrush(active.Foreground);
            _textTool.ApplyForeground(active, new SolidColorBrush(color));
            Brush newBrush = CloneBrush(active.Foreground);

            RegisterTextStyleChange(active, oldBrush, newBrush, (tb, value) => tb.Foreground = (Brush)value);
            RestoreTextEditingFocus();
        }

        private Brush CloneBrush(Brush brush)
        {
            if (brush == null)
                return null;

            var clone = brush.CloneCurrentValue();
            if (clone.CanFreeze)
                clone.Freeze();
            return clone;
        }


        private bool TryExecuteInkCanvasCommand(ICommand command)
        {
            if (command == null)
                return false;

            var canvas = GetCurrentInkCanvas();
            if (canvas == null)
                return false;

            if (command is RoutedCommand routed)
            {
                if (routed.CanExecute(null, canvas))
                {
                    routed.Execute(null, canvas);
                    return true;
                }
                return false;
            }

            if (command.CanExecute(null))
            {
                command.Execute(null);
                return true;
            }

            return false;
        }
        /// <summary>
        /// 두께 슬라이더 변경 시 펜 도구/도형 도구 두께를 동시에 갱신한다.
        /// </summary>
        private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_inkTool == null)
                return;

            double thickness = e.NewValue;

            // UI 상태를 즉시 갱신하여 새로 추가될 InkCanvas도 동일한 두께를 사용하게 한다.
            _inkTool.SetThickness(thickness, null);
            _penCursorManager.SetThickness(thickness);
            _areaEraserManager.SetRadius(thickness);

            ApplyDrawingSettingsToAllCanvases();
            UpdateShapeToolAppearance(GetSelectedDrawingColor(), thickness);

            if (ThicknessValueText != null)
            {
                string format = thickness < 1 ? "0.0#" : "0.#";
                ThicknessValueText.Text = thickness.ToString(format) + "px";
            }
        }

        /// <summary>
        /// 현재 UI에서 선택된 펜/도형 색상을 반환한다. (콤보박스가 비어 있으면 검정색)
        /// </summary>
        private Color GetSelectedDrawingColor()
        {
            if (ColorComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string colorName)
            {
                return (Color)ColorConverter.ConvertFromString(colorName);
            }
            return Colors.Black;
        }

        /// <summary>
        /// 두께 슬라이더 값이 없을 때도 안전하게 현재 두께 값을 읽는다.
        /// </summary>
        private double GetSelectedThickness()
        {
            return ThicknessSlider?.Value ?? _inkTool?.CurrentThickness ?? 3.0;
        }

        /// <summary>
        /// InkCanvas 하나에 현재 선택된 펜 설정(색/두께)을 바로 적용한다.
        /// </summary>
        private void ApplyDrawingSettingsToCanvas(InkCanvas ink)
        {
            if (ink == null) return;

            var color = GetSelectedDrawingColor();
            double thickness = GetSelectedThickness();

            _inkTool.SetColor(color, ink);
            _inkTool.SetThickness(thickness, ink);
        }

        /// <summary>
        /// 현재 로드된 모든 페이지 InkCanvas에 동일한 펜 설정을 반영한다.
        /// </summary>
        private void ApplyDrawingSettingsToAllCanvases()
        {
            foreach (var ink in _pageInkCanvases.Values)
            {
                ApplyDrawingSettingsToCanvas(ink);
            }
        }

        /// <summary>
        /// 도형 도구의 브러시/두께를 현재 펜 설정과 일치하도록 동기화한다.
        /// </summary>
        private void UpdateShapeToolAppearance(Color color, double thickness)
        {
            _shapeTool.SetStroke(new SolidColorBrush(color), thickness);
        }

        /// <summary>
        /// InkCanvas 생성 시 필요한 이벤트/도구 매니저를 한 곳에서 연결한다.
        /// </summary>
        private void AttachCanvasManagers(InkCanvas ink)
        {
            if (ink == null) return;

            ink.RequestBringIntoView += SuppressBringIntoView;
            ink.PreviewMouseDown += InkCanvas_PreviewMouseDown;
            ink.StrokeCollected += InkCanvas_StrokeCollected;
            ink.Strokes.StrokesChanged += InkCanvas_StrokesChanged;

            _inkTool.SetTool(_inkTool.CurrentTool, ink);
            ApplyDrawingSettingsToCanvas(ink);

            _shapeTool.AttachCanvas(ink);
            _selectionTool.AttachCanvas(ink);
            _penCursorManager.AttachCanvas(ink);
            _textTool.AttachCanvas(ink);
            _areaEraserManager.AttachCanvas(ink);
        }

        private void InitializeTextToolDefaults()
        {
            _textTool.SetDefaultFontSize(_textDefaultFontSize);
            _textTool.SetDefaultForeground(new SolidColorBrush(_textDefaultFontColor));
        }

        private void RestoreTextEditingFocus()
        {
            var editing = _textTool.GetEditingTextBox();
            if (editing == null)
                return;

            editing.Dispatcher.BeginInvoke(new Action(() =>
            {
                editing.Focus();
                Keyboard.Focus(editing);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void RegisterTextStyleChange(TextBox textBox, object oldValue, object newValue, Action<TextBox, object> setter)
        {
            if (textBox == null || setter == null)
                return;
            if (oldValue == null && newValue == null)
                return;
            if (oldValue != null && oldValue.Equals(newValue))
                return;

            int pageIndex;
            if (!TryGetPageIndex(textBox, out pageIndex))
                return;

            PushUndoAction(new TextStyleChangedAction(pageIndex, textBox, oldValue, newValue, setter));
        }

        /// <summary>
        /// PDF 페이지 클릭 시 ScrollViewer가 강제 스크롤되는 기본 동작을 방지.
        /// </summary>
        private bool _allowBringIntoView = false;
        private void SuppressBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (!_allowBringIntoView)
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// 현재 선택된 페이지가 ScrollViewer 가운데쯤 보이도록 스크롤한다.
        /// </summary>
        private void ScrollCurrentPageIntoView()
        {
            if (PdfScrollViewer == null || PagesPanel == null) return;
            if (_currentPage < 0 || _currentPage >= PagesPanel.Children.Count) return;

            if (PagesPanel.Children[_currentPage] is FrameworkElement pageElement)
            {
                // 레이아웃이 최신 상태가 아니면 위치 계산이 틀어지므로 갱신
                PdfScrollViewer.UpdateLayout();
                PagesPanel.UpdateLayout();

                if (!pageElement.IsLoaded)
                {
                    Dispatcher.BeginInvoke(new Action(ScrollCurrentPageIntoView),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                    return;
                }

                try
                {
                    _allowBringIntoView = true;
                    pageElement.BringIntoView();
                }
                finally
                {
                    _allowBringIntoView = false;
                }
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Debug.WriteLine($"[Key] Key={e.Key}, SystemKey={e.SystemKey}, Modifiers={Keyboard.Modifiers}");

            if (_textTool.HandleKeyDown(e))
            {
                e.Handled = true;
                return;
            }

            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl)
            {
                Key key = e.Key;
                if (key == Key.System)
                    key = e.SystemKey;
                if (key == Key.ImeProcessed)
                    key = e.ImeProcessedKey;

                switch (key)
                {
                    case Key.Z:
                    {
                        bool redo = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;
                        bool handled = TryExecuteInkCanvasCommand(redo ? ApplicationCommands.Redo : ApplicationCommands.Undo);
                        if (!handled)
                        {
                            handled = redo ? PerformRedo() : PerformUndo();
                        }
                        e.Handled = handled;
                        break;
                    }
                    case Key.O:
                    {
                        ShowOpenPdfDialog();
                        e.Handled = true;
                        break;
                    }
                    case Key.S:
                    {
                        SaveAnnotation_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    }
                    case Key.Y:
                    {
                        bool handled = TryExecuteInkCanvasCommand(ApplicationCommands.Redo);
                        if (!handled)
                        {
                            handled = PerformRedo();
                        }
                        e.Handled = handled;
                        break;
                    }
                    case Key.C:
                        CopySelectedShape();
                        e.Handled = true;
                        break;
                    case Key.V:
                        PasteCopiedShape();
                        e.Handled = true;
                        break;
                    case Key.Add:
                    case Key.OemPlus:
                        AdjustZoom(+10);
                        e.Handled = true;
                        break;
                    case Key.Subtract:
                    case Key.OemMinus:
                        AdjustZoom(-10);
                        e.Handled = true;
                        break;
                }
            }

            if (e.Handled)
                return;

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                var focusedElement = FocusManager.GetFocusedElement(this);
                if (focusedElement is TextBoxBase || focusedElement is PasswordBox)
                    return;

                DeleteCurrentSelection();
                e.Handled = true;
            }
        }

        private void PdfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            e.Handled = true;
            if (e.Delta > 0)
                AdjustZoom(+10);
            else if (e.Delta < 0)
                AdjustZoom(-10);
        }

        private void AdjustZoom(double delta)
        {
            if (!_fitWidthReady || ZoomSlider == null)
                return;

            double target = ZoomSlider.Value + delta;
            if (target < ZoomSlider.Minimum) target = ZoomSlider.Minimum;
            if (target > ZoomSlider.Maximum) target = ZoomSlider.Maximum;
            ZoomSlider.Value = target;
        }

        private void CopySelectedShape()
        {
            var canvas = GetCurrentInkCanvas();
            if (canvas == null) return;

            var elements = _selectionTool.GetSelectedElementsSnapshot(canvas);
            var strokes = _selectionTool.GetSelectedStrokesSnapshot(canvas);
            if (elements.Count == 0 && strokes.Count == 0)
                return;

            var copiedShapes = new List<CopiedShapeInfo>();
            foreach (var element in elements)
            {
                if (element == null)
                    continue;

                try
                {
                    var info = new CopiedShapeInfo
                    {
                        Xaml = XamlWriter.Save(element)
                    };
                    var topLeft = GetElementTopLeftOnCanvas(canvas, element);
                    info.Left = topLeft.X;
                    info.Top = topLeft.Y;
                    copiedShapes.Add(info);
                }
                catch
                {
                    // skip element
                }
            }

            var copiedStrokes = new List<Stroke>();
            foreach (var stroke in strokes)
            {
                if (stroke == null)
                    continue;
                copiedStrokes.Add(stroke.Clone());
            }

            if (copiedShapes.Count == 0 && copiedStrokes.Count == 0)
                return;

            _copiedShapes = copiedShapes;
            _copiedStrokes = copiedStrokes;
        }

        private void PasteCopiedShape()
        {
            if ((_copiedShapes == null || _copiedShapes.Count == 0) &&
                (_copiedStrokes == null || _copiedStrokes.Count == 0))
                return;

            var canvas = GetCurrentInkCanvas();
            if (canvas == null)
                return;
            ActivateCanvasPage(canvas, updateTool: true);

            var newElements = new List<UIElement>();
            var newStrokes = new List<Stroke>();

            if (_copiedShapes != null)
            {
                foreach (var info in _copiedShapes)
                {
                    if (info == null || string.IsNullOrEmpty(info.Xaml))
                        continue;

                    try
                    {
                        var element = XamlReader.Parse(info.Xaml) as UIElement;
                        if (element == null)
                            continue;

                        ApplyPasteOffset(element, info);
                        canvas.Children.Add(element);
                        if (element is TextBox pastedTextBox)
                        {
                            _textTool.RegisterExistingTextBox(canvas, pastedTextBox);
                        }
                        newElements.Add(element);
                    }
                    catch
                    {
                        // ignore element
                    }
                }
            }

            if (_copiedStrokes != null)
            {
                foreach (var template in _copiedStrokes)
                {
                    if (template == null)
                        continue;

                    var stroke = template.Clone();
                    TranslateStroke(stroke, 12, 12);
                    canvas.Strokes.Add(stroke);
                    EnsureStrokeId(stroke);
                    newStrokes.Add(stroke);
                }
            }

            if (newElements.Count == 0 && newStrokes.Count == 0)
                return;

            RegisterGroupedAddition(canvas, newElements, newStrokes);
            _selectionTool.SelectItems(canvas, newElements, newStrokes);
        }

        private void DeleteCurrentSelection()
        {
            var canvas = GetCurrentInkCanvas();
            if (canvas == null)
                return;

            var selectedElements = _selectionTool.GetSelectedElementsSnapshot(canvas);
            var selectedStrokes = _selectionTool.GetSelectedStrokesSnapshot(canvas);
            if (selectedElements.Count == 0 && selectedStrokes.Count == 0)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            var actions = new List<IUndoRedoAction>();

            foreach (var element in selectedElements)
            {
                if (element == null)
                    continue;

                if (!canvas.Children.Contains(element))
                    continue;

                RemoveShapeFromCanvas(canvas, element);
                actions.Add(new ShapeRemovedAction(pageIndex, element));
            }

            if (selectedStrokes.Count > 0)
            {
                var strokesToRemove = new List<Stroke>();
                foreach (var stroke in selectedStrokes)
                {
                    if (stroke != null && canvas.Strokes.Contains(stroke))
                    {
                        strokesToRemove.Add(stroke);
                    }
                }

                if (strokesToRemove.Count > 0)
                {
                    foreach (var stroke in strokesToRemove)
                    {
                        canvas.Strokes.Remove(stroke);
                    }
                    var ids = strokesToRemove.Select(s => EnsureStrokeId(s)).ToList();
                    var snapshots = strokesToRemove.Select(s => CloneStrokeWithId(s)).ToList();
                    actions.Add(new StrokeCollectionChangedAction(pageIndex, ids, snapshots, new List<Guid>(), new List<Stroke>()));
                }
            }

            if (actions.Count > 0)
            {
                PushUndoAction(actions.Count == 1 ? actions[0] : new CompositeAction(actions));
                _selectionTool.ClearSelection(canvas);
            }
        }

        private Point GetElementTopLeftOnCanvas(InkCanvas canvas, UIElement element)
        {
            double left = InkCanvas.GetLeft(element);
            double top = InkCanvas.GetTop(element);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                try
                {
                    Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
                    GeneralTransform transform = element.TransformToVisual(canvas);
                    if (transform != null)
                    {
                        bounds = transform.TransformBounds(bounds);
                        if (double.IsNaN(left)) left = bounds.X;
                        if (double.IsNaN(top)) top = bounds.Y;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            return new Point(left, top);
        }

        private void ApplyPasteOffset(UIElement element, CopiedShapeInfo info)
        {
            const double offset = 12;
            if (element is Line line)
            {
                line.X1 += offset;
                line.X2 += offset;
                line.Y1 += offset;
                line.Y2 += offset;
            }
            else if (element is Polygon polygon)
            {
                for (int i = 0; i < polygon.Points.Count; i++)
                {
                    polygon.Points[i] = new Point(polygon.Points[i].X + offset, polygon.Points[i].Y + offset);
                }
            }
            else
            {
                double left = info?.Left ?? 0;
                double top = info?.Top ?? 0;
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                InkCanvas.SetLeft(element, left + offset);
                InkCanvas.SetTop(element, top + offset);
            }
        }

        private void TranslateStroke(Stroke stroke, double offsetX, double offsetY)
        {
            if (stroke == null)
                return;

            Matrix matrix = Matrix.Identity;
            matrix.Translate(offsetX, offsetY);
            stroke.Transform(matrix, false);
        }

        private void RegisterGroupedAddition(InkCanvas canvas, List<UIElement> elements, List<Stroke> strokes)
        {
            if (canvas == null)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
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

        private bool PerformUndo()
        {
            Debug.WriteLine(">>> PerformUndo 호출");
            if (_undoStack.Count == 0)
                return false;

            var action = _undoStack.Pop();
            _recordHistory = false;
            action.Undo(this);
            _recordHistory = true;
            _redoStack.Push(action);
            return true;
        }

        private bool PerformRedo()
        {
            Debug.WriteLine(">>> PerformRedo 호출");
            if (_redoStack.Count == 0)
                return false;

            var action = _redoStack.Pop();
            _recordHistory = false;
            action.Redo(this);
            _recordHistory = true;
            _undoStack.Push(action);
            return true;
        }

        private void PushUndoAction(IUndoRedoAction action)
        {
            if (!_recordHistory || action == null)
                return;

            _undoStack.Push(action);
            _redoStack.Clear();
        }

        private void ClearHistory()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        private void ShapeTool_ShapeCreated(InkCanvas canvas, UIElement element)
        {
            ActivateCanvasPage(canvas);
            RegisterShapeAddition(canvas, element);
        }

        private void ShapeTool_ShapeRemoved(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null) return;
            if (_selectionTool.GetSelectedElement(canvas) == element)
            {
                _selectionTool.ClearSelection(canvas);
            }
            RegisterShapeRemoval(canvas, element);
        }

        private void TextTool_TextBoxCreated(InkCanvas canvas, TextBox textBox)
        {
            ActivateCanvasPage(canvas);
            RegisterShapeAddition(canvas, textBox);
        }

        private void TextTool_TextBoxRemoved(InkCanvas canvas, TextBox textBox)
        {
            RegisterShapeRemoval(canvas, textBox);
        }

        private void TextTool_TextCommitted(InkCanvas canvas, TextBox textBox, string oldText, string newText)
        {
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            PushUndoAction(new TextEditedAction(pageIndex, textBox, oldText, newText));
        }

        private void AreaEraser_ElementErased(InkCanvas canvas, UIElement element)
        {
            ActivateCanvasPage(canvas);
            RegisterShapeRemoval(canvas, element);
            RemoveShapeFromCanvas(canvas, element);
        }

        private void SelectionTool_TextElementDoubleClicked(InkCanvas canvas, TextBox textBox)
        {
            if (textBox == null)
                return;

            ActivateCanvasPage(canvas, updateTool: true);

            if (TextButton != null)
            {
                ToolButton_Click(TextButton, new RoutedEventArgs(ButtonBase.ClickEvent));
            }

            _textTool.BeginEditingExistingTextBox(textBox, selectAll: true);
        }

        private void TextTool_ActiveTextBoxChanged(TextBox textBox)
        {
            if (textBox == null)
            {
                HideTextToolbar();
                return;
            }

            UpdateTextToolbarTarget(textBox);
        }

        private void TextTool_EditingStateChanged(TextBox textBox, bool isEditing)
        {
            if (!_textTool.IsActive)
            {
                HideTextToolbar();
                return;
            }

            if (textBox == null)
            {
                HideTextToolbar();
                return;
            }

            UpdateTextToolbarTarget(textBox);
        }

        private void UpdateTextToolbarTarget(TextBox textBox)
        {
            if (TextMiniToolbar == null)
                return;

            if (!_textTool.IsActive || textBox == null)
            {
                HideTextToolbar();
                return;
            }

            TextMiniToolbar.PlacementTarget = textBox;
            TextMiniToolbar.Placement = PlacementMode.Top;
            TextMiniToolbar.HorizontalOffset = 0;
            TextMiniToolbar.VerticalOffset = -4;
            TextMiniToolbar.IsOpen = true;
        }

        private void HideTextToolbar()
        {
            if (TextMiniToolbar == null)
                return;

            TextMiniToolbar.IsOpen = false;
            TextMiniToolbar.PlacementTarget = null;
        }

        private void RegisterShapeAddition(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            PushUndoAction(new ShapeAddedAction(pageIndex, element));
        }

        private void RegisterShapeRemoval(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null)
                return;

            int pageIndex;
            if (!TryGetPageIndex(canvas, out pageIndex))
                return;

            PushUndoAction(new ShapeRemovedAction(pageIndex, element));
        }

        private void InkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            if (!_recordHistory) return;
            if (!(sender is InkCanvas canvas)) return;

            ActivateCanvasPage(canvas);

            if (!TryGetPageIndex(canvas, out int pageIndex))
                return;

            Debug.WriteLine($">>> StrokeCollected page={pageIndex}, strokeHash={e.Stroke?.GetHashCode()}");

            Guid strokeId = EnsureStrokeId(e.Stroke);
            var snapshot = CloneStrokeWithId(e.Stroke);
            PushUndoAction(new StrokeAddedAction(pageIndex, strokeId, snapshot));
        }

        private void InkCanvas_StrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (!_recordHistory) return;
            if (!(sender is InkCanvas canvas)) return;

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

            // 새로운 스트로크가 추가된 이벤트(removed=0, added>0)는 StrokeCollected에서 이미 처리하므로 무시
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

        private void RemoveShapeFromCanvas(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null) return;
            if (_selectionTool.GetSelectedElement(canvas) == element)
            {
                _selectionTool.ClearSelection(canvas);
            }
            canvas.Children.Remove(element);
            _textTool.NotifyElementRemoved(element);
        }

        private void AddShapeToCanvas(InkCanvas canvas, UIElement element)
        {
            if (canvas == null || element == null) return;
            if (!canvas.Children.Contains(element))
            {
                canvas.Children.Add(element);
            }
            if (element is TextBox textBox)
            {
                _textTool.RegisterExistingTextBox(canvas, textBox);
            }
        }

        private void ApplyTextContent(TextBox textBox, string text)
        {
            if (textBox == null)
                return;
            textBox.Text = text ?? string.Empty;
        }

        private void ApplyTextStyle(TextBox textBox, Action<TextBox, object> setter, object value)
        {
            if (textBox == null || setter == null)
                return;
            setter(textBox, value);
        }

        private void UpdateCursorMode(string tag)
        {
            switch (tag)
            {
                case "Eraser":
                    _penCursorManager.SetMode(PenCursorManager.CursorMode.Eraser);
                    break;
                case "Text":
                    _penCursorManager.SetMode(PenCursorManager.CursorMode.Hidden, Cursors.IBeam);
                    break;
                case "Pen":
                case "Highlighter":
                    _penCursorManager.SetMode(PenCursorManager.CursorMode.Pen);
                    break;
                case "Select":
                case "Rectangle":
                case "Ellipse":
                case "Line":
                case "Triangle":
                    _penCursorManager.SetMode(PenCursorManager.CursorMode.Hidden, Cursors.Arrow);
                    break;
                default:
                    _penCursorManager.SetMode(PenCursorManager.CursorMode.Hidden, Cursors.Arrow);
                    break;
            }
        }

        private InkCanvas CloneCanvasForExport(InkCanvas source)
        {
            var clone = new InkCanvas
            {
                Width = source.Width,
                Height = source.Height,
                Background = Brushes.Transparent
            };
            clone.Strokes = source.Strokes.Clone();

            foreach (UIElement child in source.Children)
            {
                if (!IsSerializableElement(child)) continue;
                try
                {
                    var xaml = XamlWriter.Save(child);
                    if (XamlReader.Parse(xaml) is UIElement cloned)
                    {
                        double left = InkCanvas.GetLeft(child);
                        double top = InkCanvas.GetTop(child);
                        if (double.IsNaN(left)) left = 0;
                        if (double.IsNaN(top)) top = 0;
                        InkCanvas.SetLeft(cloned, left);
                        InkCanvas.SetTop(cloned, top);
                        clone.Children.Add(cloned);
                    }
                }
                catch
                {
                    // ignore
                }
            }
            return clone;
        }

        private Guid EnsureStrokeId(Stroke stroke)
        {
            if (stroke == null)
                return Guid.Empty;

            if (stroke.ContainsPropertyData(StrokeIdProperty))
            {
                var existing = stroke.GetPropertyData(StrokeIdProperty) as string;
                if (Guid.TryParse(existing, out Guid parsed))
                    return parsed;
            }

            Guid id = Guid.NewGuid();
            stroke.AddPropertyData(StrokeIdProperty, id.ToString());
            return id;
        }

        private Guid? GetStrokeId(Stroke stroke)
        {
            if (stroke == null)
                return null;
            if (stroke.ContainsPropertyData(StrokeIdProperty))
            {
                var existing = stroke.GetPropertyData(StrokeIdProperty) as string;
                if (Guid.TryParse(existing, out Guid parsed))
                    return parsed;
            }
            return null;
        }

        private Stroke CloneStrokeWithId(Stroke stroke)
        {
            if (stroke == null)
                return null;

            Guid id = EnsureStrokeId(stroke);
            Stroke clone = stroke.Clone();
            if (clone.ContainsPropertyData(StrokeIdProperty))
                clone.RemovePropertyData(StrokeIdProperty);
            clone.AddPropertyData(StrokeIdProperty, id.ToString());
            return clone;
        }

        private bool TryRemoveStrokeById(InkCanvas canvas, Guid id)
        {
            if (canvas == null)
                return false;

            for (int i = canvas.Strokes.Count - 1; i >= 0; i--)
            {
                var stroke = canvas.Strokes[i];
                var strokeId = GetStrokeId(stroke);
                if (strokeId.HasValue && strokeId.Value == id)
                {
                    canvas.Strokes.Remove(stroke);
                    return true;
                }
            }

            return false;
        }

        private class AnnotatedPageImage
        {
            public int PixelWidth { get; set; }
            public int PixelHeight { get; set; }
            public double WidthPoints { get; set; }
            public double HeightPoints { get; set; }
            public byte[] ImageBytes { get; set; }
        }

        private void ActivateCanvasPage(InkCanvas canvas, bool updateTool = false)
        {
            if (canvas == null)
                return;

            if (_inkCanvasPageIndex.TryGetValue(canvas, out int pageIndex))
            {
                if (_currentPage != pageIndex)
                {
                    _currentPage = pageIndex;
                    UpdatePageInfo();
                }

                if (updateTool)
                {
                    _inkTool.SetTool(_inkTool.CurrentTool, canvas);
                }
            }
        }

        private void InkCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is InkCanvas canvas)
            {
                ActivateCanvasPage(canvas, updateTool: true);
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
