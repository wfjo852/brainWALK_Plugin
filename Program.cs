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

        // UI 스레드에서 처리되지 않은 예외가 보이지 않는 모달 창 뒤에 숨어
        // 앱이 멈춘 것처럼 보이는 상황을 막기 위해 명시적으로 메시지를 띄운다.
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"예상치 못한 오류가 발생했습니다:\n{e.Exception.Message}", "brainWALK", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.Run(new LauncherForm());
        return 0;
    }
}
