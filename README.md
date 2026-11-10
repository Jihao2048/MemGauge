# 内存挂件 (MemGauge)

一个贴在屏幕边缘的 Windows 小挂件：点一下释放物理内存，结果用系统通知告诉你。

## 功能

- **环形占用**：默认是一个 72px 的环，中间显示当前内存占用百分比，弧线用 `#FFD1DC → #A6E0CF → #87CEEB` 渐变。
- **闲置收缩**：停手 1.6 秒后收成一条固定长度的细条（贴左右边为竖条、贴顶底边为横条）。细条是固定轨道的"液位计"，只按占用率填充，渐变比例不随填充变化。
- **点击释放**：点环或点细条都会释放内存，弹出 Windows 原生通知（已释放多少 / 当前可用与已用）。
- **贴边吸附**：拖到屏幕边缘自动吸住；按钮式拖动，位置记在 `%APPDATA%\MemGauge\settings.json`。
- 跟随系统深浅色、开机自启、始终置顶。

## 图片

<img width="110" height="110" alt="image" src="https://github.com/user-attachments/assets/0a378f62-b3bf-4179-8549-cf12a62a56a0" />

<img width="37" height="130" alt="image" src="https://github.com/user-attachments/assets/cc425648-9f92-4c4b-aa58-78eccab238a9" />

<img width="470" height="157" alt="image" src="https://github.com/user-attachments/assets/f9d73ec2-eaec-423f-ba91-67b2c56dd01a" />

<img width="406" height="311" alt="image" src="https://github.com/user-attachments/assets/8725d441-eb30-496a-87c8-9d7fdf950a06" />


## 释放原理

1. 遍历进程调用 `SetProcessWorkingSetSizeEx(h, -1, -1)`，把工作集页推回待机列表；
2. 调用 `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` 清空待机列表；
3. 采样 1.1 秒取可用内存峰值作为"已释放"值。

第 2 步需要 `SeProfileSingleProcessPrivilege`，也就是**管理员权限**。普通权限下第 1 步仍能回收同用户进程的工作集（实测约 1GB），但待机列表清不掉。右键菜单里有「以管理员身份重启」。

## 安装

到 [Releases](../../releases) 下载 `MemGauge-<版本>-setup.exe` 双击安装，装到当前用户目录（`%LOCALAPPDATA%\Programs\MemGauge`），不需要管理员权限。

安装时会创建开始菜单快捷方式，可选开机自启和桌面图标。

## 运行要求

- Windows 10 1809 或更高
- [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0)（框架依赖发布，单文件约 25 MB）

## 构建

```bash
dotnet build MemGauge.sln -c Release
dotnet publish MemGauge/MemGauge.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o publish
```

也可以直接用 Visual Studio 打开 `MemGauge.sln`。

打安装包需要 Inno Setup 6/7，编译脚本在 `installer/MemGauge.iss`：

```bash
"C:\Program Files\Inno Setup 7\ISCC.exe" installer\MemGauge.iss
```

产物在 `installer\Output\`。

## 说明

- 首次发送通知时会往开始菜单写一个带 AUMID 的快捷方式（`内存挂件.lnk`），这是非打包应用发 Toast 的必要条件，删掉通知就没了。
- 单文件版体积偏大，是因为把 WinRT 投影打包进去了 —— 发系统通知要用 `Windows.UI.Notifications`。

## 许可

GPL-3.0，见 [LICENSE](LICENSE)。
