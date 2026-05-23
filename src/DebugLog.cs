using System;
using System.IO;

namespace Headroom
{
    static class DebugLog
    {
        public static string DirectoryPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".headroom"); }
        }

        public static void Write(string name, string text)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(Path.Combine(DirectoryPath, name), text ?? "");
            }
            catch
            {
            }
        }
    }
}
