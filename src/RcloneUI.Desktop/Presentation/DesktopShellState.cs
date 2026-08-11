using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Desktop.Presentation;

public enum DesktopConnectionState { Connecting, ConnectedLocked, ConnectedOperational, ReadOnlyRecovery, Disconnected }
public sealed record DesktopRemoteOption(Guid Id, string DisplayName);

public interface IDesktopHostClient
{
    ValueTask<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    ValueTask<JsonElement> SendCommandAsync(string commandType, JsonElement arguments, CancellationToken cancellationToken);
}

public sealed class DesktopShellState : INotifyPropertyChanged
{
    private string route = "Home";
    private DesktopConnectionState connection = DesktopConnectionState.Connecting;
    private bool english;
    private string lastAction = string.Empty;
    private string remoteSummary = string.Empty;
    private string[] remoteNames = [];
    private DesktopRemoteOption[] remoteOptions = [];
    private string copyStatus = string.Empty;
    private string? capabilityBinding;
    private string masterPassword = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string PageTitle => route switch { "Home" => T("主页", "Home"), "Remotes" => T("远程存储", "Remotes"), "Transfers" => T("传输任务", "Transfer Tasks"), "Browser" => T("文件浏览器", "File Browser"), "Mounts" => T("挂载磁盘", "Mounts"), "Schedules" => T("计划任务", "Schedules"), "Activity" => T("活动与日志", "Activity & Logs"), _ => T("设置", "Settings") };
    public bool IsHome => route == "Home";
    public bool IsJourney => !IsHome;
    public bool IsTransferJourney => route == "Transfers";
    public string CurrentRoute => route;
    public string ConnectionLabel => connection switch { DesktopConnectionState.Connecting => T("正在连接…", "Connecting…"), DesktopConnectionState.ConnectedLocked => T("已锁定", "Locked"), DesktopConnectionState.ConnectedOperational => T("运行正常", "Operational"), DesktopConnectionState.ReadOnlyRecovery => T("只读恢复", "Read-only recovery"), _ => T("连接已中断", "Disconnected") };
    public string SessionLabel => connection switch { DesktopConnectionState.ConnectedOperational => T("数据保险库已解锁", "Vault unlocked"), DesktopConnectionState.ConnectedLocked => T("需要解锁数据保险库", "Vault unlock required"), DesktopConnectionState.ReadOnlyRecovery => T("写入已停用，数据保持原状", "Writes disabled; data preserved"), _ => T("后台任务状态可能不是最新", "Background state may be stale") };
    public IBrush StatusBrush => connection == DesktopConnectionState.ConnectedOperational ? Brushes.MediumSeaGreen : connection == DesktopConnectionState.Disconnected ? Brushes.IndianRed : Brushes.DarkOrange;
    public bool NeedsAttention => connection != DesktopConnectionState.ConnectedOperational;
    public bool IsVaultLocked => connection == DesktopConnectionState.ConnectedLocked;
    public string MasterPassword
    {
        get => masterPassword;
        set { if (masterPassword == value) return; masterPassword = value; PropertyChanged?.Invoke(this, new(nameof(MasterPassword))); }
    }
    public string AttentionTitle => connection switch { DesktopConnectionState.ConnectedLocked => T("保险库已锁定", "Vault is locked"), DesktopConnectionState.ReadOnlyRecovery => T("需要恢复", "Recovery required"), DesktopConnectionState.Disconnected => T("后台服务连接中断", "Background Host disconnected"), _ => T("正在连接后台服务", "Connecting to Background Host") };
    public string AttentionDetail => connection switch { DesktopConnectionState.ReadOnlyRecovery => T("不会自动清理或覆盖未知状态。请检查恢复详情。", "Unknown state will not be cleared or overwritten. Review recovery details."), DesktopConnectionState.Disconnected => T("现有后台任务可能仍在运行；重新连接前不会猜测其结果。", "Existing work may still run; results remain unknown until reconnection."), _ => T("需要连接并解锁后才能启动新任务。", "Connect and unlock before starting new work.") };
    public string AttentionAction => connection switch { DesktopConnectionState.ConnectedLocked => T("解锁", "Unlock"), DesktopConnectionState.ReadOnlyRecovery => T("检查恢复", "Review recovery"), DesktopConnectionState.Disconnected => T("重新连接", "Reconnect"), _ => T("等待", "Wait") };
    public string RunningTasksLabel => T("0 个运行中", "0 running");
    public string LastAction => lastAction;
    public string RemoteSummary => remoteSummary;
    public IReadOnlyList<string> RemoteNames => remoteNames;
    public IReadOnlyList<DesktopRemoteOption> RemoteOptions => remoteOptions;
    public DesktopRemoteOption? CopySourceRemote { get; set; }
    public DesktopRemoteOption? CopyDestinationRemote { get; set; }
    public string CopyStatus => copyStatus;
    public string CopySourcePath { get; set; } = string.Empty;
    public string CopyDestinationPath { get; set; } = string.Empty;
    public string? CapabilityBinding => capabilityBinding;
    public string JourneyHeading => PageTitle;
    public string JourneyDescription => route switch { "Remotes" => T("通过三步向导添加云端账号；凭据只发送给后台服务并写入加密保险库。", "Add cloud accounts in three guided steps. Credentials go only to the Host and encrypted Vault."), "Transfers" => T("复制、移动或单向镜像。执行前必须接受预览，涉及删除时需要明确确认。", "Copy, move, or one-way mirror. Accept a preview before execution; deletion requires explicit confirmation."), "Mounts" => T("创建稳定盘符的挂载配置。Ready 与安全卸载均需要完整证据。", "Create stable drive profiles. Ready and safe unmount require complete evidence."), "Activity" => T("查看真实结果、部分完成、未知状态和可脱敏导出的诊断信息。", "Review truthful outcomes, partial results, unknown states, and redacted diagnostics."), _ => T("此功能通过后台服务快照和命令工作；界面不直接操作 rclone 或保险库。", "This journey uses Host snapshots and commands; the UI never operates rclone or the Vault directly.") };
    public string JourneyStatus => connection == DesktopConnectionState.ConnectedOperational ? T("可以开始", "Ready") : T("当前不可执行", "Action unavailable");
    public string JourneyActionHint => connection == DesktopConnectionState.ConnectedOperational ? T("普通用户默认值已启用，高级选项按需展开。", "Ordinary-user defaults are active; advanced options remain available on demand.") : AttentionDetail;
    public string JourneyPrimaryAction => route switch { "Remotes" => T("添加远程存储", "Add Remote"), "Transfers" => T("创建并预览", "Create & Preview"), "Mounts" => T("创建挂载配置", "Create Mount Profile"), "Activity" => T("导出脱敏诊断", "Export Redacted Diagnostics"), _ => T("继续", "Continue") };

    public void Navigate(string value) { route = value; ChangedAll(); }
    public void ToggleLanguage() { english = !english; ChangedAll(); }
    public void ApplyConnection(DesktopConnectionState value) { connection = value; ChangedAll(); }
    public void ApplyAction(string value) { lastAction = value; ChangedAll(); }
    public void ApplySnapshot(HostSnapshot snapshot)
    {
        connection = snapshot.Body.TryGetProperty("session", out var session) ? session.GetString() switch { "operational" => DesktopConnectionState.ConnectedOperational, "locked" => DesktopConnectionState.ConnectedLocked, "read-only-recovery" => DesktopConnectionState.ReadOnlyRecovery, _ => DesktopConnectionState.ReadOnlyRecovery } : DesktopConnectionState.ReadOnlyRecovery;
        if (snapshot.Body.TryGetProperty("remotes", out var remotes) && remotes.ValueKind == JsonValueKind.Array)
        {
            remoteNames = remotes.EnumerateArray().Select(item => item.TryGetProperty("displayName", out var name) ? name.GetString() : null).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray();
            remoteOptions = remotes.EnumerateArray().Select(item => new DesktopRemoteOption(item.GetProperty("id").GetGuid(), item.GetProperty("displayName").GetString()!)).ToArray();
            CopySourceRemote = KeepSelection(CopySourceRemote, remoteOptions) ?? remoteOptions.FirstOrDefault();
            CopyDestinationRemote = KeepSelection(CopyDestinationRemote, remoteOptions) ?? remoteOptions.Skip(1).FirstOrDefault() ?? remoteOptions.FirstOrDefault();
            remoteSummary = remoteNames.Length == 0 ? T("尚未添加远程存储", "No Remotes added") : string.Join(" · ", remoteNames);
        }
        else { remoteNames = []; remoteOptions = []; CopySourceRemote = null; CopyDestinationRemote = null; remoteSummary = T("远程存储状态未知", "Remote state unknown"); }
        capabilityBinding = snapshot.Body.TryGetProperty("rclone", out var rclone) && rclone.TryGetProperty("status", out var status) && status.GetString() == "ready" && rclone.TryGetProperty("capabilityBinding", out var binding) ? binding.GetString() : null;
        if (snapshot.Body.TryGetProperty("copyRuns", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0)
        {
            var run = runs.EnumerateArray().Last();
            var state = run.GetProperty("state").GetString() ?? "unknown";
            var bytes = run.GetProperty("bytes").GetInt64(); var total = run.GetProperty("totalBytes").GetInt64(); var speed = run.GetProperty("bytesPerSecond").GetDouble();
            copyStatus = $"{state} · {bytes}/{total} bytes · {speed:F0} B/s";
        }
        else copyStatus = capabilityBinding is null ? T("rclone 不可用", "rclone unavailable") : T("尚未运行 Copy", "No Copy run yet");
        ChangedAll();
    }
    private string T(string zh, string en) => english ? en : zh;
    private static DesktopRemoteOption? KeepSelection(DesktopRemoteOption? selected, DesktopRemoteOption[] options) => selected is null ? null : options.FirstOrDefault(option => option.Id == selected.Id);
    private void ChangedAll() { foreach (var property in GetType().GetProperties().Where(x => x.GetIndexParameters().Length == 0)) PropertyChanged?.Invoke(this, new(property.Name)); }
}
