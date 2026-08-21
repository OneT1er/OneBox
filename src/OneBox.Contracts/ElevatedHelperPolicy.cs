namespace OneBox.Contracts;

public static class ElevatedHelperPolicy
{
    public static string TimeoutMessage(bool helperExited)
        => helperExited
            ? "提权操作等待超时（辅助进程已终止）"
            : "提权操作等待超时（辅助进程仍在运行，请查看日志）";
}
