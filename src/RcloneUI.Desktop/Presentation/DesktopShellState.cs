using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using RcloneUI.Contracts.HostProtocol.V1;

namespace RcloneUI.Desktop.Presentation;

public enum DesktopConnectionState { Connecting, ConnectedLocked, ConnectedOperational, ReadOnlyRecovery, Disconnected }
public enum DesktopTransferMode { Download, Upload, RemoteCopy }
public enum DesktopActionNotificationKind { Success, Error, Information }
public sealed record DesktopRemoteOption(Guid Id, string DisplayName);
public sealed record DesktopSavedRemoteOption(Guid Id, ulong Revision, string DisplayName, string ProviderType, string Health);
public sealed record DesktopCopyRunOption(Guid Id, string State, long Bytes, long TotalBytes)
{
    public string DisplayName => $"{State} · {Bytes}/{TotalBytes} bytes";
}
public sealed record DesktopChoice(string Key, string DisplayName);
public sealed record DesktopMountProfileOption(Guid Id, ulong Revision, string DisplayName, Guid RemoteId, string Subpath, string PresentationMode, string DriveSelection, string DriveLetter, string? FixedDirectoryPath, string VolumeName, string CachePreset);
public sealed record DesktopMountVfsStatus(bool Available, long? BytesUsed, int? ErroredFiles, int? UploadsInProgress, int? UploadsQueued, bool? OutOfSpace, int? QueueItems, DateTimeOffset? ObservedUtc);
public sealed record DesktopBrowserItem(string Path, bool IsDirectory, long? Size) { public string DisplayName => IsDirectory ? $"📁 {Path}" : $"📄 {Path}" + (Size is null ? string.Empty : $"  ({Size} B)"); }

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
    private DesktopSavedRemoteOption[] savedRemotes = [];
    private DesktopSavedRemoteOption? selectedSavedRemote;
    private string remoteDeleteConfirmation = string.Empty;
    private string copyStatus = string.Empty;
    private string? capabilityBinding;
    private string? rcloneVersion;
    private string masterPassword = string.Empty;
    private bool advancedOptionsExpanded;
    private DesktopChoice remoteSetupKind = new("connection", "");
    private DesktopChoice connectionProtocol = new("ftp", "FTP");
    private DesktopTransferMode transferMode;
    private DesktopChoice transferModeChoice = null!;
    private string downloadDestinationPath = string.Empty;
    private string uploadSourcePath = string.Empty;
    private string maximumTransferMiB = string.Empty;
    private string maximumDurationMinutes = string.Empty;
    private Guid? activeMountId;
    private string mountLifecycleState = "stopped";
    private Guid? mountLifecycleProfileId;
    private string mountStatus = string.Empty;
    private bool mountRequiresVfsDrain;
    private DesktopMountVfsStatus? mountVfs;
    private string mountFixedDirectoryPath = string.Empty;
    private DesktopChoice[] mountPresentationOptions = [];
    private DesktopChoice[] driveSelectionOptions = [];
    private DesktopChoice[] mountCachePresetOptions = [];
    private DesktopChoice mountPresentation = null!;
    private DesktopChoice mountDriveSelection = null!;
    private DesktopChoice mountCachePreset = null!;
    private string winFspStatus = "unknown";
    private string? winFspVersion;
    private bool rcloneMountAvailable;
    private DesktopMountProfileOption[] mountProfiles = [];
    private DesktopMountProfileOption? selectedMountProfile;
    private DesktopBrowserItem[] browserItems = [];
    private DesktopBrowserItem[] allBrowserItems = [];
    private string browserFilter = string.Empty;
    private string newBrowserFolderName = string.Empty;
    private string browserDeleteConfirmation = string.Empty;
    private string browserRenameNewName = string.Empty;
    private string[] activityRows = [];
    private DesktopCopyRunOption[] copyRunOptions = [];
    private DesktopCopyRunOption? selectedCopyRun;

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
    public string QuitAllLabel => T("彻底退出（停止后台）", "Quit completely (stop Host)");
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
    public string HomeTransferStatus => CopyStatus;
    public string RemoteHealthHeading => T("远程存储健康", "Remote health");
    public string NoRemoteText => T("尚未添加远程存储", "No Remotes added");
    public string HomeRemoteStatus => RemoteSummary;
    public string OpenRemoteWizardLabel => T("打开三步设置向导", "Open three-step setup");
    public string QuickAddHeading => T("快速添加（高级）", "Quick add (advanced)");
    public string RemoteSetupKindHint => T("添加方式", "Setup method");
    public string ConnectionSetupLabel => T("服务器连接（FTP / FTPS / SFTP）", "Server connection (FTP / FTPS / SFTP)");
    public string TokenSetupLabel => T("云端 OAuth 令牌", "Cloud OAuth token");
    public string RemoteDisplayNameHint => T("显示名称", "Display name");
    public string RemoteTokenHint => T("rclone OAuth 令牌", "rclone OAuth token");
    public string ConnectionRemoteHeading => T("FTP / FTPS / SFTP", "FTP / FTPS / SFTP");
    public string ConnectionProtocolHint => T("协议", "Protocol");
    public string ConnectionHostHint => T("服务器地址或 IP", "Server address or IP");
    public string ConnectionPortHint => T("端口", "Port");
    public string ConnectionUserHint => T("用户名", "Username");
    public string ConnectionPasswordHint => T("密码", "Password");
    public string ConnectionTlsModeHint => T("FTPS 加密方式", "FTPS mode");
    public string ConnectionCertificateHint => T("证书安全", "Certificate security");
    public string ConnectionHostKeyHint => T("SFTP 主机密钥指纹（必填）", "SFTP host-key fingerprint (required)");
    public string ConnectionSecureCertificate => T("验证服务器证书（推荐）", "Verify server certificate (recommended)");
    public string ConnectionInsecureCertificate => T("跳过证书验证（不安全）", "Skip certificate verification (insecure)");
    public string RemoteHelpText => T("支持 Google Drive、OneDrive 和 Dropbox。Host 会先测试连接，成功后才加密保存。", "Supports Google Drive, OneDrive, and Dropbox. The Host tests the connection before encrypted storage.");
    public string SavedRemotesHeading => T("已保存的远程存储", "Saved Remotes");
    public string RemoteDeleteConfirmationHint => T("输入所选名称以删除", "Type the selected name to delete");
    public string DeleteRemoteLabel => T("删除远程存储", "Delete Remote");
    public string TransferFormHeading => T("下载、上传与远程复制", "Download, upload, and remote copy");
    public string SourceRemoteHint => T("选择来源 Remote", "Select source Remote");
    public string SourcePathHint => T("远程文件或文件夹路径（根目录可留空）", "Remote file or folder path (leave empty for root)");
    public string DownloadFolderHint => T("选择电脑上的下载目录", "Select a download folder on this computer");
    public string UploadFolderHint => T("选择电脑上要上传的文件夹", "Select a local folder to upload");
    public string MaximumTransferMiBHint => T("最多传输 MiB（可选）", "Maximum MiB to transfer (optional)");
    public string MaximumDurationMinutesHint => T("最多时长分钟（可选）", "Maximum minutes (optional)");
    public string PickFolderLabel => T("选择文件夹…", "Choose folder…");
    public string DestinationRemoteHint => T("选择目标 Remote", "Select destination Remote");
    public string DestinationPathHint => T("目标路径", "Destination path");
    public bool IsAdvancedOptionsAvailable => route is "Remotes" or "Transfers";
    public bool IsAdvancedOptionsExpanded => advancedOptionsExpanded;
    public bool IsRemoteAdvancedVisible => IsRemoteJourney && advancedOptionsExpanded;
    public bool IsTransferAdvancedVisible => IsTransferJourney && advancedOptionsExpanded;
    public string AdvancedOptionsLabel => advancedOptionsExpanded ? T("收起高级选项", "Hide advanced options") : T("高级选项", "Advanced options");
    public string TransferModeHeading => T("传输类型", "Transfer mode");
    public string TransferModeHelpText => T("选择一种单向传输。移动与镜像尚未在桌面端直接提供。", "Choose one one-way transfer. Direct move and mirror are not yet offered by the desktop app.");
    public string MasterPasswordHint => T("主密码", "Master password");
    public string PageTitle => route switch { "Home" => T("主页", "Home"), "Remotes" => T("远程存储", "Remotes"), "Transfers" => T("传输任务", "Transfer Tasks"), "Browser" => T("文件浏览器", "File Browser"), "Mounts" => T("挂载磁盘", "Mounts"), "Schedules" => T("计划任务", "Schedules"), "Activity" => T("活动与日志", "Activity & Logs"), _ => T("设置", "Settings") };
    public bool IsHome => route == "Home";
    public bool IsJourney => !IsHome;
    public bool IsTransferJourney => route == "Transfers";
    public bool IsRemoteJourney => route == "Remotes";
    public bool IsMountJourney => route == "Mounts";
    public bool IsBrowserJourney => route == "Browser";
    public bool IsActivityJourney => route == "Activity";
    public bool IsSettingsJourney => route == "Settings";
    public string SettingsHostStatus => ConnectionLabel;
    public string SettingsVaultStatus => VaultStatusLabel;
    public string SettingsRcloneStatus => rcloneVersion is null ? T("rclone 不可用", "rclone unavailable") : $"rclone {rcloneVersion}";
    public string SettingsWinFspStatus => MountPrerequisiteStatus;
    public IReadOnlyList<string> ActivityRows => activityRows;
    public IReadOnlyList<DesktopCopyRunOption> CopyRunOptions => copyRunOptions;
    public DesktopCopyRunOption? SelectedCopyRun { get => selectedCopyRun; set { if (selectedCopyRun?.Id == value?.Id) return; selectedCopyRun = value; ChangedAll(); } }
    public bool CanCancelSelectedCopy => selectedCopyRun?.State == "running";
    public string CopyRunHint => T("选择正在运行的传输", "Select a running transfer");
    public string CancelCopyLabel => T("取消所选传输", "Cancel selected transfer");
    public string ActivityEmptyText => activityRows.Length == 0 ? T("尚无后台服务记录的传输或挂载活动。", "No transfer or Mount activity is currently recorded by the Background Host.") : string.Empty;
    public DesktopRemoteOption? BrowserRemote { get; set; }
    public string BrowserPath { get; set; } = string.Empty;
    public IReadOnlyList<DesktopBrowserItem> BrowserItems => browserItems;
    public string BrowserFilter
    {
        get => browserFilter;
        set
        {
            if (browserFilter == value) return;
            browserFilter = value;
            ApplyBrowserFilter();
            ChangedAll();
        }
    }
    public DesktopBrowserItem? SelectedBrowserItem { get; set { field = value; browserDeleteConfirmation = string.Empty; ChangedAll(); } }
    public string NewBrowserFolderName { get => newBrowserFolderName; set { if (newBrowserFolderName == value) return; newBrowserFolderName = value; ChangedAll(); } }
    public string BrowserDeleteConfirmation { get => browserDeleteConfirmation; set { if (browserDeleteConfirmation == value) return; browserDeleteConfirmation = value; ChangedAll(); } }
    public string BrowserRenameNewName { get => browserRenameNewName; set { if (browserRenameNewName == value) return; browserRenameNewName = value; ChangedAll(); } }
    public bool CanOpenBrowserFolder => SelectedBrowserItem?.IsDirectory == true;
    public bool CanUseBrowserSelectionForTransfer => BrowserRemote is not null && SelectedBrowserItem is not null;
    public bool CanDownloadBrowserSelection => BrowserRemote is not null && SelectedBrowserItem is not null;
    public bool CanBrowseParent => !string.IsNullOrWhiteSpace(BrowserPath);
    public bool CanCreateBrowserFolder => BrowserRemote is not null && !string.IsNullOrWhiteSpace(newBrowserFolderName);
    public bool CanDeleteBrowserFile => SelectedBrowserItem is { IsDirectory: false } item && StringComparer.Ordinal.Equals(browserDeleteConfirmation, SelectedBrowserPath(item));
    public bool CanRenameBrowserFile => CanDeleteBrowserFile && !string.IsNullOrWhiteSpace(browserRenameNewName) && !StringComparer.Ordinal.Equals(browserRenameNewName, SelectedBrowserItem!.Path);
    public bool HasBrowserSelection => SelectedBrowserItem is not null;
    public string BrowserSelectionHeading => T("所选项目", "Selected item");
    public string BrowserSelectionDetails => SelectedBrowserItem is not { } item ? T("未选择文件或文件夹。", "No file or folder selected.") : T($"路径：{SelectedBrowserPath(item)}\n类型：{(item.IsDirectory ? "文件夹" : "文件")}\n大小：{(item.Size is null ? "未知" : FormatBytes(item.Size.Value))}", $"Path: {SelectedBrowserPath(item)}\nType: {(item.IsDirectory ? "Folder" : "File")}\nSize: {(item.Size is null ? "Unknown" : FormatBytes(item.Size.Value))}");
    public string BrowserRemoteHint => T("选择要浏览的 Remote", "Select a Remote to browse");
    public string BrowserPathHint => T("目录路径（根目录可留空）", "Folder path (leave empty for root)");
    public string BrowserFilterHint => T("筛选当前目录中的名称", "Filter names in this folder");
    public string BrowserOpenLabel => T("打开文件夹", "Open folder");
    public string BrowserUseAsTransferSourceLabel => T("用作传输来源", "Use as transfer source");
    public string BrowserDownloadSelectedLabel => T("下载所选项目", "Download selected item");
    public string NewBrowserFolderHint => T("新文件夹名称", "New folder name");
    public string CreateBrowserFolderLabel => T("新建文件夹", "Create folder");
    public string BrowserDeleteConfirmationHint => T("输入所选文件的完整路径以删除", "Type the selected file path to delete");
    public string DeleteBrowserFileLabel => T("删除所选文件", "Delete selected file");
    public string BrowserRenameNewNameHint => T("新的文件名", "New file name");
    public string RenameBrowserFileLabel => T("重命名所选文件", "Rename selected file");
    public string BrowserEmptyText => browserItems.Length != 0 ? string.Empty : allBrowserItems.Length != 0 && !string.IsNullOrWhiteSpace(browserFilter) ? T("当前筛选没有匹配项。", "No items match the current filter.") : T("尚未读取目录或目录为空。", "No listing yet, or this folder is empty.");
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
    public bool HasActionNotification => !string.IsNullOrWhiteSpace(lastAction);
    public DesktopActionNotificationKind ActionNotificationKind => lastAction switch
    {
        "remote-added" or "remote-deleted" or "folder-created" or "file-deleted" or "file-renamed" or "copy-accepted" or "copy-cancel-requested" or "mount-started" or "mount-stopped" or "mount-profile-saved" or "mount-profile-deleted" or "vault-unlocked" or "vault-lock-completed" or "shutdown-accepted" or "winfsp-install-complete" or "winfsp-install-complete:restart-required" => DesktopActionNotificationKind.Success,
        "remote-input-invalid" or "remote-host-key-required" or "remote-test-failed" or "remote-delete-confirmation-required" or "remote-delete-blocked-profile" or "remote-delete-conflict" or "remote-delete-unavailable" or "remote-delete-invalid" or "folder-create-invalid" or "folder-create-failed" or "folder-create-unavailable" or "file-delete-confirmation-required" or "file-delete-invalid" or "file-delete-failed" or "file-delete-unavailable" or "file-rename-confirmation-required" or "file-rename-invalid" or "file-rename-failed" or "file-rename-unavailable" or "copy-cancel-invalid" or "copy-cancel-not-running" or "copy-cancel-unavailable" or "copy-cancel-failed" or "vault-locked" or "host-unavailable" or "rclone-unavailable" or "mount-prerequisites-unavailable" or "mount-profile-required" or "mount-drain-not-proved" or "source-remote-required" or "destination-remote-required" or "download-folder-required" or "upload-folder-required" or "transfer-limits-invalid" or "shutdown-blocked-active-mount" or "winfsp-installer-unavailable" or "winfsp-installer-not-started" or "winfsp-download-failed" or "winfsp-hash-mismatch" or "winfsp-signature-invalid" or "winfsp-install-cancelled" or "winfsp-uac-cancelled" => DesktopActionNotificationKind.Error,
        var value when value.StartsWith("winfsp-installer-failed:", StringComparison.Ordinal) || value.StartsWith("winfsp-install-failed:", StringComparison.Ordinal) => DesktopActionNotificationKind.Error,
        _ => DesktopActionNotificationKind.Information
    };
    public IBrush ActionNotificationBrush => ActionNotificationKind switch { DesktopActionNotificationKind.Success => Brushes.MediumSeaGreen, DesktopActionNotificationKind.Error => Brushes.IndianRed, _ => Brushes.DarkOrange };
    public string ActionNotificationTitle => ActionNotificationKind switch
    {
        DesktopActionNotificationKind.Success => T("操作已完成", "Operation completed"),
        DesktopActionNotificationKind.Error => T("操作未完成", "Action not completed"),
        _ => T("操作状态", "Action status")
    };
    public string ActionNotificationMessage => lastAction switch
    {
        "remote-added" => T("远程存储已添加，并已验证连接。", "Remote added and its connection was verified."),
        "remote-deleted" => T("远程存储已删除。", "The Remote was deleted."),
        "folder-created" => T("远程文件夹已创建。", "The remote folder was created."),
        "folder-create-invalid" => T("文件夹名称无效。名称不能包含斜杠，且不能为 . 或 ..。", "The folder name is invalid. It cannot contain a slash or be . or .. ."),
        "folder-create-failed" => T("无法创建远程文件夹。请检查连接和写入权限后重试。", "Could not create the remote folder. Check the connection and write permission, then try again."),
        "file-deleted" => T("远程文件已删除。", "The remote file was deleted."),
        "file-delete-confirmation-required" => T("请先输入所选文件的完整路径以确认删除。", "Type the selected file path to confirm deletion."),
        "file-delete-invalid" => T("只能删除当前目录中选定的单个文件。", "Only one selected file in the current folder can be deleted."),
        "file-delete-failed" => T("无法删除远程文件。请检查连接和写入权限后重试。", "Could not delete the remote file. Check the connection and write permission, then try again."),
        "file-renamed" => T("远程文件已重命名。", "The remote file was renamed."),
        "file-rename-confirmation-required" => T("请确认原文件路径并输入不同的新文件名。", "Confirm the original file path and enter a different new file name."),
        "file-rename-invalid" => T("新文件名无效；只能在当前目录中重命名单个文件。", "The new name is invalid; only one file in the current folder can be renamed."),
        "file-rename-failed" => T("无法重命名远程文件。请检查连接和写入权限后重试。", "Could not rename the remote file. Check the connection and write permission, then try again."),
        "copy-cancel-requested" => T("已请求取消传输，正在等待后台服务确认最终状态。", "Cancellation was requested; waiting for the Background Host to confirm the final state."),
        "copy-cancel-not-running" => T("该传输已结束或不再由后台服务管理。", "That transfer already ended or is no longer managed by the Background Host."),
        "copy-cancel-failed" => T("无法取消传输；请稍后刷新活动状态。", "Could not cancel the transfer. Refresh Activity shortly."),
        "remote-delete-confirmation-required" => T("请先输入所选远程存储的完整名称以确认删除。", "Type the selected Remote's full name to confirm deletion."),
        "remote-delete-blocked-profile" => T("该远程存储仍被挂载配置引用；请先删除或改用其他远程存储。", "This Remote is still referenced by a Mount profile. Delete or change that profile first."),
        "remote-delete-conflict" => T("远程存储已被其他操作更改或删除；请刷新后重试。", "The Remote changed or was deleted elsewhere. Refresh and try again."),
        "vault-lock-completed" => T("保险库已锁定。", "The Vault is now locked."),
        "remote-input-invalid" => T($"请填写：{MissingRemoteSetupFields}。", $"Enter: {MissingRemoteSetupFields}."),
        "remote-host-key-required" => T("SFTP 必须填写服务器主机密钥指纹，不能跳过此验证。", "SFTP requires the server host-key fingerprint; this verification cannot be skipped."),
        "remote-test-failed" => T("无法连接到服务器。请检查地址、端口、协议、账号密码和证书设置。", "Could not connect to the server. Check the address, port, protocol, credentials, and certificate settings."),
        "host-unavailable" => T("无法连接到后台服务。请等待它启动后重试。", "Cannot connect to the Background Host. Wait for it to start, then try again."),
        "vault-locked" => T("保险库已锁定。请先输入主密码解锁。", "The Vault is locked. Enter the Master Password to unlock it first."),
        "rclone-unavailable" => T("rclone 当前不可用。请在设置中检测或更新组件后重试。", "rclone is unavailable. Detect or update the component in Settings, then try again."),
        "mount-prerequisites-unavailable" => T("挂载条件未满足。请确认 WinFsp 已安装且 rclone 挂载能力可用。", "Mount prerequisites are not met. Confirm WinFsp is installed and rclone Mount capability is available."),
        "mount-profile-required" => T("请先保存或选择一个挂载配置。", "Save or select a Mount Profile first."),
        "mount-drain-not-proved" => T("读写挂载仍有上传、错误、空间压力或未知遥测；为避免丢失本地缓存的写入，暂不卸载。", "The writable Mount still has uploads, errors, space pressure, or unknown telemetry, so it remains mounted to avoid losing cached writes."),
        "source-remote-required" => T("请选择来源远程存储。", "Select a source Remote."),
        "destination-remote-required" => T("请选择目标远程存储。", "Select a destination Remote."),
        "download-folder-required" => T("请选择本地下载文件夹。", "Select a local download folder."),
        "upload-folder-required" => T("请选择要上传的本地文件夹。", "Select a local folder to upload."),
        "transfer-limits-invalid" => T("高级传输限制必须是有效的正整数，并且处于允许范围内。", "Advanced transfer limits must be positive whole numbers within the allowed range."),
        "shutdown-blocked-active-mount" => T("仍有正在使用的挂载，无法彻底退出。请先安全卸载。", "A Mount is still active, so the Host cannot stop. Safely unmount it first."),
        "winfsp-install-started" => T("正在下载 WinFsp，随后会请求管理员授权安装。请留意 Windows 的授权窗口。", "Downloading WinFsp, then Windows will request administrator approval. Watch for the UAC prompt."),
        "winfsp-install-complete" => T("WinFsp 已安装完成。正在重新检测挂载条件。", "WinFsp installation completed. Rechecking Mount prerequisites."),
        "winfsp-install-complete:restart-required" => T("WinFsp 已安装完成，需要重启 Windows 后才能使用挂载。", "WinFsp installation completed. Restart Windows before using Mounts."),
        "winfsp-uac-cancelled" => T("你取消了 Windows 管理员授权，WinFsp 未安装。", "Windows administrator approval was cancelled; WinFsp was not installed."),
        "winfsp-download-failed" => T("无法下载 WinFsp。请检查网络后重试。", "WinFsp could not be downloaded. Check the network and retry."),
        "winfsp-hash-mismatch" => T("下载的 WinFsp 校验失败，已拒绝安装。", "The downloaded WinFsp hash did not match, so installation was refused."),
        "winfsp-signature-invalid" => T("WinFsp 数字签名验证失败，已拒绝安装。", "WinFsp signature verification failed, so installation was refused."),
        "winfsp-installer-unavailable" => T("此启动方式无法安装 WinFsp。请使用完整便携包中的 Rclone UI.exe 启动。", "This launch mode cannot install WinFsp. Start from Rclone UI.exe in the full portable package."),
        var value when value.StartsWith("winfsp-installer-failed:", StringComparison.Ordinal) => T("WinFsp 安装器失败，Windows 返回代码：" + value["winfsp-installer-failed:".Length], "WinFsp installer failed with Windows exit code: " + value["winfsp-installer-failed:".Length]),
        var value when value.StartsWith("winfsp-install-failed:", StringComparison.Ordinal) => T("WinFsp 安装准备失败：" + value["winfsp-install-failed:".Length], "WinFsp installation preparation failed: " + value["winfsp-install-failed:".Length]),
        _ => T($"后台服务返回：{lastAction}", $"Background Host result: {lastAction}")
    };
    public string RemoteSummary => remoteSummary;
    public IReadOnlyList<string> RemoteNames => remoteNames;
    public IReadOnlyList<DesktopRemoteOption> RemoteOptions => remoteOptions;
    public DesktopRemoteOption? CopySourceRemote { get; set; }
    public DesktopRemoteOption? CopyDestinationRemote { get; set; }
    public DesktopRemoteOption? MountRemote { get; set; }
    public IReadOnlyList<DesktopSavedRemoteOption> SavedRemotes => savedRemotes;
    public DesktopSavedRemoteOption? SelectedSavedRemote { get => selectedSavedRemote; set { if (selectedSavedRemote?.Id == value?.Id) return; selectedSavedRemote = value; remoteDeleteConfirmation = string.Empty; ChangedAll(); } }
    public string RemoteDeleteConfirmation { get => remoteDeleteConfirmation; set { if (remoteDeleteConfirmation == value) return; remoteDeleteConfirmation = value; ChangedAll(); } }
    public bool CanDeleteSelectedRemote => selectedSavedRemote is not null && StringComparer.Ordinal.Equals(remoteDeleteConfirmation, selectedSavedRemote.DisplayName);
    public IReadOnlyList<DesktopChoice> TransferModeOptions { get; private set; } = [];
    public DesktopChoice TransferModeChoice
    {
        get => transferModeChoice;
        set
        {
            if (transferModeChoice == value) return;
            transferModeChoice = value;
            transferMode = value.Key switch { "upload" => DesktopTransferMode.Upload, "remote-copy" => DesktopTransferMode.RemoteCopy, _ => DesktopTransferMode.Download };
            ChangedAll();
        }
    }
    public DesktopTransferMode TransferMode
    {
        get => transferMode;
        set
        {
            if (transferMode == value) return;
            transferMode = value;
            transferModeChoice = TransferModeOptions.Single(option => option.Key == TransferModeKey(value));
            ChangedAll();
        }
    }
    public bool IsDownloadMode => transferMode == DesktopTransferMode.Download;
    public bool IsUploadMode => transferMode == DesktopTransferMode.Upload;
    public bool IsRemoteCopyMode => transferMode == DesktopTransferMode.RemoteCopy;
    public bool IsRemoteSourceVisible => !IsUploadMode;
    public string DownloadDestinationPath { get => downloadDestinationPath; set { if (downloadDestinationPath == value) return; downloadDestinationPath = value; ChangedAll(); } }
    public string UploadSourcePath { get => uploadSourcePath; set { if (uploadSourcePath == value) return; uploadSourcePath = value; ChangedAll(); } }
    public string MaximumTransferMiB { get => maximumTransferMiB; set { if (maximumTransferMiB == value) return; maximumTransferMiB = value; ChangedAll(); } }
    public string MaximumDurationMinutes { get => maximumDurationMinutes; set { if (maximumDurationMinutes == value) return; maximumDurationMinutes = value; ChangedAll(); } }
    public bool TryGetTransferLimits(out long? maximumTransferBytes, out TimeSpan? maximumDuration)
    {
        maximumTransferBytes = null; maximumDuration = null;
        if (!string.IsNullOrWhiteSpace(maximumTransferMiB))
        {
            if (!long.TryParse(maximumTransferMiB, out var mib) || mib is < 1 or > 1_048_576) return false;
            maximumTransferBytes = checked(mib * 1024 * 1024);
        }
        if (!string.IsNullOrWhiteSpace(maximumDurationMinutes))
        {
            if (!int.TryParse(maximumDurationMinutes, out var minutes) || minutes is < 1 or > 10_080) return false;
            maximumDuration = TimeSpan.FromMinutes(minutes);
        }
        return true;
    }
    public string CopyStatus => copyStatus;
    public string CopySourcePath { get; set; } = string.Empty;
    public string CopyDestinationPath { get; set; } = string.Empty;
    public string? CapabilityBinding => capabilityBinding;
    public IReadOnlyList<DesktopChoice> RemoteSetupKinds => [new("connection", ConnectionSetupLabel), new("token", TokenSetupLabel)];
    public DesktopChoice RemoteSetupKind { get => remoteSetupKind; set { if (value is null || remoteSetupKind.Key == value.Key) return; remoteSetupKind = value; ChangedAll(); } }
    public bool IsConnectionRemoteSetup => remoteSetupKind.Key == "connection";
    public bool IsTokenRemoteSetup => remoteSetupKind.Key == "token";
    public string RemoteSetupRequiredFields => IsTokenRemoteSetup
        ? T("远程存储名称、OAuth 令牌", "Remote name and OAuth token")
        : IsSftpConnection
            ? T("远程存储名称、服务器地址、端口、用户名、密码、SFTP 主机密钥指纹", "Remote name, server address, port, username, password, and SFTP host-key fingerprint")
            : T("远程存储名称、服务器地址、端口、用户名、密码", "Remote name, server address, port, username, and password");
    private string MissingRemoteSetupFields => IsTokenRemoteSetup
        ? string.Join("、", new[] { string.IsNullOrWhiteSpace(RemoteDisplayName) ? T("远程存储名称", "Remote name") : null, string.IsNullOrWhiteSpace(RemoteToken) ? T("OAuth 令牌", "OAuth token") : null }.Where(value => value is not null))
        : string.Join("、", new[] { string.IsNullOrWhiteSpace(RemoteDisplayName) ? T("远程存储名称", "Remote name") : null, string.IsNullOrWhiteSpace(ConnectionHost) ? T("服务器地址", "server address") : null, !int.TryParse(ConnectionPort, out var port) || port is < 1 or > 65535 ? T("有效端口", "valid port") : null, string.IsNullOrWhiteSpace(ConnectionUser) ? T("用户名", "username") : null, string.IsNullOrWhiteSpace(ConnectionPassword) ? T("密码", "password") : null, IsSftpConnection && string.IsNullOrWhiteSpace(ConnectionHostKeyFingerprint) ? T("SFTP 主机密钥指纹", "SFTP host-key fingerprint") : null }.Where(value => value is not null));
    public bool IsSftpConnection => IsConnectionRemoteSetup && connectionProtocol.Key == "sftp";
    public bool IsFtpsConnection => IsConnectionRemoteSetup && connectionProtocol.Key is "ftps-explicit" or "ftps-implicit";
    public bool IsSftpHostKeyVisible => IsSftpConnection;
    public string RemoteSetupHelpText => IsTokenRemoteSetup
        ? T("适用于 Google Drive、OneDrive 和 Dropbox。请粘贴 rclone OAuth 令牌。", "For Google Drive, OneDrive, and Dropbox. Paste an rclone OAuth token.")
        : T("仅需填写标记为必填的字段。FTPS 默认验证服务器证书；SFTP 必须验证主机密钥。", "Only fields marked required are needed. FTPS verifies the server certificate by default; SFTP must verify its host key.");
    public IReadOnlyList<string> TokenProviderTypes { get; } = ["drive", "onedrive", "dropbox"];
    public string RemoteDisplayName { get; set; } = string.Empty;
    public string RemoteProviderType { get; set; } = "drive";
    public string RemoteToken { get; set; } = string.Empty;
    public IReadOnlyList<DesktopChoice> ConnectionProtocols { get; } = [new("ftp", "FTP"), new("ftps-explicit", "FTPS (Explicit TLS)"), new("ftps-implicit", "FTPS (Implicit TLS)"), new("sftp", "SFTP")];
    public DesktopChoice ConnectionProtocol
    {
        get => connectionProtocol;
        set
        {
            if (value is null || connectionProtocol.Key == value.Key) return;
            var oldPort = connectionProtocol.Key == "sftp" ? "22" : connectionProtocol.Key == "ftps-implicit" ? "990" : "21";
            connectionProtocol = value;
            if (ConnectionPort == oldPort) ConnectionPort = value.Key == "sftp" ? "22" : value.Key == "ftps-implicit" ? "990" : "21";
            ChangedAll();
        }
    }
    public string ConnectionHost { get; set; } = string.Empty;
    public string ConnectionPort { get; set; } = "21";
    public string ConnectionUser { get; set; } = string.Empty;
    public string ConnectionPassword { get; set; } = string.Empty;
    public string ConnectionHostKeyFingerprint { get; set; } = string.Empty;
    public bool ConnectionSkipCertificateVerification { get; set; }
    public IReadOnlyList<string> MountDriveLetters { get; } = Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => ((char)value).ToString()).ToArray();
    public string MountDriveLetter { get; set; } = "R";
    public string MountSubpath { get; set; } = string.Empty;
    public string MountVolumeName { get; set; } = "Rclone Cloud";
    public string MountFixedDirectoryPath { get => mountFixedDirectoryPath; set { if (mountFixedDirectoryPath == value) return; mountFixedDirectoryPath = value; ChangedAll(); } }
    public IReadOnlyList<DesktopChoice> MountPresentationOptions => mountPresentationOptions;
    public IReadOnlyList<DesktopChoice> MountDriveSelectionOptions => driveSelectionOptions;
    public DesktopChoice MountPresentation { get => mountPresentation; set { if (value is null || mountPresentation.Key == value.Key) return; mountPresentation = value; ChangedAll(); } }
    public DesktopChoice MountDriveSelection { get => mountDriveSelection; set { if (value is null || mountDriveSelection.Key == value.Key) return; mountDriveSelection = value; ChangedAll(); } }
    public IReadOnlyList<DesktopChoice> MountCachePresetOptions => mountCachePresetOptions;
    public DesktopChoice MountCachePreset { get => mountCachePreset; set { if (value is null || mountCachePreset.Key == value.Key) return; mountCachePreset = value; ChangedAll(); } }
    public bool IsMountDrivePresentation => mountPresentation.Key is "network-drive" or "fixed-drive";
    public bool IsPreferredDriveLetter => IsMountDrivePresentation && mountDriveSelection.Key == "preferred";
    public bool IsFixedDirectoryMount => mountPresentation.Key == "fixed-directory";
    public Guid? ActiveMountId => activeMountId;
    public bool HasActiveMount => activeMountId is not null;
    public bool MountRecoveryRequired => mountLifecycleState == "recovery-required";
    public bool MountNeedsRemount => mountLifecycleState == "needs-remount" && selectedMountProfile?.Id == mountLifecycleProfileId;
    public string MountStatus => mountStatus;
    public string MountVfsHeading => T("读写缓存与上传状态", "Read/write cache and upload status");
    public string MountVfsStatus => !HasActiveMount
        ? T("尚未挂载。", "Not mounted.")
        : !mountRequiresVfsDrain
            ? T("只读挂载不会产生本地写入缓存。", "The read-only Mount has no local write-back cache.")
            : mountVfs is not { Available: true } || !HasCompleteVfsObservation(mountVfs)
                ? T("上传状态未知；安全卸载会保守地拒绝，以保护本地缓存中的写入。", "Upload state is unknown; safe unmount will conservatively refuse to protect cached writes.")
                : mountVfs.ErroredFiles > 0 || mountVfs.OutOfSpace == true
                    ? T($"检测到上传错误或空间压力（错误 {mountVfs.ErroredFiles}）；请先处理后再安全卸载。", $"Upload errors or space pressure detected (errors {mountVfs.ErroredFiles}); resolve them before safe unmount.")
                    : mountVfs.UploadsInProgress > 0 || mountVfs.UploadsQueued > 0 || mountVfs.QueueItems > 0
                        ? T($"正在上传或排队：上传中 {mountVfs.UploadsInProgress}，待上传 {mountVfs.UploadsQueued}，队列 {mountVfs.QueueItems}。", $"Uploads are active or queued: active {mountVfs.UploadsInProgress}, queued {mountVfs.UploadsQueued}, queue {mountVfs.QueueItems}.")
                        : T($"当前观察到缓存 {FormatBytes(mountVfs.BytesUsed!.Value)}，上传队列为空。Host 会在卸载前再次核验。", $"Currently observed cache {FormatBytes(mountVfs.BytesUsed!.Value)} with an empty upload queue. The Host will verify again before unmount.");
    public IBrush MountVfsStatusBrush => !HasActiveMount || !mountRequiresVfsDrain ? Brushes.Gray : mountVfs is not { Available: true } || !HasCompleteVfsObservation(mountVfs) || mountVfs.ErroredFiles > 0 || mountVfs.OutOfSpace == true ? Brushes.IndianRed : mountVfs.UploadsInProgress > 0 || mountVfs.UploadsQueued > 0 || mountVfs.QueueItems > 0 ? Brushes.DarkOrange : Brushes.MediumSeaGreen;
    public string MountRemoteHint => T("选择要挂载的 Remote", "Select a Remote to mount");
    public string MountSubpathHint => T("远程子目录（可留空）", "Remote subfolder (optional)");
    public string MountVolumeNameHint => T("磁盘名称", "Volume name");
    public string MountDriveLetterHeading => T("盘符", "Drive letter");
    public string MountPresentationHeading => T("挂载类型", "Mount type");
    public string MountDriveSelectionHeading => T("盘符分配", "Drive assignment");
    public string MountFixedDirectoryHint => T("选择一个现有的空文件夹", "Choose an existing empty folder");
    public string PickMountDirectoryLabel => T("选择目录…", "Choose directory…");
    public string MountReadOnlyNotice => mountCachePreset.Key == "standard-read-write"
        ? T("读写模式先写入 data/cache/rclone，再由 rclone 上传。安全卸载会等待上传队列清空；本地写入不等于云端已完成。", "Read/write first writes to data/cache/rclone, then rclone uploads. Safe unmount waits for an empty upload queue; a local write is not a completed cloud write.")
        : T("当前安全预设为只读浏览，不会向云端写入文件。", "The current safe preset is read-only browsing and cannot write to the cloud.");
    public string MountCachePresetHeading => T("缓存与写入预设", "Cache and write presets");
    public string MountReadOnlyPresetLabel => T("只读浏览（可用）", "Read-only browsing (available)");
    public string MountReadOnlyPresetDescription => T("不写入云端，不需要本地写入缓存。", "Does not write to the Remote and needs no local write cache.");
    public string MountStandardPresetLabel => T("标准读写（可用）", "Standard read/write (available)");
    public string MountMaximumPresetLabel => T("最大兼容性（暂不可用）", "Maximum compatibility (not yet available)");
    public string MountWritePresetExplanation => T("10 GiB 是缓存软目标；队列、上传错误或空间不足时，安全卸载会拒绝执行并保留挂载。", "10 GiB is a soft cache target; safe unmount refuses while uploads, errors, or space pressure remain.");
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
    public string JourneyDescription => route switch { "Remotes" => T("通过三步向导添加云端账号；凭据只发送给后台服务并写入加密保险库。", "Add cloud accounts in three guided steps. Credentials go only to the Host and encrypted Vault."), "Transfers" => T("下载、上传或远程复制。每次操作都是单向复制，不会删除来源或执行镜像。", "Download, upload, or copy between Remotes. Each action is a one-way copy; it does not delete the source or mirror a destination."), "Mounts" => T("创建稳定盘符的挂载配置。Ready 与安全卸载均需要完整证据。", "Create stable drive profiles. Ready and safe unmount require complete evidence."), "Activity" => T("查看真实结果、部分完成、未知状态和可脱敏导出的诊断信息。", "Review truthful outcomes, partial results, unknown states, and redacted diagnostics."), _ => T("此功能通过后台服务快照和命令工作；界面不直接操作 rclone 或保险库。", "This journey uses Host snapshots and commands; the UI never operates rclone or the Vault directly.") };
    public string JourneyStatus => connection == DesktopConnectionState.ConnectedOperational ? T("可以开始", "Ready") : T("当前不可执行", "Action unavailable");
    public string JourneyActionHint => connection == DesktopConnectionState.ConnectedOperational ? T("普通用户默认值已启用，高级选项按需展开。", "Ordinary-user defaults are active; advanced options remain available on demand.") : AttentionDetail;
    public string JourneyPrimaryAction => route switch { "Remotes" => T("添加远程存储", "Add Remote"), "Browser" => T("读取目录", "List folder"), "Transfers" when TransferMode == DesktopTransferMode.Download => T("开始下载", "Start download"), "Transfers" when TransferMode == DesktopTransferMode.Upload => T("开始上传", "Start upload"), "Transfers" => T("开始复制", "Start copy"), "Mounts" when HasActiveMount => T("安全卸载", "Safe unmount"), "Mounts" when MountNeedsRemount => T("重新挂载", "Remount"), "Mounts" when MountRecoveryRequired => T("需要人工恢复", "Manual recovery required"), "Mounts" when mountCachePreset.Key == "standard-read-write" => T("读写挂载", "Mount read/write"), "Mounts" => T("只读挂载", "Mount read-only"), "Activity" => T("导出脱敏诊断", "Export Redacted Diagnostics"), _ => T("继续", "Continue") };

    public void Navigate(string value) { route = value; advancedOptionsExpanded = false; ChangedAll(); }
    public void ToggleLanguage() { english = !english; RefreshMountChoices(); ChangedAll(); }
    public void ToggleAdvancedOptions() { if (!IsAdvancedOptionsAvailable) return; advancedOptionsExpanded = !advancedOptionsExpanded; ChangedAll(); }
    public void BeginNewMountProfile()
    {
        selectedMountProfile = null; MountProfileName = string.Empty; MountRemote = remoteOptions.FirstOrDefault(); MountSubpath = string.Empty; MountVolumeName = "Rclone Cloud"; MountDriveLetter = "R"; mountFixedDirectoryPath = string.Empty;
        mountPresentation = mountPresentationOptions.Single(option => option.Key == "network-drive"); mountDriveSelection = driveSelectionOptions.Single(option => option.Key == "preferred"); mountCachePreset = mountCachePresetOptions.Single(option => option.Key == "read-only"); ChangedAll();
    }
    public void ApplyConnection(DesktopConnectionState value) { connection = value; ChangedAll(); }
    public void ApplyAction(string value) { lastAction = value; ChangedAll(); }
    public void ApplySnapshot(HostSnapshot snapshot)
    {
        connection = snapshot.Body.TryGetProperty("session", out var session) ? session.GetString() switch { "operational" => DesktopConnectionState.ConnectedOperational, "locked" => DesktopConnectionState.ConnectedLocked, "read-only-recovery" => DesktopConnectionState.ReadOnlyRecovery, _ => DesktopConnectionState.ReadOnlyRecovery } : DesktopConnectionState.ReadOnlyRecovery;
        if (snapshot.Body.TryGetProperty("remotes", out var remotes) && remotes.ValueKind == JsonValueKind.Array)
        {
            remoteNames = remotes.EnumerateArray().Select(item => item.TryGetProperty("displayName", out var name) ? name.GetString() : null).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToArray();
            savedRemotes = remotes.EnumerateArray().Select(item => new DesktopSavedRemoteOption(item.GetProperty("id").GetGuid(), item.TryGetProperty("revision", out var remoteRevision) && remoteRevision.TryGetUInt64(out var parsedRevision) ? parsedRevision : 0, item.GetProperty("displayName").GetString()!, item.TryGetProperty("providerType", out var provider) ? provider.GetString() ?? "unknown" : "unknown", item.TryGetProperty("health", out var health) ? health.GetString() ?? "Unknown" : "Unknown")).ToArray();
            selectedSavedRemote = selectedSavedRemote is null ? savedRemotes.FirstOrDefault() : savedRemotes.FirstOrDefault(item => item.Id == selectedSavedRemote.Id);
            remoteOptions = savedRemotes.Select(item => new DesktopRemoteOption(item.Id, item.DisplayName)).ToArray();
            CopySourceRemote = KeepSelection(CopySourceRemote, remoteOptions) ?? remoteOptions.FirstOrDefault();
            CopyDestinationRemote = KeepSelection(CopyDestinationRemote, remoteOptions) ?? remoteOptions.Skip(1).FirstOrDefault() ?? remoteOptions.FirstOrDefault();
            MountRemote = KeepSelection(MountRemote, remoteOptions) ?? remoteOptions.FirstOrDefault();
            BrowserRemote = KeepSelection(BrowserRemote, remoteOptions) ?? remoteOptions.FirstOrDefault();
            remoteSummary = remoteNames.Length == 0 ? T("尚未添加远程存储", "No Remotes added") : string.Join(" · ", remoteNames);
        }
        else { remoteNames = []; remoteOptions = []; savedRemotes = []; selectedSavedRemote = null; CopySourceRemote = null; CopyDestinationRemote = null; MountRemote = null; remoteSummary = T("远程存储状态未知", "Remote state unknown"); }
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
            mountRequiresVfsDrain = mount.TryGetProperty("requiresVfsDrain", out var requiresVfsDrain) && requiresVfsDrain.ValueKind == JsonValueKind.True;
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
        else { activeMountId = null; mountLifecycleState = "stopped"; mountLifecycleProfileId = null; mountRequiresVfsDrain = false; mountStatus = T("尚未挂载", "Not mounted"); }
        mountVfs = null;
        if (activeMountId is { } activeId && snapshot.Body.TryGetProperty("mountVfs", out var vfsEntries) && vfsEntries.ValueKind == JsonValueKind.Array)
        {
            var entry = vfsEntries.EnumerateArray().FirstOrDefault(item => item.TryGetProperty("instanceId", out var id) && id.GetGuid() == activeId);
            if (entry.ValueKind == JsonValueKind.Object)
                mountVfs = new(entry.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True, ReadNullableInt64(entry, "bytesUsed"), ReadNullableInt32(entry, "erroredFiles"), ReadNullableInt32(entry, "uploadsInProgress"), ReadNullableInt32(entry, "uploadsQueued"), ReadNullableBool(entry, "outOfSpace"), ReadNullableInt32(entry, "queueItems"), ReadNullableDateTimeOffset(entry, "observedUtc"));
        }
        if (snapshot.Body.TryGetProperty("mountProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            var selectedId = selectedMountProfile?.Id;
            mountProfiles = profiles.EnumerateArray().Select(profile => new DesktopMountProfileOption(
                profile.GetProperty("id").GetProperty("value").GetGuid(), profile.GetProperty("revision").GetUInt64(), profile.GetProperty("displayName").GetString()!, profile.GetProperty("remoteId").GetGuid(), profile.GetProperty("subpath").GetString() ?? string.Empty,
                ToPresentationKey(profile.GetProperty("presentationMode")), ToDriveSelectionKey(profile.GetProperty("driveLetterSelection")), profile.GetProperty("preferredDriveLetter").GetString()!,
                profile.TryGetProperty("fixedDirectoryPath", out var directory) && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null, profile.GetProperty("volumeName").GetString()!, ToCachePresetKey(profile.GetProperty("cachePreset")))).ToArray();
            selectedMountProfile = mountProfiles.FirstOrDefault(profile => profile.Id == selectedId) ?? mountProfiles.FirstOrDefault();
            if (selectedMountProfile is not null) ApplyMountProfile(selectedMountProfile);
        }
        else { mountProfiles = []; selectedMountProfile = null; }
        var activities = new List<string>();
        if (snapshot.Body.TryGetProperty("copyRuns", out var activityCopies) && activityCopies.ValueKind == JsonValueKind.Array)
        {
            copyRunOptions = activityCopies.EnumerateArray().Where(run => run.TryGetProperty("runId", out var runId) && runId.ValueKind == JsonValueKind.String && Guid.TryParse(runId.GetString(), out _)).Select(run => new DesktopCopyRunOption(run.GetProperty("runId").GetGuid(), run.GetProperty("state").GetString() ?? "unknown", run.GetProperty("bytes").GetInt64(), run.GetProperty("totalBytes").GetInt64())).ToArray();
            selectedCopyRun = selectedCopyRun is null ? copyRunOptions.LastOrDefault(run => run.State == "running") : copyRunOptions.FirstOrDefault(run => run.Id == selectedCopyRun.Id);
            activities.AddRange(activityCopies.EnumerateArray().TakeLast(20).Select(run => $"Copy · {run.GetProperty("state").GetString() ?? "unknown"} · {run.GetProperty("bytes").GetInt64()}/{run.GetProperty("totalBytes").GetInt64()} bytes"));
        }
        else { copyRunOptions = []; selectedCopyRun = null; }
        if (snapshot.Body.TryGetProperty("mounts", out var activityMounts) && activityMounts.ValueKind == JsonValueKind.Array)
            activities.AddRange(activityMounts.EnumerateArray().TakeLast(20).Select(mount => $"Mount · {mount.GetProperty("state").GetString() ?? "unknown"} · {mount.GetProperty("mountPoint").GetString() ?? "?"}"));
        activityRows = activities.TakeLast(40).ToArray();
        ChangedAll();
    }
    private string T(string zh, string en) => english ? en : zh;
    private void RefreshMountChoices()
    {
        var transferModeKey = TransferModeKey(transferMode);
        TransferModeOptions =
        [
            new("download", T("下载到此电脑", "Download to this computer")),
            new("upload", T("上传本地文件夹", "Upload a local folder")),
            new("remote-copy", T("远程存储之间复制", "Copy between Remotes"))
        ];
        transferModeChoice = TransferModeOptions.Single(option => option.Key == transferModeKey);
        var presentationKey = mountPresentation?.Key ?? "network-drive";
        var driveKey = mountDriveSelection?.Key ?? "preferred";
        var cachePresetKey = mountCachePreset?.Key ?? "read-only";
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
        mountCachePresetOptions =
        [
            new("read-only", T("只读浏览", "Read-only browsing")),
            new("standard-read-write", T("标准读写（10 GiB 缓存软目标）", "Standard read/write (10 GiB soft cache target)")),
        ];
        mountPresentation = mountPresentationOptions.Single(option => option.Key == presentationKey);
        mountDriveSelection = driveSelectionOptions.Single(option => option.Key == driveKey);
        mountCachePreset = mountCachePresetOptions.Single(option => option.Key == cachePresetKey);
    }
    private static string TransferModeKey(DesktopTransferMode mode) => mode switch
    {
        DesktopTransferMode.Upload => "upload",
        DesktopTransferMode.RemoteCopy => "remote-copy",
        _ => "download"
    };
    private static DesktopRemoteOption? KeepSelection(DesktopRemoteOption? selected, DesktopRemoteOption[] options) => selected is null ? null : options.FirstOrDefault(option => option.Id == selected.Id);
    private void ApplyMountProfile(DesktopMountProfileOption profile)
    {
        MountProfileName = profile.DisplayName; MountRemote = remoteOptions.FirstOrDefault(remote => remote.Id == profile.RemoteId); MountSubpath = profile.Subpath; MountVolumeName = profile.VolumeName; MountDriveLetter = profile.DriveLetter; mountFixedDirectoryPath = profile.FixedDirectoryPath ?? string.Empty;
        mountPresentation = mountPresentationOptions.Single(option => option.Key == profile.PresentationMode);
        mountDriveSelection = driveSelectionOptions.Single(option => option.Key == profile.DriveSelection);
        mountCachePreset = mountCachePresetOptions.Single(option => option.Key == profile.CachePreset);
    }
    private static string ToPresentationKey(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() switch { "NetworkDrive" => "network-drive", "FixedDrive" => "fixed-drive", "FixedDirectory" => "fixed-directory", _ => "network-drive" } : value.GetInt32() switch { 1 => "fixed-drive", 2 => "fixed-directory", _ => "network-drive" };
    private static string ToDriveSelectionKey(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() == "Automatic" ? "automatic" : "preferred" : value.GetInt32() == 1 ? "automatic" : "preferred";
    private static string ToCachePresetKey(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() == "StandardReadWrite" ? "standard-read-write" : "read-only" : value.GetInt32() == 1 ? "standard-read-write" : "read-only";
    private static bool HasCompleteVfsObservation(DesktopMountVfsStatus value) => value.BytesUsed is not null && value.ErroredFiles is not null && value.UploadsInProgress is not null && value.UploadsQueued is not null && value.OutOfSpace is not null && value.QueueItems is not null;
    private static long? ReadNullableInt64(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var result) ? result : null;
    private static int? ReadNullableInt32(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var result) ? result : null;
    private static bool? ReadNullableBool(JsonElement value, string property) => value.TryGetProperty(property, out var item) && (item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.False) ? item.GetBoolean() : null;
    private static DateTimeOffset? ReadNullableDateTimeOffset(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out var result) ? result : null;
    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):F1} GiB" : value >= 1024L * 1024 ? $"{value / (1024d * 1024):F1} MiB" : $"{value} B";
    public void BrowseParent()
    {
        var normalized = BrowserPath.Trim('/');
        var separator = normalized.LastIndexOf('/');
        BrowserPath = separator < 0 ? string.Empty : normalized[..separator];
        ChangedAll();
    }
    public void OpenSelectedBrowserFolder()
    {
        if (SelectedBrowserItem is not { IsDirectory: true } item) return;
        BrowserPath = string.Join('/', new[] { BrowserPath.Trim('/'), item.Path.Trim('/') }.Where(value => value.Length > 0));
        SelectedBrowserItem = null;
        ChangedAll();
    }
    public void UseBrowserSelectionForTransfer()
    {
        if (BrowserRemote is null || SelectedBrowserItem is null) return;
        CopySourceRemote = BrowserRemote;
        CopySourcePath = string.Join('/', new[] { BrowserPath.Trim('/'), SelectedBrowserItem.Path.Trim('/') }.Where(value => value.Length > 0));
        Navigate("Transfers");
    }
    public void PrepareBrowserSelectionForDownload()
    {
        if (BrowserRemote is null || SelectedBrowserItem is null) return;
        CopySourceRemote = BrowserRemote;
        CopySourcePath = SelectedBrowserPath(SelectedBrowserItem);
        DownloadDestinationPath = string.Empty;
        TransferMode = DesktopTransferMode.Download;
        Navigate("Transfers");
    }
    public string SelectedBrowserPath(DesktopBrowserItem item) => string.Join('/', new[] { BrowserPath.Trim('/'), item.Path.Trim('/') }.Where(value => value.Length > 0));
    public void ApplyBrowserItems(IEnumerable<DesktopBrowserItem> items)
    {
        var selectedPath = SelectedBrowserItem?.Path;
        allBrowserItems = items.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        ApplyBrowserFilter();
        SelectedBrowserItem = selectedPath is null ? null : browserItems.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Path, selectedPath));
        ChangedAll();
    }
    private void ApplyBrowserFilter()
    {
        browserItems = string.IsNullOrWhiteSpace(browserFilter) ? allBrowserItems : allBrowserItems.Where(item => item.Path.Contains(browserFilter.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (SelectedBrowserItem is { } selected && !browserItems.Any(item => StringComparer.Ordinal.Equals(item.Path, selected.Path))) SelectedBrowserItem = null;
    }
    private void ChangedAll() { foreach (var property in GetType().GetProperties().Where(x => x.GetIndexParameters().Length == 0)) PropertyChanged?.Invoke(this, new(property.Name)); }
}
