using System;
using System.IO;

namespace Headroom
{
    static class FileWrites
    {
        public static void WriteUtf8Atomic(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            string temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, content ?? "", new System.Text.UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            catch (Exception ex)
            {
                DebugLog.Write("file-write-error.txt", "path: " + path + "\r\n" + ex);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }
    }
}
