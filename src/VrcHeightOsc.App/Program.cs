namespace VrcHeightOsc.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var controller = new AppController();
        Application.Run(new MainForm(controller));
    }
}
