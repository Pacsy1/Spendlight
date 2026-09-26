namespace Spendlight;

internal static class Program
{
    /// <summary>
    /// Spendlight.exe [--dir &lt;path to .claude\projects&gt;] [--export &lt;file.json&gt;]
    /// --export writes the dashboard's data (every API call with its tokens and cost) and exits.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var root = LogReader.DefaultRoot();
        string? export = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--dir" or "-d") root = args[i + 1];
            if (args[i] is "--export") export = args[i + 1];
        }

        if (export != null)
        {
            File.WriteAllBytes(export, new DataStore(root).BuildJson());
            return;
        }

        if (!Directory.Exists(root))
        {
            MessageBox.Show(
                $"Couldn't find Claude Code's logs at:\n{root}\n\nRun Claude Code at least once, or start the app with --dir <path>.",
                "Spendlight", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new MainForm(new DataStore(root)));
    }
}
