namespace Hl7Receiver;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => WriteCrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show("程序启动失败，详情见 crash.log：" + Environment.NewLine + ex.Message,
                "HL7 接收工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去时不再抛异常
        }
    }
}
