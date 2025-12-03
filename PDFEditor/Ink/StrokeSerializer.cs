using System.IO;
using System.Windows.Ink;

namespace PDFEditor.Ink
{
    /// <summary>
    /// InkCanvas StrokeCollection을 파일로 저장/로드하는 유틸리티.
    /// 현재 프로젝트에서는 사용하지 않지만, 향후 저장 기능을 추가할 때 바로 재활용 가능.
    /// </summary>
    public static class StrokeSerializer
    {
        /// <summary>
        /// 지정 경로에 StrokeCollection을 바이너리 형식으로 저장.
        /// </summary>
        public static void SaveStrokeLayer(string path, StrokeCollection strokes)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create))
            { 
                strokes.Save(fs);
            }
        }

        /// <summary>
        /// 저장된 StrokeCollection 파일을 읽어서 반환.
        /// 파일이 없다면 빈 컬렉션을 만들어 돌려준다.
        /// </summary>
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
