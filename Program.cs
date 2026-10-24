namespace DownloadManager;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\IndependentDownloadManager.Instance.v1",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
        return 0;
    }
}