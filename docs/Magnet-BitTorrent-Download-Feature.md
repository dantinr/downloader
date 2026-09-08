# Feature / Product Spec: Magnet & BitTorrent Download
## Multi-Protocol Downloader 的 Magnet / Torrent 下载模块

> 目标：为现有下载器增加 Magnet Link 和 `.torrent` 文件支持，并将其纳入统一的下载任务模型、任务队列、历史记录、限速、持久化和统计系统。
>
> 第一阶段不从零实现 BitTorrent 协议栈，优先基于成熟 .NET BitTorrent 引擎完成产品能力。推荐封装 MonoTorrent，不让 UI 和业务层直接依赖具体第三方库。

---

# 1. 产品定位

## 一句话定义

> 为现有下载器增加一个可持续、可恢复、可统计的 Magnet / BitTorrent 下载引擎。

它不是一个独立 Torrent 客户端。

它应该成为：

```text
Multi-Protocol Download Manager

├── HTTP / HTTPS
├── HLS
├── DASH
├── Magnet
└── Torrent
```

用户面对的是统一的：

```text
Download Task
```

而不是完全不同的产品。

---

# 2. 产品目标

第一阶段必须让用户完成：

```text
Paste Magnet
↓
Parse
↓
Find Peers
↓
Download Metadata
↓
Show Files
↓
Select Files
↓
Download
↓
Pause / Resume
↓
Close App
↓
Open App
↓
Continue
```

并且可以清楚看到：

```text
Progress
Download Speed
Upload Speed
Peer Count
Seed Count
Availability
ETA
Downloaded
Uploaded
State
```

---

# 3. 第一阶段核心场景

## 3.1 Magnet Link

用户粘贴：

```text
magnet:?xt=urn:btih:...
```

系统：

```text
Validate
↓
Create Torrent Task
↓
Start Metadata Discovery
↓
Show "Fetching Metadata"
↓
Metadata Ready
↓
Show File Tree
↓
Select Files
↓
Start Download
```

## 3.2 .torrent 文件

用户可以：

```text
Open .torrent
```

或：

```text
Drag & Drop .torrent
```

系统直接：

```text
Parse Metadata
↓
Show File Tree
↓
Select
↓
Download
```

因为 `.torrent` 已包含 metadata，所以无需等待 Magnet Metadata Discovery。

---

# 4. MVP 功能

## P0 必须完成

- [ ] Magnet Link 解析
- [ ] `.torrent` 文件导入
- [ ] Metadata 下载
- [ ] Metadata 状态显示
- [ ] 文件树
- [ ] 文件选择
- [ ] 下载目录选择
- [ ] Start
- [ ] Pause
- [ ] Resume
- [ ] Stop
- [ ] Remove Task
- [ ] Progress
- [ ] Download Speed
- [ ] Upload Speed
- [ ] Peer Count
- [ ] Seed Count
- [ ] Downloaded Bytes
- [ ] Uploaded Bytes
- [ ] ETA
- [ ] DHT 状态
- [ ] Tracker 状态
- [ ] Task Persistence
- [ ] App 重启恢复任务
- [ ] Download / Upload Limit
- [ ] Error Message
- [ ] Logging

---

# 5. 第一版明确不做

MVP 不做：

- 自己实现 BitTorrent Peer Wire Protocol
- 自己实现 DHT
- 自己实现 Tracker Protocol
- 自己实现 PEX
- 自己实现 Piece Picker
- 自己实现 Piece Hash Verification
- 自己实现 NAT traversal
- 自己实现 UPnP
- Streaming playback
- Sequential streaming optimization
- Remote Web UI
- Cloud download
- Seedbox
- Search engine
- Torrent resource index
- Built-in resource discovery
- User account
- Cloud sync
- AI recommendation

这些能力以后如有需要再评估。

---

# 6. 推荐技术方案

优先使用：

```text
.NET
C#
MonoTorrent
SQLite
```

注意：

> MonoTorrent 必须放在 Infrastructure / Adapter 层。

UI 和 Core 不允许直接依赖 MonoTorrent 类型。

---

# 7. 总体架构

```text
UI
│
▼
Application
│
├── DownloadManager
├── DownloadQueue
├── DownloadScheduler
└── TaskCoordinator
│
▼
Core
│
├── DownloadTask
├── DownloadState
├── DownloadProgress
├── DownloadStatistics
└── Protocol Abstractions
│
▼
Protocol Adapters
│
├── Http
├── Hls
├── Dash
└── BitTorrent
    └── MonoTorrentAdapter
│
▼
Storage
└── SQLite
```

---

# 8. 统一 DownloadTask 模型

不要为 Magnet 完全重新做一套任务系统。

```csharp
public abstract class DownloadTask
{
    public Guid Id { get; init; }
    public DownloadProtocol Protocol { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public DownloadState State { get; set; }
    public string SavePath { get; set; } = string.Empty;
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double DownloadSpeedBytesPerSecond { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
```

扩展：

```csharp
public sealed class TorrentDownloadTask : DownloadTask
{
    public string? MagnetUri { get; set; }
    public string? TorrentFilePath { get; set; }
    public string? InfoHash { get; set; }
    public TorrentMetadataState MetadataState { get; set; }
    public long UploadedBytes { get; set; }
    public double UploadSpeedBytesPerSecond { get; set; }
    public int PeerCount { get; set; }
    public int SeedCount { get; set; }
    public double? Availability { get; set; }
}
```

---

# 9. Protocol Abstraction

```csharp
public interface IDownloadEngine
{
    DownloadProtocol Protocol { get; }

    Task StartAsync(
        DownloadTask task,
        CancellationToken cancellationToken = default);

    Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task StopAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid taskId, CancellationToken cancellationToken = default);
}
```

Torrent 实现：

```text
TorrentDownloadEngine
```

内部使用：

```text
IMonoTorrentAdapter
```

---

# 10. MonoTorrent Adapter

禁止：

```text
Form
↓
直接调用 ClientEngine
```

必须：

```text
UI
↓
Application
↓
ITorrentEngine
↓
MonoTorrentAdapter
↓
MonoTorrent
```

示例：

```csharp
public interface ITorrentEngine
{
    Task<TorrentTaskHandle> AddMagnetAsync(
        string magnetUri,
        string savePath,
        CancellationToken cancellationToken = default);

    Task<TorrentTaskHandle> AddTorrentAsync(
        string torrentPath,
        string savePath,
        CancellationToken cancellationToken = default);

    Task PauseAsync(Guid taskId);
    Task ResumeAsync(Guid taskId);
    Task StopAsync(Guid taskId);

    TorrentRuntimeSnapshot GetSnapshot(Guid taskId);
}
```

这样未来可替换 BitTorrent 引擎，而不影响 UI / Core。

---

# 11. Magnet 状态机

```text
Created
↓
Validating
↓
DiscoveringPeers
↓
FetchingMetadata
↓
MetadataReady
↓
WaitingForSelection
↓
Queued
↓
Downloading
↓
Paused
↓
Completed
```

失败：

```text
Failed
```

停止：

```text
Stopped
```

---

# 12. Metadata 状态

必须单独展示。

例如：

```text
Magnet detected
InfoHash: ...
DHT: Connected
Peers: 12

Fetching metadata...
```

Metadata Ready 后：

```text
Name: ...
Files: 23
Size: 18.4 GB
```

不要在 Metadata 未完成时显示：

```text
0%
```

正确状态应该是：

```text
Fetching Metadata
```

---

# 13. File Tree

Metadata Ready 后显示：

```text
Torrent Name
├── Folder A
│   ├── File 1
│   └── File 2
├── File 3
└── File 4
```

支持：

- [ ] 全选
- [ ] 全不选
- [ ] Folder 勾选
- [ ] 单文件勾选
- [ ] 显示文件大小
- [ ] 显示总选择大小
- [ ] 搜索文件
- [ ] 默认全部选择

---

# 14. File Priority

P1 支持：

```text
Do Not Download
Low
Normal
High
```

MVP 可以先只做：

```text
Download / Skip
```

---

# 15. Torrent Runtime Snapshot

```csharp
public sealed class TorrentRuntimeSnapshot
{
    public Guid TaskId { get; init; }
    public DownloadState State { get; init; }
    public double ProgressPercent { get; init; }
    public long DownloadedBytes { get; init; }
    public long UploadedBytes { get; init; }
    public double DownloadSpeed { get; init; }
    public double UploadSpeed { get; init; }
    public int ConnectedPeers { get; init; }
    public int Seeds { get; init; }
    public double? Availability { get; init; }
    public TimeSpan? Eta { get; init; }
    public bool DhtConnected { get; init; }
    public int WorkingTrackers { get; init; }
    public int TotalTrackers { get; init; }
}
```

---

# 16. UI 任务卡

```text
Ubuntu.iso

63.2%
████████████████░░░░░░

↓ 18.4 MB/s
↑ 2.1 MB/s

Peers        47
Seeds        18
Availability 2.8

12.3 GB / 19.4 GB
ETA 06:21
```

---

# 17. Availability

Availability 是 Magnet/Torrent 很有价值的指标。

如果：

```text
Availability = 2.8
```

可以解释为：

> 当前 Peer 集合中，完整数据大约存在 2.8 份等价可用性。

如果：

```text
Availability < 1
```

提示：

```text
当前已发现 Peer 中可能暂时不存在完整副本。
任务可能无法立即完成。
```

注意：

> 不要把 Availability < 1 直接定义为“永远无法完成”。Peer 集合会动态变化。

---

# 18. No Seed / No Peer UX

不要只显示：

```text
0 KB/s
```

应该区分：

```text
Finding Peers
No Peers
Waiting for Peers
Metadata Pending
Downloading
Stalled
```

例如：

```text
Waiting for peers

DHT: Connected
Tracker: 2/4
Peers: 0

No active peer currently provides this content.
```

---

# 19. DHT

UI 最低限度：

```text
DHT
Connected / Connecting / Disabled / Error
```

MVP 不需要展示：

```text
Node Count
Routing Table
```

可放 Debug 页面。

---

# 20. Tracker

每个 Torrent 可以有多个 Tracker。

MVP UI：

```text
Tracker 3 / 5
```

详情：

```text
tracker-a    OK
tracker-b    Timeout
tracker-c    OK
```

---

# 21. PEX

由 BitTorrent 引擎负责。

MVP 不需要 UI 单独暴露。

Debug 模式可以记录。

---

# 22. Piece

Piece 下载和 Hash Verification 交给引擎。

UI 可在 P1 增加：

```text
Pieces
2481 / 3926
```

MVP 不需要 Piece Map。

---

# 23. Resume / Persistence

这是 MVP 核心。

```text
Download 63%
↓
Close App
↓
Tomorrow
↓
Open App
↓
Continue 63%
```

必须成立。

持久化至少保存：

```text
Task
Magnet
InfoHash
SavePath
SelectedFiles
State
CreatedAt
Downloaded
Metadata
Engine Resume Data
```

---

# 24. SQLite

如果项目已有统一数据库，优先复用。

建议表：

```text
download_tasks
torrent_tasks
torrent_files
torrent_trackers
download_events
```

---

# 25. torrent_tasks

字段建议：

```text
task_id
magnet_uri
torrent_path
info_hash
metadata_state
uploaded_bytes
peer_count
seed_count
availability
ratio
created_at
updated_at
```

---

# 26. torrent_files

```text
id
task_id
path
length
selected
priority
progress
```

---

# 27. 下载队列

Torrent 与 HTTP 共用统一 Queue。

```text
Queue

1. HTTP File
2. Magnet A
3. HLS Video
4. Magnet B
```

Scheduler 决定：

```text
Max Active Tasks
```

Torrent 引擎内部 Peer 并发不等于 App Task 并发，二者必须分开。

---

# 28. 全局带宽限制

支持：

```text
Global Download Limit
Global Upload Limit
```

例如：

```text
Download Unlimited
Upload 2 MB/s
```

Per Task Limit 放到 P1。

---

# 29. 上传策略

BitTorrent 下载过程中会同时上传已有 Piece。

UI 必须明确显示：

```text
Upload Speed
Uploaded
```

设置：

```text
Upload Limit
```

第一版不要默认无限制占满上行。

---

# 30. 完成后的 Seeding

完成后不要默认无限 Seed 而不告诉用户。

设置：

```text
After Download

○ Stop
○ Seed for 30 min
○ Seed to ratio 1.0
○ Keep seeding
```

MVP 推荐默认：

```text
Stop
```

---

# 31. Ratio

P1 支持：

```text
Ratio = Uploaded / Downloaded
```

---

# 32. Settings

Torrent Settings：

```text
Enable DHT
Enable PEX
Upload Limit
Download Limit
Max Connections
Listen Port
Encryption Preference
Post-download Seed Policy
Metadata Timeout
```

MVP 不要暴露过多高级配置。

第一版建议只展示：

```text
Download Limit
Upload Limit
DHT
Post-download Behavior
```

---

# 33. Magnet Parser

必须校验：

```text
URI Scheme = magnet
xt
btih / btmh
```

可能存在：

```text
dn
tr
xl
```

支持可选 Tracker。

不要因为缺少 `dn` 就认为 Magnet 无效。

---

# 34. BitTorrent v1 / v2

架构上不要假设只有：

```text
BTIH / SHA-1
```

预留：

```text
BitTorrent v1
BitTorrent v2
Hybrid Torrent
```

InfoHash 内部建议使用抽象类型，不要把固定长度字符串规则散落代码。

---

# 35. Error Handling

必须区分：

```text
Invalid Magnet
Unsupported Magnet
Invalid Torrent File
Metadata Timeout
No Peers
Tracker Failure
DHT Failure
Disk Full
Permission Denied
File In Use
Hash Check Failure
Engine Error
Network Error
Path Too Long
Cancelled
```

单个任务错误不能让整个下载器退出。

---

# 36. Disk Space Check

Metadata Ready 后：

```text
Selected Size
↓
Check Disk Free Space
```

如果：

```text
Required > Available
```

明确提示。

---

# 37. Existing Files

目标目录已有同名文件时，不要直接覆盖。

需要：

```text
Verify / Resume
Rename
Cancel
```

Torrent 下载应优先通过 Piece Hash 校验已有数据，并复用有效 Piece。

---

# 38. Hash Check

UI 状态：

```text
Checking Existing Data
```

显示校验进度。

不要把 Hash Check 当成 Download。

---

# 39. Logging

建议记录：

```text
Task Created
Magnet Parsed
Metadata Started
Metadata Completed
Tracker Result
DHT State
Peers
Download Start
Pause
Resume
Hash Check
Complete
Error
```

默认不要把下载历史发送到任何远端遥测。

---

# 40. Privacy

第一版：

```text
No Account
No Telemetry
No Magnet Upload
No Download History Upload
```

所有任务默认：

```text
Local Only
```

注意：

> BitTorrent 本身是 P2P 协议，加入 Swarm 后其他 Peer 通常可以观察到参与者的网络地址。产品 UI/文档应如实说明这一协议特征。

---

# 41. Security

不要自动执行下载文件。

禁止：

```text
Download Complete
↓
Auto Run EXE
```

对：

```text
.exe
.msi
.bat
.cmd
.ps1
.scr
```

完成后只正常显示文件。

P1 可以增加：

```text
Open Folder
```

而不是 `Run`。

---

# 42. Product Boundary

软件是下载工具。

不要集成：

```text
Pirated Resource Search
Torrent Index Search
Copyright Bypass
DRM Bypass
```

Magnet / Torrent 只是传输协议支持。

用户输入自己的：

```text
Magnet Link
.torrent
```

---

# 43. Stats

全局统计：

```text
Torrent Tasks
Completed
Failed
Total Downloaded
Total Uploaded
Average Speed
Peak Speed
Metadata Success Rate
Resume Count
```

---

# 44. History

下载历史：

```text
Name
Protocol
Size
CompletedAt
Downloaded
Uploaded
SavePath
Status
```

支持：

```text
Open Folder
Remove History
Redownload
```

---

# 45. Notifications

P1：

```text
Download Complete
Download Failed
Metadata Failed
Disk Full
```

第一版可以先只做应用内通知。

---

# 46. Shutdown Behavior

关闭主窗口时：

```text
Exit App
↓
Torrent Engine Stop Gracefully
↓
Save Resume Data
↓
Flush Database
↓
Exit
```

不要直接 Kill Process，避免下次大量重新 Hash Check。

---

# 47. Crash Recovery

程序异常退出后：

```text
Restart
↓
Load Tasks
↓
Validate Resume Data
↓
Hash Check if necessary
↓
Restore Paused / Queued State
```

默认不要未经用户确认自动恢复大量网络传输。

推荐恢复为：

```text
Paused
```

或者尊重用户设置。

---

# 48. UI Tabs

建议：

```text
Downloading
Completed
Paused
Failed
All
```

Torrent 任务与其他协议统一显示。

---

# 49. Torrent Detail Page

```text
General
Files
Peers
Trackers
Log
```

MVP：

```text
General
Files
Trackers
```

Peers 页面放 P1。

---

# 50. General

展示：

```text
Name
InfoHash
Size
Progress
State
Download
Upload
Peers
Seeds
Availability
ETA
Save Path
Created
Started
```

---

# 51. Peers（P1）

显示：

```text
IP / Masked Address
Client
Download Rate
Upload Rate
Progress
Flags
```

默认可考虑隐藏或模糊完整 IP，避免 UI 不必要暴露。

---

# 52. Trackers

显示：

```text
URL
Status
Last Announce
Next Announce
Peers
Error
```

---

# 53. Magnet Add Dialog

```text
┌─────────────────────────────┐
│ Add Magnet                  │
│                             │
│ magnet:?xt=...              │
│                             │
│ Save to: D:\Downloads      │
│                             │
│ [Add] [Cancel]              │
└─────────────────────────────┘
```

Add 后：

```text
Fetching Metadata
```

Metadata Ready：

```text
Select Files
```

---

# 54. Integration with Existing Downloader

如果现有下载器已经有：

```text
Queue
SQLite
History
Settings
Statistics
```

必须复用。

不要做：

```text
HTTP 下载任务数据库
Torrent 下载任务数据库
两套 History
两套 Queue
```

Torrent 只增加协议特有字段。

---

# 55. Project Structure

```text
src/
├── App/
├── Core/
│   ├── Downloads/
│   ├── Queue/
│   └── Statistics/
│
├── Protocols/
│   ├── Http/
│   ├── Hls/
│   └── BitTorrent/
│       ├── Abstractions/
│       ├── MonoTorrent/
│       ├── Models/
│       └── Mapping/
│
├── Storage/
│   ├── Repositories/
│   └── Migrations/
│
└── Tests/
    └── BitTorrent/
```

---

# 56. Unit Tests

至少：

- [ ] Magnet parser
- [ ] Invalid Magnet
- [ ] Magnet with tracker
- [ ] Torrent file load
- [ ] Task persistence
- [ ] Selected files persistence
- [ ] State mapping
- [ ] Snapshot mapping
- [ ] App restart recovery
- [ ] Disk space validation
- [ ] Duplicate task handling

---

# 57. Integration Tests

尽量使用合法、公开、稳定的测试资源，例如：

```text
Linux distribution torrents
Open-source software torrents
```

测试：

```text
Magnet
↓
Metadata
↓
Download
↓
Pause
↓
Resume
↓
Complete
↓
Hash Valid
```

---

# 58. Duplicate Task

如果用户添加同一个 InfoHash：

```text
Already exists
```

提供：

```text
Open existing task
```

不要默认创建完全重复任务。

---

# 59. Protocol Detection

输入框可以统一：

```text
Paste URL / Magnet
```

识别：

```text
http://
https://
magnet:
```

`.torrent` 使用文件导入。

长期：

```text
InputRouter
↓
ProtocolResolver
↓
Engine
```

---

# 60. P0 TODO

## Core
- [ ] DownloadProtocol 增加 BitTorrent
- [ ] TorrentDownloadTask
- [ ] TorrentRuntimeSnapshot
- [ ] Torrent 状态枚举
- [ ] Metadata 状态枚举

## Engine
- [ ] 添加 MonoTorrent package
- [ ] ITorrentEngine
- [ ] MonoTorrentAdapter
- [ ] ClientEngine 生命周期
- [ ] Add Magnet
- [ ] Add Torrent
- [ ] Pause
- [ ] Resume
- [ ] Stop
- [ ] Remove

## Magnet
- [ ] Magnet parser
- [ ] InfoHash
- [ ] Metadata acquisition
- [ ] Metadata state
- [ ] Tracker extraction

## Files
- [ ] Torrent file tree
- [ ] File selection
- [ ] Selected size
- [ ] File persistence

## Runtime
- [ ] Progress
- [ ] Download Speed
- [ ] Upload Speed
- [ ] Peers
- [ ] Seeds
- [ ] Availability
- [ ] ETA
- [ ] DHT state
- [ ] Tracker state

## Storage
- [ ] DB migration
- [ ] Torrent task persistence
- [ ] File persistence
- [ ] Resume data
- [ ] Crash recovery

## UI
- [ ] Add Magnet
- [ ] Add Torrent
- [ ] Fetching Metadata
- [ ] File Selection Dialog
- [ ] Torrent Task Card
- [ ] Torrent Detail
- [ ] DHT
- [ ] Tracker summary
- [ ] Pause / Resume
- [ ] Error state

## Settings
- [ ] Download limit
- [ ] Upload limit
- [ ] DHT toggle
- [ ] Post-download behavior

## Test
- [ ] Parser tests
- [ ] State tests
- [ ] Persistence tests
- [ ] Recovery tests
- [ ] Public legal torrent integration test

---

# 61. P1 TODO

- [ ] File Priority
- [ ] Peer list
- [ ] Tracker details
- [ ] Piece progress
- [ ] Ratio
- [ ] Seed to ratio
- [ ] Seed by time
- [ ] Per-task limits
- [ ] Adaptive connection limits
- [ ] UPnP / NAT-PMP
- [ ] Port status
- [ ] Magnet History
- [ ] Notifications

---

# 62. P2 TODO

- [ ] BitTorrent v2 advanced UI
- [ ] Hybrid torrent diagnostics
- [ ] Sequential download option
- [ ] Streaming experiment
- [ ] Remote control API
- [ ] Browser extension integration
- [ ] CLI
- [ ] Agent Tool Interface

---

# 63. Codex 开发规则

Codex 必须遵守：

1. 不自己实现完整 BitTorrent 协议栈。
2. MonoTorrent 只存在于 Adapter / Infrastructure 层。
3. UI 不直接使用 MonoTorrent 类型。
4. Torrent 必须复用现有 Download Queue。
5. Torrent 必须复用现有 History。
6. Torrent 必须复用现有 SQLite 基础设施。
7. HTTP/HLS/Torrent 不得产生三套相互独立的任务系统。
8. 所有 Engine 操作支持 CancellationToken。
9. 所有任务状态必须可持久化。
10. 关闭程序前必须保存恢复状态。
11. 单个 Torrent 错误不能导致 Engine 全局崩溃。
12. Metadata 下载必须有独立状态。
13. 不将 0 KB/s 统一解释为失败。
14. UI 必须区分 Waiting / No Peer / Metadata / Stalled。
15. 不默认无限 Seeding。
16. 不自动执行下载文件。
17. 不增加资源搜索/索引功能。
18. 不增加 Telemetry。
19. 所有 Debug 日志支持关闭。
20. 优先做最小下载闭环，再做高级 Peer/Piece UI。

---

# 64. Codex 实现顺序

```text
STEP 1
TorrentDownloadTask + Protocol abstraction

↓

STEP 2
MonoTorrent Adapter

↓

STEP 3
Load .torrent

↓

STEP 4
Download one legal test torrent

↓

STEP 5
Magnet parsing

↓

STEP 6
Magnet metadata

↓

STEP 7
File selection

↓

STEP 8
Task UI

↓

STEP 9
Pause / Resume

↓

STEP 10
SQLite persistence

↓

STEP 11
App restart recovery

↓

STEP 12
Speed / Peer / Seed / ETA

↓

STEP 13
DHT / Tracker status

↓

STEP 14
Limits / Settings

↓

STEP 15
Availability / stalled UX
```

特别注意：

> 不要先做复杂 Torrent UI，再尝试下载。

第一阶段先完成：

```text
.torrent
↓
MonoTorrent
↓
Download
↓
Pause
↓
Resume
↓
Complete
```

然后再加入 Magnet。

---

# 65. MVP 验收标准

## Torrent

```text
Open .torrent
↓
Show Files
↓
Select
↓
Download
↓
Pause
↓
Resume
↓
Complete
```

## Magnet

```text
Paste Magnet
↓
Fetching Metadata
↓
Metadata Ready
↓
Show Files
↓
Download
↓
Complete
```

## Persistence

```text
Download 30%
↓
Close App
↓
Open App
↓
Task Restored
↓
Resume
↓
Complete
```

## No Peer

```text
Magnet
↓
No Peer
↓
UI 明确显示 Waiting for Peers
```

而不是：

```text
Unknown Error
```

---

# 66. 产品成功标准

成功不是：

> 软件可以识别 `magnet:`。

真正成功是：

> Magnet / Torrent 与 HTTP/HLS 一样成为下载器里稳定、统一、可恢复、可统计的一种 Download Protocol。

用户不需要理解：

```text
DHT
PEX
Piece Picker
Tracker internals
```

只需要知道：

```text
我添加了一个任务
它现在在做什么
有没有资源
速度是多少
什么时候完成
关闭程序以后还能不能继续
```

---

# 67. 长期产品定位

短期：

```text
Downloader
+
Magnet Support
```

中期：

```text
Multi-Protocol Download Manager
```

长期：

```text
Local Download / Transfer Engine

├── HTTP
├── HLS
├── DASH
├── BitTorrent
└── Future Protocols
```

未来可以暴露：

```text
CLI
Browser Extension
Agent API
```

让 GUI 只是多个入口之一。

---

# 最终定义

> **Magnet 模块不是“加一个磁力链接按钮”，而是为下载器增加完整的 P2P Download Engine，同时保持任务、队列、历史、统计和持久化的一致性。**
