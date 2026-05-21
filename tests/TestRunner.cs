using System;
using System.IO;

namespace Headroom
{
    static class TestRunner
    {
        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            try
            {
                ParserTests.Run(root);
                CredentialStoreTests.Run(root);
                RefreshPolicyTests.Run(root);
                Console.WriteLine("All tests passed");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Tests failed");
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
