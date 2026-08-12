using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Desktop.Presentation;

public enum DesktopConnectionState { Connecting, ConnectedLocked, ConnectedOperational, ReadOnlyRecovery, Disconnected }
public enum DesktopTransferMode { Download, RemoteCopy }
public sealed record DesktopRemoteOption(Guid Id, string DisplayName);
public sealed record DesktopChoice(string Key, string DisplayName);
public sealed record DesktopMountProfileOption(Guid Id, ulong Revision, string DisplayName, Guid RemoteId, string Subpath, string PresentationMode, string DriveSelection, string DriveLetter, string? FixedDirectoryPath, string VolumeName);

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
    private string? rcloneVersion;
    private string masterPassword = string.Empty;
    private bool advancedOptionsExpanded;
    private DesktopTransferMode transferMode;
    private string downloadDestinationPath = string.Empty;
    private Guid? activeMountId;
    private string mountLifecycleState = "stopped";
    private Guid? mountLifecycleProfileId;
    private string mountStatus = string.Empty;
    private string mountFixedDirectoryPath = string.Empty;
    private DesktopChoice[] mountPresentationOptions = [];
    private DesktopChoice[] driveSelectionOptions = [];
    private DesktopChoice mountPresentation = null!;
    private DesktopChoice mountDriveSelection = null!;
    private string winFspStatus = "unknown";
    private string? winFspVersion;
    private bool rcloneMountAvailable;
    private DesktopMountProfileOption[] mountProfiles = [];
    private DesktopMountProfileOption? selectedMountProfile;

    public DesktopShellState() => RefreshMountChoices();

    public event PropertyChangedEventHandler? PropertyChanged;
    public string EditionLabel => T("便携版 · Portable", "Portable edition");
    public string NavHome => T("⌂  主页", "⌂  Home");
    public string NavRemotes => T("☁  远程存储", "☁  Remotes");
    public string NavTransfers => T("⇄  传输任务", "⇄  Transfers");
    public string NavBrowser => T("▤  文件浏览器", "▤  File Browser");
    public string NavMounts => T("▣  挂载磁盘", "▣  Mounts");
    public string NavSchedules => T("◷  计划任务", "◷  Schedules");
    public string NavActivity => T("≡  活动与日志", "≡  Activity & Logs");
    public string NavSettings => T("⚙  设置", "⚙  Settings");
    public string HostHeading => T("后台服务", "Background Host");
    public string VaultHeading => T("数据保险库", "Vault");
    public string VaultStatusLabel => connection switch
    {
        DesktopConnectionState.ConnectedOperational => T("已解锁", "Unlocked"),
        DesktopConnectionState.ConnectedLocked => T("已锁定", "Locked"),
        DesktopConnectionState.ReadOnlyRecovery => T("只读恢复", "Read-only recovery"),
        _ => T("状态未知", "Status unknown")
    };
    public IBrush VaultStatusBrush => connection == DesktopConnectionState.ConnectedOperational ? Brushes.MediumSeaGreen : connection == DesktopConnectionState.ConnectedLocked ? Brushes.DarkOrange : Brushes.Gray;
    public bool CanLockVault => connection == DesktopConnectionState.ConnectedOperational;
    public string LockVaultLabel => T("立即锁定", "Lock now");
    public string ExitLabel => T("退出界面", "Exit Desktop");
    public string NotificationsLabel => T("通知", "Notifications");
    public string HomeHeading => T("一切尽在掌握", "Everything under control");
    public string HomeDescription => T("查看云端状态并继续最近的任务。", "Review cloud status and continue recent tasks.");
    public string NewTaskLabel => T("＋ 新建任务", "+ New task");
    public string ShortcutTransfer => T("⇄ 复制或同步", "⇄ Copy or sync");
    public string ShortcutBrowse => T("▤ 浏览文件", "▤ Browse files");
    public string ShortcutMount => T("▣ 挂载磁盘", "▣ Mount storage");
    public string ShortcutRemote => T("☁ 添加远程存储", "☁ Add Remote");
    public string TransferCardHeading => T("传输任务", "Transfer tasks");
    public string EmptyTaskText => T("暂无运行中的任务。创建任务前会先展示变更预览和删除风险。", "No tasks are running. Changes and deletion risks are shown before a task starts.");
    public string RemoteHealthHeading => T("远程存储健康", "Remote health");
    public string NoRemoteText => T("尚未添加远程存储", "No Remotes added");
    public string OpenRemoteWizardLabel => T("打开三步设置向导", "Open three-step setup");
    public string QuickAddHeading => T("快速添加（高级）", "Quick add (advanced)");
    public string RemoteDisplayNameHint => T("显示名称", "Display name");
    public string RemoteTokenHint => T("rclone OAuth 令牌", "rclone OAuth token");
    public string RemoteHelpText => T("支持 Google Drive、OneDrive 和 Dropbox。Host 会先测试连接，成功后才加密保存。", "Supports Google Drive, OneDrive, and Dropbox. The Host tests the connection before encrypted storage.");
    public string TransferFormHeading => T("下载与复制", "Download and copy");
    public string SourceRemoteHint => T("选择来源 Remote", "Select source Remote");
    public string SourcePathHint => T("远程文件或文件夹路径（根目录可留空）", "Remote file or folder path (leave empty for root)");
    public string DownloadFolderHint => T("选择电脑上的下载目录", "Select a download folder on this computer");
    public string PickFolderLabel => T("选择文件夹…", "Choose folder…");
    public string DestinationRemoteHint => T("选择目标 Remote", "Select destination Remote");
    public string DestinationPathHint => T("目标路径", "Destination path");
    public bool IsAdvancedOptionsAvailable => route is "Remotes" or "Transfers";
    public bool IsAdvancedOptionsExpanded => advancedOptionsExpanded;
    public bool IsRemoteAdvancedVisible => IsRemoteJourney && advancedOptionsExpanded;
    public bool IsTransferAdvancedVisible => IsTransferJourney && advancedOptionsExpanded;
    public string AdvancedOptionsLabel => advancedOptionsExpanded ? T("收起高级选项", "Hide advanced options") : T("高级选项", "Advanced options");
    public string TransferModeHeading => T("传输类型", "Transfer mode");
    public string MasterPasswordHint => T("主密码", "Master password");
    public string PageTitle => route switch { "Home" => T("主页", "Home"), "Remotes" => T("远程存储", "Remotes"), "Transfers" => T("传输任务", "Transfer Tasks"), "Browser" => T("文件浏览器", "File Browser"), "Mounts" => T("挂载磁盘", "Mounts"), "Schedules" => T("计划任务", "Schedules"), "Activity" => T("活动与日志", "Activity & Logs"), _ => T("设置", "Settings") };
    public bool IsHome => route == "Home";
    public bool IsJourney => !IsHome;
    public bool IsTransferJourney => route == "Transfers";
    public bool IsRemoteJourney => route == "Remotes";
    public bool IsMountJourney => route == "Mounts";
    public string CurrentRoute => route;
    public string ConnectionLabel => connection switch { DesktopConnectionState.Connecting => T("正在连接…", "Connecting…"), DesktopConnectionState.ConnectedLocked or DesktopConnectionState.ConnectedOperational => T("已自动连接", "Auto-connected"), DesktopConnectionState.ReadOnlyRecovery => T("已连接（恢复模式）", "Connected (recovery)"), _ => T("连接已中断", "Disconnected") };
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
    public DesktopRemoteOption? MountRemote { get; set; }
    public IReadOnlyList<DesktopTransferMode> TransferModes { get; } = [DesktopTransferMode.Download, DesktopTransferMode.RemoteCopy];
    public DesktopTransferMode TransferMode { get => transferMode; set { if (transferMode == value) return; transferMode = value; ChangedAll(); } }
    public bool IsDownloadMode => transferMode == DesktopTransferMode.Download;
    public bool IsRemoteCopyMode => transferMode == DesktopTransferMode.RemoteCopy;
    public string DownloadDestinationPath { get => downloadDestinationPath; set { if (downloadDestinationPath == value) return; downloadDestinationPath = value; ChangedAll(); } }
    public string CopyStatus => copyStatus;
    public string CopySourcePath { get; set; } = string.Empty;
    public string CopyDestinationPath { get; set; } = string.Empty;
    public string? CapabilityBinding => capabilityBinding;
    public IReadOnlyList<string> TokenProviderTypes { get; } = ["drive", "onedrive", "dropbox"];
    public string RemoteDisplayName { get; set; } = string.Empty;
    public string RemoteProviderType { get; set; } = "drive";
    public string RemoteToken { get; set; } = string.Empty;
    public IReadOnlyList<string> MountDriveLetters { get; } = Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => ((char)value).ToString()).ToArray();
    public string MountDriveLetter { get; set; } = "R";
    public string MountSubpath { get; set; } = string.Empty;
    public string MountVolumeName { get; set; } = "Rclone Cloud";
    public string MountFixedDirectoryPath { get => mountFixedDirectoryPath; set { if (mountFixedDirectoryPath == value) return; mountFixedDirectoryPath = value; ChangedAll(); } }
    public IReadOnlyList<DesktopChoice> MountPresentationOptions => mountPresentationOptions;
    public IReadOnlyList<DesktopChoice> MountDriveSelectionOptions => driveSelectionOptions;
    public DesktopChoice MountPresentation { get => mountPresentation; set { if (value is null || mountPresentation.Key == value.Key) return; mountPresentation = value; ChangedAll(); } }
    public DesktopChoice MountDriveSelection { get => mountDriveSelection; set { if (value is null || mountDriveSelection.Key == value.Key) return; mountDriveSelection = value; ChangedAll(); } }
    public bool IsMountDrivePresentation => mountPresentation.Key is "network-drive" or "fixed-drive";
    public bool IsPreferredDriveLetter => IsMountDrivePresentation && mountDriveSelection.Key == "preferred";
    public bool IsFixedDirectoryMount => mountPresentation.Key == "fixed-directory";
    public Guid? ActiveMountId => activeMountId;
    public bool HasActiveMount => activeMountId is not null;
    public bool MountRecoveryRequired => mountLifecycleState == "recovery-required";
    public bool MountNeedsRemount => mountLifecycleState == "needs-remount" && selectedMountProfile?.Id == mountLifecycleProfileId;
    public string MountStatus => mountStatus;
    public string MountRemoteHint => T("选择要挂载的 Remote", "Select a Remote to mount");
    public string MountSubpathHint => T("远程子目录（可留空）", "Remote subfolder (optional)");
    public string MountVolumeNameHint => T("磁盘名称", "Volume name");
    public string MountDriveLetterHeading => T("盘符", "Drive letter");
    public string MountPresentationHeading => T("挂载类型", "Mount type");
    public string MountDriveSelectionHeading => T("盘符分配", "Drive assignment");
    public string MountFixedDirectoryHint => T("选择一个现有的空文件夹", "Choose an existing empty folder");
    public string PickMountDirectoryLabel => T("选择目录…", "Choose directory…");
    public string MountReadOnlyNotice => T("当前安全预设为只读浏览，不会向云端写入文件。", "The current safe preset is read-only browsing and cannot write to the cloud.");
    public string MountCachePresetHeading => T("缓存与写入预设", "Cache and write presets");
    public string MountReadOnlyPresetLabel => T("只读浏览（可用）", "Read-only browsing (available)");
    public string MountReadOnlyPresetDescription => T("不写入云端，不需要本地写入缓存。", "Does not write to the Remote and needs no local write cache.");
    public string MountStandardPresetLabel => T("标准读写（暂不可用）", "Standard read/write (not yet available)");
    public string MountMaximumPresetLabel => T("最大兼容性（暂不可用）", "Maximum compatibility (not yet available)");
    public string MountWritePresetExplanation => T("读写预设将在后台服务能持续观察上传队列、完成安全排空、保留异常缓存并提供恢复证据后开放。", "Write presets will be enabled only after the Background Host can observe upload queues, prove a clean drain, preserve interrupted cache, and provide recovery evidence.");
    public bool MountPrerequisitesReady => winFspStatus == "ready" && rcloneMountAvailable;
    public bool IsJourneyPrimaryEnabled => connection == DesktopConnectionState.ConnectedOperational && (route != "Mounts" || !MountRecoveryRequired && (HasActiveMount || MountPrerequisitesReady && selectedMountProfile is not null));
    public string MountPrerequisiteHeading => T("挂载运行环境", "Mount prerequisites");
    public string MountPrerequisiteStatus => winFspStatus switch
    {
        "ready" when rcloneMountAvailable => T($"WinFsp {winFspVersion} 可用", $"WinFsp {winFspVersion} ready"),
        "missing" => T("未安装 WinFsp", "WinFsp is not installed"),
        "incomplete" => T("WinFsp 安装不完整", "WinFsp installation is incomplete"),
        "ready" => T("rclone 挂载能力不可用", "rclone Mount capability unavailable"),
        _ => T("无法检测 WinFsp", "Unable to detect WinFsp")
    };
    public IBrush MountPrerequisiteBrush => MountPrerequisitesReady ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    public string RedetectLabel => T("重新检测", "Detect again");
    public bool CanInstallWinFsp => winFspStatus is "missing" or "incomplete";
    public string InstallWinFspLabel => T("安装/修复 WinFsp（需要管理员权限）", "Install/repair WinFsp (administrator approval required)");
    public string WinFspStableNotice => T("将下载官方稳定版 WinFsp 2.1.25156。此版本存在已公开的后续安全修复；你已选择继续使用稳定版。", "Downloads official stable WinFsp 2.1.25156. Later public security fixes exist; you chose to continue with the stable release.");
    public IReadOnlyList<DesktopMountProfileOption> MountProfiles => mountProfiles;
    public DesktopMountProfileOption? SelectedMountProfile { get => selectedMountProfile; set { if (selectedMountProfile?.Id == value?.Id) return; selectedMountProfile = value; if (value is not null) ApplyMountProfile(value); ChangedAll(); } }
    public string MountProfileName { get; set; } = string.Empty;
    public bool HasSelectedMountProfile => selectedMountProfile is not null;
    public string MountProfileHint => T("选择已保存的挂载配置", "Select a saved Mount Profile");
    public string MountProfileNameHint => T("配置名称", "Profile name");
    public string SaveMountProfileLabel => selectedMountProfile is null ? T("保存新配置", "Save new profile") : T("保存修改", "Save changes");
    public string DeleteMountProfileLabel => T("删除配置", "Delete profile");
    public string NewMountProfileLabel => T("新建", "New");
    public string JourneyHeading => PageTitle;
    public string JourneyDescription => route switch { "Remotes" => T("通过三步向导添加云端账号；凭据只发送给后台服务并写入加密保险库。", "Add cloud accounts in three guided steps. Credentials go only to the Host and encrypted Vault."), "Transfers" => T("复制、移动或单向镜像。执行前必须接受预览，涉及删除时需要明确确认。", "Copy, move, or one-way mirror. Accept a preview before execution; deletion requires explicit confirmation."), "Mounts" => T("创建稳定盘符的挂载配置。Ready 与安全卸载均需要完整证据。", "Create stable drive profiles. Ready and safe unmount require complete evidence."), "Activity" => T("查看真实结果、部分完成、未知状态和可脱敏导出的诊断信息。", "Review truthful outcomes, partial results, unknown states, and redacted diagnostics."), _ => T("此功能通过后台服务快照和命令工作；界面不直接操作 rclone 或保险库。", "This journey uses Host snapshots and commands; the UI never operates rclone or the Vault directly.") };
    public string JourneyStatus => connection == DesktopConnectionState.ConnectedOperational ? T("可以开始", "Ready") : T("当前不可执行", "Action unavailable");
    public string JourneyActionHint => connection == DesktopConnectionState.ConnectedOperational ? T("普通用户默认值已启用，高级选项按需展开。", "Ordinary-user defaults are active; advanced options remain available on demand.") : AttentionDetail;
    public string JourneyPrimaryAction => route switch { "Remotes" => T("添加远程存储", "Add Remote"), "Transfers" => T("创建并预览", "Create & Preview"), "Mounts" when HasActiveMount => T("安全卸载", "Safe unmount"), "Mounts" when MountNeedsRemount => T("重新挂载", "Remount"), "Mounts" when MountRecoveryRequired => T("需要人工恢复", "Manual recovery required"), "Mounts" => T("只读挂载", "Mount read-only"), "Activity" => T("导出脱敏诊断", "Export Redacted Diagnostics"), _ => T("继续", "Continue") };

    public void Navigate(string value) { route = value; advancedOptionsExpanded = false; ChangedAll(); }
    public void ToggleLanguage() { english = !english; RefreshMountChoices(); ChangedAll(); }
    public void ToggleAdvancedOptions() { if (!IsAdvancedOptionsAvailable) return; advancedOptionsExpanded = !advancedOptionsExpanded; ChangedAll(); }
    public void BeginNewMountProfile()
    {
        selectedMountProfile = null; MountProfileName = string.Empty; MountRemote = remoteOptions.FirstOrDefault(); MountSubpath = string.Empty; MountVolumeName = "Rclone Cloud"; MountDriveLetter = "R"; mountFixedDirectoryPath = string.Empty;
        mountPresentation = mountPresentationOptions.Single(option => option.Key == "network-drive"); mountDriveSelection = driveSelectionOptions.Single(option => option.Key == "preferred"); ChangedAll();
    }
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
            MountRemote = KeepSelection(MountRemote, remoteOptions) ?? remoteOptions.FirstOrDefault();
            remoteSummary = remoteNames.Length == 0 ? T("尚未添加远程存储", "No Remotes added") : string.Join(" · ", remoteNames);
        }
        else { remoteNames = []; remoteOptions = []; CopySourceRemote = null; CopyDestinationRemote = null; MountRemote = null; remoteSummary = T("远程存储状态未知", "Remote state unknown"); }
        capabilityBinding = snapshot.Body.TryGetProperty("rclone", out var rclone) && rclone.TryGetProperty("status", out var status) && status.GetString() == "ready" && rclone.TryGetProperty("capabilityBinding", out var binding) ? binding.GetString() : null;
        rcloneVersion = rclone.ValueKind == JsonValueKind.Object && rclone.TryGetProperty("version", out var rcloneVersionValue) && rcloneVersionValue.ValueKind == JsonValueKind.String ? rcloneVersionValue.GetString() : null;
        rcloneMountAvailable = snapshot.Body.TryGetProperty("rclone", out rclone) && rclone.TryGetProperty("mountAvailable", out var mountAvailable) && mountAvailable.GetBoolean();
        if (snapshot.Body.TryGetProperty("winFsp", out var winFsp))
        {
            winFspStatus = winFsp.TryGetProperty("status", out var detectedStatus) ? detectedStatus.GetString() ?? "unknown" : "unknown";
            winFspVersion = winFsp.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        else { winFspStatus = "unknown"; winFspVersion = null; }
        if (snapshot.Body.TryGetProperty("copyRuns", out var runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0)
        {
            var run = runs.EnumerateArray().Last();
            var state = run.GetProperty("state").GetString() ?? "unknown";
            var bytes = run.GetProperty("bytes").GetInt64(); var total = run.GetProperty("totalBytes").GetInt64(); var speed = run.GetProperty("bytesPerSecond").GetDouble();
            copyStatus = $"{state} · {bytes}/{total} bytes · {speed:F0} B/s";
        }
        else copyStatus = capabilityBinding is null ? T("rclone 不可用", "rclone unavailable") : T("尚未运行 Copy", "No Copy run yet");
        if (snapshot.Body.TryGetProperty("mounts", out var mountRuns) && mountRuns.ValueKind == JsonValueKind.Array && mountRuns.GetArrayLength() > 0)
        {
            var mount = mountRuns.EnumerateArray().Last();
            mountLifecycleState = mount.GetProperty("state").GetString() ?? "unknown";
            mountLifecycleProfileId = mount.TryGetProperty("profileId", out var profileId) && profileId.ValueKind == JsonValueKind.String ? profileId.GetGuid() : null;
            activeMountId = mountLifecycleState == "ready" ? mount.GetProperty("instanceId").GetGuid() : null;
            var point = mount.GetProperty("mountPoint").GetString();
            var diagnostic = mount.TryGetProperty("diagnosticCode", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
            var startedUtc = mount.TryGetProperty("startedUtc", out var started) ? started.GetDateTimeOffset() : DateTimeOffset.UtcNow;
            var uptime = DateTimeOffset.UtcNow - startedUtc;
            var components = $"rclone {rcloneVersion ?? "?"} · WinFsp {winFspVersion ?? "?"}";
            mountStatus = mountLifecycleState switch
            {
                "needs-remount" => T($"上次挂载已中断 · {point} · 可手动重新挂载", $"Previous Mount was interrupted · {point} · remount explicitly"),
                "recovery-required" => T($"需要人工恢复 · {point} · {diagnostic}", $"Manual recovery required · {point} · {diagnostic}"),
                _ => T($"{mountLifecycleState} · {point} · 已运行 {uptime:hh\\:mm\\:ss} · {components}", $"{mountLifecycleState} · {point} · uptime {uptime:hh\\:mm\\:ss} · {components}")
            };
        }
        else { activeMountId = null; mountLifecycleState = "stopped"; mountLifecycleProfileId = null; mountStatus = T("尚未挂载", "Not mounted"); }
        if (snapshot.Body.TryGetProperty("mountProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            var selectedId = selectedMountProfile?.Id;
            mountProfiles = profiles.EnumerateArray().Select(profile => new DesktopMountProfileOption(
                profile.GetProperty("id").GetProperty("value").GetGuid(), profile.GetProperty("revision").GetUInt64(), profile.GetProperty("displayName").GetString()!, profile.GetProperty("remoteId").GetGuid(), profile.GetProperty("subpath").GetString() ?? string.Empty,
                ToPresentationKey(profile.GetProperty("presentationMode")), ToDriveSelectionKey(profile.GetProperty("driveLetterSelection")), profile.GetProperty("preferredDriveLetter").GetString()!,
                profile.TryGetProperty("fixedDirectoryPath", out var directory) && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null, profile.GetProperty("volumeName").GetString()!)).ToArray();
            selectedMountProfile = mountProfiles.FirstOrDefault(profile => profile.Id == selectedId) ?? mountProfiles.FirstOrDefault();
            if (selectedMountProfile is not null) ApplyMountProfile(selectedMountProfile);
        }
        else { mountProfiles = []; selectedMountProfile = null; }
        ChangedAll();
    }
    private string T(string zh, string en) => english ? en : zh;
    private void RefreshMountChoices()
    {
        var presentationKey = mountPresentation?.Key ?? "network-drive";
        var driveKey = mountDriveSelection?.Key ?? "preferred";
        mountPresentationOptions =
        [
            new("network-drive", T("网络驱动器（推荐）", "Network drive (recommended)")),
            new("fixed-drive", T("固定磁盘兼容模式", "Fixed-drive compatibility")),
            new("fixed-directory", T("固定目录", "Fixed directory")),
        ];
        driveSelectionOptions =
        [
            new("preferred", T("指定盘符", "Preferred letter")),
            new("automatic", T("自动分配", "Automatic")),
        ];
        mountPresentation = mountPresentationOptions.Single(option => option.Key == presentationKey);
        mountDriveSelection = driveSelectionOptions.Single(option => option.Key == driveKey);
    }
    private static DesktopRemoteOption? KeepSelection(DesktopRemoteOption? selected, DesktopRemoteOption[] options) => selected is null ? null : options.FirstOrDefault(option => option.Id == selected.Id);
    private void ApplyMountProfile(DesktopMountProfileOption profile)
    {
        MountProfileName = profile.DisplayName; MountRemote = remoteOptions.FirstOrDefault(remote => remote.Id == profile.RemoteId); MountSubpath = profile.Subpath; MountVolumeName = profile.VolumeName; MountDriveLetter = profile.DriveLetter; mountFixedDirectoryPath = profile.FixedDirectoryPath ?? string.Empty;
        mountPresentation = mountPresentationOptions.Single(option => option.Key == profile.PresentationMode);
        mountDriveSelection = driveSelectionOptions.Single(option => option.Key == profile.DriveSelection);
    }
    private static string ToPresentationKey(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() switch { "NetworkDrive" => "network-drive", "FixedDrive" => "fixed-drive", "FixedDirectory" => "fixed-directory", _ => "network-drive" } : value.GetInt32() switch { 1 => "fixed-drive", 2 => "fixed-directory", _ => "network-drive" };
    private static string ToDriveSelectionKey(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() == "Automatic" ? "automatic" : "preferred" : value.GetInt32() == 1 ? "automatic" : "preferred";
    private void ChangedAll() { foreach (var property in GetType().GetProperties().Where(x => x.GetIndexParameters().Length == 0)) PropertyChanged?.Invoke(this, new(property.Name)); }
}
