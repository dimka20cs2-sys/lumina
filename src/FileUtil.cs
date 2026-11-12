// Надёжная запись файла: временный файл → замена. Антивирус/индексатор иногда ненадолго держат файл —
// тогда повторяем, а в крайнем случае копируем поверх.
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AkeeLauncher
{
    static class FileUtil
    {
        public static void WriteAtomic(string path, string text, Encoding enc)
        {
            string tmp = path + ".lumina-tmp";
            File.WriteAllText(tmp, text, enc);
            ReplaceWith(tmp, path);
        }

        public static void ReplaceWith(string tmp, string path)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException)
                {
                    if (i < 6) { Thread.Sleep(80 * (i + 1)); continue; }
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    if (i < 6) { Thread.Sleep(80 * (i + 1)); continue; }
                    throw;
                }
            }
        }
    }
}
