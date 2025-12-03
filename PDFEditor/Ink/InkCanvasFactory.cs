using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows;

namespace PDFEditor.Ink
{
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
