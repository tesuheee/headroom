using System;
using System.IO;
using System.Windows.Forms;

namespace Headroom
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            HeadroomOptions.Configure(Environment.GetCommandLineArgs());
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UsageForm());
        }
    }

    static class HeadroomOptions
    {
        public static bool FixtureMode { get; private set; }
        public static string FixtureDir { get; private set; }

        public static void Configure(string[] args)
        {
            FixtureDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".headroom-fixture");

            string envDir = Environment.GetEnvironmentVariable("HEADROOM_FIXTURE_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                FixtureMode = true;
                FixtureDir = envDir.Trim();
            }

            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg.Equals("--fixture", StringComparison.OrdinalIgnoreCase))
                {
                    FixtureMode = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        FixtureDir = args[++i];
                }
                else if (arg.StartsWith("--fixture=", StringComparison.OrdinalIgnoreCase))
                {
                    FixtureMode = true;
                    FixtureDir = arg.Substring("--fixture=".Length).Trim('"');
                }
            }

            if (!Path.IsPathRooted(FixtureDir))
                FixtureDir = Path.GetFullPath(FixtureDir);
        }
    }
}
