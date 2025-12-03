using System.IO;
using System.Windows.Ink;

namespace PDFEditor.Ink
{
    public static class StrokeSerializer
    {
        public static void SaveStrokeLayer(string path, StrokeCollection strokes)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create))
            { 
                strokes.Save(fs);
            }
        }

        public static StrokeCollection LoadStrokeLayer(string path)
        {
            if (!File.Exists(path))
                return new StrokeCollection();

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            { 
                return new StrokeCollection(fs);
            }
        }
    }
}
