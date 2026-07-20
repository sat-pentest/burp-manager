namespace BurpManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "genicon")
        {
            var path = args.Length >= 2 ? args[1] : "app.ico";
            IconGen.Run(path);
            Console.WriteLine("icon written: " + path);
            return;
        }
        if (args.Length >= 3 && args[0] == "genicon-from")
        {
            IconGen.RunFromImage(args[1], args[2]);
            Console.WriteLine("icon written from " + args[1] + " -> " + args[2]);
            return;
        }
        if (args.Length >= 1 && args[0] == "selftest")
        {
            Environment.Exit(SelfTest.Run());
            return;
        }

        string? initial = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;

        try
        {
            ApplicationConfiguration.Initialize();
            Theme.InitDarkMode();
            Application.Run(new MainForm(initial));
        }
        catch (Exception ex)
        {
            var log = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "crash.log");
            File.WriteAllText(log, ex.ToString());
            throw;
        }
    }
}
