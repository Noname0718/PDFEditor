using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows;

namespace PDFEditor.Ink
{
    /// <summary>
    /// 렌더링된 PDF 이미지를 InkCanvas와 한 그리드에 얹을 때 사용하는 팩토리.
    /// 현재는 사용하지 않지만 구조 파악을 돕기 위해 주석을 남겨 둔다.
    /// </summary>
    public static class InkCanvasFactory
    {
        //PDF 페이지 이미지 위에 InkCanvas를 올린 Grid 생성
        public static Grid CreatePageLayer(ImageSource pageImage, out InkCanvas inkCanvas)
        {
            Grid grid = new Grid();

            var image = new Image
            {
                Source = pageImage,
                Stretch = Stretch.Uniform
            };
            grid.Children.Add(image);

            inkCanvas = new InkCanvas
            {
                Background = Brushes.Transparent,
            };
            grid.Children.Add(inkCanvas);
            
            return grid;
        }
    }
}
