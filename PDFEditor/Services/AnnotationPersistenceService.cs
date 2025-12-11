using PDFEditor.Shapes;
using PDFEditor.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Markup;
using System.Windows.Shapes;
using System.Xml;
using System.Xml.Linq;

namespace PDFEditor.Services
{
    /// <summary>
    /// InkCanvas에 그린 스트로크/도형/텍스트를 XML 기반 .pdfanno 파일로 저장하거나 다시 로드하는 서비스.
    /// 포맷 구조: <PdfAnnotations><PdfPath/><Pages><Page Index=""><Strokes>Base64</Strokes><Elements><![CDATA[XAML]]></Elements></Page>...
    /// MainWindow에서 파일 입출력 책임을 분리해 직렬화 세부 구현을 한 곳에서 관리한다.
    /// </summary>
    public class AnnotationPersistenceService
    {
        public void Save(string path, string pdfPath, IDictionary<int, InkCanvas> canvases)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("저장 경로가 필요합니다.", nameof(path));
            if (canvases == null)
                throw new ArgumentNullException(nameof(canvases));

            var root = new XElement("PdfAnnotations",
                new XAttribute("Version", "1"),
                new XElement("PdfPath", pdfPath ?? string.Empty));

            var pagesElement = new XElement("Pages");
            foreach (var kvp in canvases.OrderBy(kvp => kvp.Key))
            {
                var pageElement = new XElement("Page", new XAttribute("Index", kvp.Key));
                string strokes = SerializeStrokes(kvp.Value.Strokes);
                if (!string.IsNullOrEmpty(strokes))
                {
                    pageElement.Add(new XElement("Strokes", strokes));
                }

                var elementsNode = SerializeElements(kvp.Value);
                if (elementsNode != null)
                {
                    pageElement.Add(elementsNode);
                }

                if (pageElement.HasElements)
                {
                    pagesElement.Add(pageElement);
                }
            }

            root.Add(pagesElement);
            new XDocument(root).Save(path);
        }

        public AnnotationDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("로드할 경로가 필요합니다.", nameof(path));

            var document = XDocument.Load(path);
            var root = document.Root ?? throw new InvalidDataException("주석 파일 구조가 올바르지 않습니다.");

            string pdfPath = root.Element("PdfPath")?.Value;
            var pages = new List<PageAnnotations>();

            var pageNodes = root.Element("Pages")?.Elements("Page");
            if (pageNodes != null)
            {
                foreach (var pageNode in pageNodes)
                {
                    int pageIndex = (int?)pageNode.Attribute("Index") ?? -1;
                    if (pageIndex < 0)
                        continue;

                    var strokesData = pageNode.Element("Strokes")?.Value;
                    var strokes = DeserializeStrokes(strokesData);

                    var elements = new List<UIElement>();
                    var elementsNode = pageNode.Element("Elements");
                    if (elementsNode != null)
                    {
                        foreach (var elementNode in elementsNode.Elements("Element"))
                        {
                            var element = DeserializeElement(elementNode.Value);
                            if (element != null)
                            {
                                elements.Add(element);
                            }
                        }
                    }

                    pages.Add(new PageAnnotations(pageIndex, strokes, elements));
                }
            }

            return new AnnotationDocument(pdfPath, pages);
        }

        internal static bool IsSerializableElement(UIElement element)
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

        private static string SerializeStrokes(StrokeCollection strokes)
        {
            if (strokes == null || strokes.Count == 0)
                return null;

            using (var ms = new MemoryStream())
            {
                strokes.Save(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        private static StrokeCollection DeserializeStrokes(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return null;

            byte[] bytes = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(bytes))
            {
                return new StrokeCollection(ms);
            }
        }

        private static XElement SerializeElements(InkCanvas canvas)
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

        private static UIElement DeserializeElement(string xaml)
        {
            if (string.IsNullOrWhiteSpace(xaml))
                return null;

            using (var stringReader = new StringReader(xaml))
            using (var xmlReader = XmlReader.Create(stringReader))
            {
                return XamlReader.Load(xmlReader) as UIElement;
            }
        }
    }

    public sealed class AnnotationDocument
    {
        public AnnotationDocument(string pdfPath, IReadOnlyList<PageAnnotations> pages)
        {
            PdfPath = pdfPath;
            Pages = pages ?? Array.Empty<PageAnnotations>();
        }

        public string PdfPath { get; }
        public IReadOnlyList<PageAnnotations> Pages { get; }
    }

    public sealed class PageAnnotations
    {
        public PageAnnotations(int pageIndex, StrokeCollection strokes, IReadOnlyList<UIElement> elements)
        {
            PageIndex = pageIndex;
            Strokes = strokes;
            Elements = elements ?? Array.Empty<UIElement>();
        }

        public int PageIndex { get; }
        public StrokeCollection Strokes { get; }
        public IReadOnlyList<UIElement> Elements { get; }
    }
}
