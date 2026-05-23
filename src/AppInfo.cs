using System;
using System.Reflection;

namespace Headroom
{
    static class AppInfo
    {
        public static string DisplayVersion
        {
            get
            {
                string version = InformationalVersion();
                if (string.IsNullOrWhiteSpace(version)) return "dev";
                if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase)) return version;
                if (version.StartsWith("dev", StringComparison.OrdinalIgnoreCase)) return version;
                return "v" + version;
            }
        }

        static string InformationalVersion()
        {
            object[] attributes = typeof(AppInfo).Assembly.GetCustomAttributes(
                typeof(AssemblyInformationalVersionAttribute), false);
            if (attributes.Length == 0) return "";

            var info = attributes[0] as AssemblyInformationalVersionAttribute;
            return info == null ? "" : (info.InformationalVersion ?? "").Trim();
        }
    }
}
