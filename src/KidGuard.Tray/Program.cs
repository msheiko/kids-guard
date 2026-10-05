namespace KidGuard.Tray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Один экземпляр на сеанс: служба может запустить Tray повторно после своего перезапуска.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\KidGuard.Tray", out var createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
