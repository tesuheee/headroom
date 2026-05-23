using System;

namespace Headroom
{
    static class CliLoginLauncher
    {
        public static void Start(string serviceName, string title, string banner)
        {
            string cliExec = serviceName == "Claude" ? "claude" : "codex login";
            string cliCommand = "chcp 65001 >nul && title " + title + " && echo. && echo " + banner + " && echo. && " + cliExec;
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k " + cliCommand)
            {
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
            };
            System.Diagnostics.Process.Start(psi);
        }
    }
}
