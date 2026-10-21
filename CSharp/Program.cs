namespace BrainWalk;

static class Program
{
    [STAThread]
    static async Task<int> Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Any(arg => arg.Equals("-console", StringComparison.OrdinalIgnoreCase) ||
                            arg.Equals("--console", StringComparison.OrdinalIgnoreCase)))
            return await ConsoleRunner.RunAsync();

        Application.Run(new MainForm());
        return 0;
    }
}
