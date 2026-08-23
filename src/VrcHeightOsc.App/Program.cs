namespace VrcHeightOsc.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\SophiasOscScaleTool.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            SingleInstanceWindow.ActivateExisting();
            return;
        }

        ApplicationConfiguration.Initialize();
        var controller = new AppController();
        Application.Run(new MainForm(controller));
        GC.KeepAlive(singleInstance);
    }
}
