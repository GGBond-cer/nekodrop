<p align="center">
  <img src="app/assets/nekodrop.png" width="128" alt="nekodrop">
</p>

<h1 align="center">nekodrop</h1>

<p align="center">
  <b>用笔记本自带无线网卡在 Windows 上原生收发 AirDrop，Wi-Fi 照常上网；<br>同时打通安卓互传联盟、LocalSend 与跨生态规则中继。</b><br>
  不需要 USB 网卡，不需要关闭安全启动，不需要 Linux 虚拟机，也不需要 Mac。
</p>

<p align="center">
  简体中文 · <a href="README.en.md">English</a> · <a href="../../releases/latest">下载</a> · <a href="docs/ARCHITECTURE.zh-CN.md">全景架构</a> · <a href="docs/COMPATIBILITY.zh-CN.md">网卡兼容性</a> · <a href="CHANGELOG.md">更新日志</a>
</p>

<p align="center">
  <a href="#交流群"><img alt="QQ群 1137780084" src="https://img.shields.io/badge/QQ%E7%BE%A4-1137780084-12b7f5?logo=qq&logoColor=white"></a>
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078d4">
  <img alt="speed" src="docs/badges/speed.zh-CN.svg">
  <img alt="protocols" src="docs/badges/protocols.zh-CN.svg">
  <img alt="card" src="docs/badges/card.svg">
  <img alt="license" src="https://img.shields.io/badge/license-GPL--2.0-blue">
</p>

<p align="center">
  <img src="docs/demo.gif" width="760" alt="实机拍摄：iPhone 与 Windows 11 用自带 Intel AX201 网卡双向收发原生 AirDrop，同时保持 Wi-Fi 在线">
  <br>
  <sub>实机拍摄：未装任何 App 的 iPhone ↔ Windows 11，自带 Intel AX201，全程 Wi-Fi 在线。</sub>
</p>

## 一个软件，打通三大生态

| 生态 / 协议 | 对端设备 | 是否免装 App | 说明 |
|---|---|---|---|
| **Apple AirDrop**（原生 AWDL） | iPhone · iPad · Mac | ✅ 原生免装 | 直接出现在苹果隔空投送列表，实测双向 **53 MB/s**；支持「仅限联系人」（Apple ID / 导入 Mac 证书）与 iOS / iPadOS 26.x |
| **安卓互传联盟**（MTA v2，实验性） | 一加 · OPPO · 小米 · vivo · 荣耀 · 真我等 | ✅ 原生免装 | 同局域网 Wi-Fi 下 BLE 雷达自动发现、双向高速收发，内置隐私脱敏 BLE 诊断扫描器（默认关闭，可在设置开启） |
| **LocalSend**（v2，实验性） | Android · iOS · macOS · Windows · Linux | 需对端安装 LocalSend | 同局域网全平台兜底，支持双向 TLS 客户端证书，与 AirDrop 共用同一套雷达与发送界面（默认关闭） |
| **跨生态规则中继**（可视化流向图） | AirDrop ⇄ 安卓互传 ⇄ LocalSend 任意组合 | — | 将 Windows 电脑作为本地零配置桥接中转站：按设备配置单向/双向转发规则，仅规则内来源自动接收并无缝转发 |

## 核心亮点

- **双向 53 MB/s 极速直连**：Intel AX201 实测 846 MB 文件约 16 秒传完、0 丢包，连发稳定不掉线，且不干扰亮屏 iPhone 的正常 Wi-Fi（[实测数据](#速度)）。
- **单卡同时上网 + AirDrop**：Intel 与 MediaTek（MT7902 / MT7922）支持**并发模式**，一张网卡同时连 Wi-Fi 上网与收发 AirDrop；默认沿用原网卡 MAC 地址（路由器 DHCP 固定 IP、MAC 过滤照常生效），遇到 WPA/TKIP 混合加密等旧路由器时自动说明原因并优雅退回 Windows Wi-Fi。
- **纯用户态、零内核驱动门槛**：基于 UMDF 寄存器代理驱动与 4 套独立用户态无线电后端（Intel / MediaTek / Realtek），**无需关闭 Secure Boot、无需开启 Windows 测试模式**，出问题一键回退到 Windows 原厂 Wi-Fi。
- **像原生系统一样顺手**：AirDrop 风格雷达视图、资源管理器一级右键 **用 nekodrop 发送**、Windows 共享面板、系统通知预览与一键复制剪贴板、不可发送文件类型/文件夹清晰引导卡片（[支持的文件类型](docs/FILE-TYPES.zh-CN.md)）、一键清空待发送列表。
- **实验性高阶性能开关（默认关闭）**：Intel 并发模式可选开启 **Wi-Fi 6 (HE) 上网** 与 **数据面加速**（64 槽位 STA RX Block Ack 重排 + 双线程异步 TAP 写入器，多线程下行实测 156–200 Mbps）。
- **装上就不用管**：单安装包、内置自动更新（稳定版 / 预览版通道可选）、服务异常自动恢复，高风险实验特性全部默认关闭。

> 想了解客户端、三大协议引擎、跨生态中继、虚拟网卡与 4 套用户态无线电驱动的协作方式，见 **[全景架构图](docs/ARCHITECTURE.zh-CN.md)** 与 [工作原理](docs/HOW-IT-WORKS.zh-CN.md)。

## 速度

Intel AX201、Windows 11、`v2.2.9+`，开启高吞吐模式（HE80 + MTU 1500）实测：

| 方向 | 速度 | 测试 |
|---|---|---|
| 电脑 → iPhone | **53.00 MB/s**（50.55 MiB/s） | 846 MB 用 15.97 秒，Block Ack 确认率 99.85%，0 丢包 |
| iPhone → 电脑 | 净传输 **53.28 MB/s**（50.81 MiB/s）· 端到端 49.66 MB/s | 846 MB 净收 15.84 秒 / 总计 17.00 秒，0 丢包 |

其他已验证参考：Realtek RTL8852CE 按需模式约 **24.5 MB/s**；MediaTek MT7902 双向 A-MPDU 接收约 **15 MB/s**；Intel 并发模式开启数据面加速后多线程 Wi-Fi 下行实测 **156–200 Mbps**。测试方法见 [docs/TESTING.md](docs/TESTING.md)。

## 支持的无线网卡

无需购买外置 USB 网卡，直接使用笔记本/主板自带 PCIe / CNVi 无线网卡：

| 厂商 | 后端 | 型号 | 联网模式 | 状态 |
|---|---|---|---|---|
| **Intel** | `iwxradio` | AX201 · AX200 · AX210 · AX211 · AX203 (Arrow Lake) · AX411 / Killer AX1690 · 9560 · 9462 | 同时连 Wi-Fi / 按需 | ✅ **实测可用**（AX201 作者实测，AX200 / AX210 / AX211 / AX203 等为社区实测） |
| **Intel** | `iwxradio` | BE200（Wi-Fi 7，MLD） | 同时连 Wi-Fi / 按需 | 🧪 实验性 |
| **MediaTek / AMD** | `mtradio` | MT7902 · MT7925 | 5 GHz 同时连 Wi-Fi / 按需 | ✅ 双向实测可用 |
| **MediaTek / AMD** | `mtkradio` | MT7922 / AMD RZ616 | 5 GHz 同时连 Wi-Fi（实验性）/ 按需 | ✅ 双向实测可用 |
| **MediaTek / AMD** | `mtkradio` | MT7921 / AMD RZ608 | 按需接管 | 🧪 实验性 |
| **Realtek** | `devourer` | RTL8852CE | 按需接管 | ✅ 社区实测可用（约 24.5 MB/s） |

> 未列出的无线网卡暂不支持 AWDL（如高通 Qualcomm、博通 Broadcom、Intel BE201/BE211、老款 Intel 9260 等）。完整 PCI ID、固件版本与每张卡的详细说明见 **[网卡兼容性](docs/COMPATIBILITY.zh-CN.md)** · **[网卡状态与适配新网卡](docs/CARDS.zh-CN.md)**。

## 安装与回退

1. 确认环境：**Windows 10 2004+ / Windows 11 x64**、受支持的无线网卡、打开蓝牙。
2. 从 **[Releases](../../releases/latest)** 下载 `nekodrop-<版本>-setup.exe` 并运行（安装包目前未购置商业代码签名，若 Windows SmartScreen 弹出提示，点击「更多信息 → 仍要运行」即可；所有二进制均由本开源仓库构建，详见[隐私说明](docs/PRIVACY.md)）。
3. 点击系统托盘里的紫色小猫，打开 **允许接收**；iPhone 隔空投送设为 **所有人（10 分钟）**（或登录 Apple ID 开启「仅限联系人」）。
4. 如需使用 **安卓互传联盟**、**LocalSend** 或 **跨生态中继**，在 **设置 → 互传与中继** 中按需开启即可。

**出问题了一键恢复**：开始菜单 → **nekodrop - restore Windows Wi-Fi**，网卡立即交还 Windows 原厂驱动。更多排查见 [常见问题](docs/SUPPORT.zh-CN.md)。

## 界面截图

<table>
  <tr>
    <td align="center"><img src="docs/screenshots/main-radar.png" width="260" alt="主界面：雷达"><br><sub>雷达上的附近设备</sub></td>
    <td align="center"><img src="docs/screenshots/send.png" width="230" alt="发送窗口"><br><sub>右键 → 用 nekodrop 发送</sub></td>
    <td align="center"><img src="docs/screenshots/wifi.png" width="240" alt="Wi-Fi 窗口"><br><sub>nekodrop 自己的 Wi-Fi</sub></td>
  </tr>
</table>

更多截图和全部功能说明：[使用指南](docs/USAGE.zh-CN.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [使用指南](docs/USAGE.zh-CN.md) | 全部功能、安装细节、联网方式、Wi-Fi 加密、仅限联系人、安卓互传 / LocalSend / 中继、更新与卸载 |
| [常见问题](docs/SUPPORT.zh-CN.md) | 常见故障排查、一键回退、生成脱敏诊断报告 |
| [网卡兼容性](docs/COMPATIBILITY.zh-CN.md) · [网卡状态](docs/CARDS.zh-CN.md) | 全部 PCI ID、固件、单卡说明与适配新网卡指南 |
| [可发送文件类型](docs/FILE-TYPES.zh-CN.md) | 文件/文件夹发送规则与限制说明 |
| [全景架构](docs/ARCHITECTURE.zh-CN.md) · [工作原理](docs/HOW-IT-WORKS.zh-CN.md) | 四层全景架构图、底层原理、开发进度与从源码构建 |
| [致谢与代码来源](docs/CREDITS.zh-CN.md) | 贡献者名单、上游开源代码来源与许可证 |
| [隐私说明](docs/PRIVACY.md) | nekodrop 会连接哪些地址、发送什么数据 |
| [更新日志](CHANGELOG.md) | 稳定版与预览版完整变更记录 |

## 交流群

欢迎加入 nekodrop 用户 QQ 群交流和反馈真机测试结果：**1137780084**

<p>
  <img src="docs/community/qq-group.jpg" width="240" alt="nekodrop 用户 QQ 群二维码，群号 1137780084">
</p>

社区友链：[LINUX DO](https://linux.do/)。

## 贡献者

感谢 [@BaiBaiAi](https://github.com/BaiBaiAi)、[@XXD051030](https://github.com/XXD051030)、[@GGBond-cer](https://github.com/GGBond-cer)、[@DaiJiequn](https://github.com/DaiJiequn)、[@sh17632](https://github.com/sh17632)、[@YiHoooong](https://github.com/YiHoooong)、[@Yanlyn](https://github.com/Yanlyn)、[@lincoo2019](https://github.com/lincoo2019)、[@CarzyBilly](https://github.com/CarzyBilly)、[@Aobing-code](https://github.com/Aobing-code)，以及所有提交真机日志和测试反馈的朋友。详细贡献见 [致谢](docs/CREDITS.zh-CN.md#贡献者)。

欢迎在 [Issues](../../issues) 反馈问题，提交时请附上[诊断报告](docs/SUPPORT.zh-CN.md#反馈问题诊断报告)。

## 许可

**GPL-2.0**（[LICENSE](LICENSE)）；打包的第三方组件保留各自的许可证（[详情](docs/CREDITS.zh-CN.md#许可)）。

nekodrop 是独立开源项目，与 Apple、Intel、MediaTek、Realtek 及各手机厂商无关，也未获得其认可。AirDrop、iPhone、iPad、Mac 是 Apple Inc. 的商标；Intel 是 Intel Corporation 的商标。nekodrop 运行时会通过用户态代理临时接管你的无线网卡，属于实验性软件，按原样提供，不提供任何担保。
