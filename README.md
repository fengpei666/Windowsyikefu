# 易客服 · Windows 客户端

面向客服人员的 Windows 桌面接待工作台，对接自建客服系统的 HTTP 接口。

把一个浏览器后台需要干的活搬到桌面上：**多站点并行接待、系统通知上直接回复、订单与红包处理、托盘常驻后台收消息**。

---

## 功能特性

### 多站点并行接待

- 一个客户端可绑定多个网站，每个站点一份 `接口地址 + AppID + AppSecret`，顶部标签页切换，各自独立轮询、互不干扰
- 后台站点同样收消息、弹通知，不会漏掉任何一个站点的访客
- 支持单独刷新某个站点、移除站点、随时新增绑定

### 会话与消息

- 会话列表：未读角标、访客在线小绿点、会话状态标签、最近一条消息预览
- 文本 / 表情 / 图片消息收发，图片走上传接口
- 消息右键菜单：引用、转发到其它会话、复制文本、复制订单号、复制图片、图片另存为、用系统看图程序打开
- 会话结束 / 重新打开、手动刷新列表、窗口置顶

### 订单与红包

- 发送订单卡片给访客
- 按关键词搜索订单、查看订单详情
- 一键**退款**、**补单**
- 发红包、领取红包、查看余额与红包记录

### Windows 深度集成

- **原生 Toast 通知**：弹窗上可直接输入文字回复访客，无需切回主界面
- 点击通知自动定位到所属**站点**与**会话**（多站点下不会投错）
- 系统托盘常驻：关闭窗口继续收消息，托盘菜单支持打开主界面、暂停接收、退出登录
- 新消息提示音（可关闭）
- 开机自动启动（静默驻留托盘，不弹界面）
- 单实例运行：重复启动只唤醒已有窗口；带 Deep Link 参数启动时自动转交给已运行实例处理

### Deep Link 一键绑定

网站后台的绑定页点击「打开桌面端」，即可唤起客户端并**自动填入凭证完成绑定**，客服不用手抄 AppSecret。

```
easykefu://bind?api_url=https://example.com/api&app_id=xxx&app_secret=yyy
```

### 抗 CDN / WAF 干扰

客户端出口 IP 是浮动的，没法在 CDN 上加 IP 白名单，程序内置了三道对策：

- **浏览器化请求**：`User-Agent` / `Accept` / `Referer` 按正常浏览器请求构造
- **自定义请求头（暗号头）**：配置一个别处猜不到的请求头，让 CDN 按「带这个头」直接放行
- **低频轮询模式**：一键拉长各处轮询间隔，降低被安全狗 / 云锁这类防护拉黑的概率

### 其它

- 在线 / 离线状态切换（访客侧可见）
- 快捷回复短语，点击即插入输入框
- 在线检查新版本

---

## 运行环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（17763）及以上 / Windows 11 |
| 架构 | x64 |
| 开发环境 | .NET 8 SDK（版本由 `global.json` 锁定）、Visual Studio 2022 或 JetBrains Rider |
| 最终用户 | 无需安装 .NET 运行时（发布包为自包含单文件） |

---

## 快速开始

```powershell
git clone https://github.com/fengpei666/Windowsyikefu.git
cd Windowsyikefu

# 编译并运行（Debug）
dotnet run --project src\Windowsyikefu.App
```

也可以直接用 Visual Studio 打开 `Windowsyikefu.Desktop.sln` 后按 F5。

### 打包发布

一键脚本会自动完成「发布自包含单文件 + 生成安装包」，并把 `csproj` 里的版本号同步到安装包：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-setup.ps1
```

产物：

| 文件 | 说明 |
| --- | --- |
| `dist\Windowsyikefu.exe` | 发布出来的程序（自包含单文件，对方不用装 .NET） |
| `installer\kefu_setup.exe` | 发给别人安装用的安装包 |

> 打包需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)：`winget install --id JRSoftware.InnoSetup -e`

发新版时只要改 `src\Windowsyikefu.App\Windowsyikefu.App.csproj` 里的 `<Version>`，脚本会自动同步。

---

## 使用说明

### 绑定客服账号

**方式一：手动填写**

启动后在绑定窗口填入客服系统后台提供的三项凭证：

- 接口地址（如 `https://example.com/api`）
- APP ID
- APP SECRET

**方式二：Deep Link 一键绑定**

网站后台的绑定页拼接如下链接并跳转，客户端会被唤起并自动绑定：

```
easykefu://bind?api_url=<接口地址>&app_id=<APP ID>&app_secret=<APP SECRET>
```

支持的参数：

| 参数 | 别名 | 说明 |
| --- | --- | --- |
| `api_url` | `apiurl`、`url` | 接口地址 |
| `app_id` | `appid` | APP ID |
| `app_secret` | `appsecret`、`secret` | APP SECRET |
| `scheme` | — | 自定义协议名，默认 `easykefu`（安装包注册的是 `easykefu`） |
| `download` | — | 未安装时的下载地址（仅记录） |
| `auto` | — | 是否自动唤起，`0` 表示否 |

也兼容直接粘贴纯 query 串（`api_url=...&app_id=...`）。

### 托盘与通知

| 操作 | 行为 |
| --- | --- |
| 关闭窗口 | 默认最小化到托盘，继续收消息（可在设置中改为直接退出） |
| 双击托盘图标 | 打开主界面 |
| 托盘菜单 | 打开主界面 / 暂停接收消息 / 退出登录 / 退出 |

暂停接收后不再轮询、不再弹通知，但仍保持登录状态。

### 配置文件

配置保存在 `%AppData%\Windowsyikefu\config.json`，主要字段：

| 字段 | 说明 |
| --- | --- |
| `sites` | 已绑定的站点列表（接口地址、AppID、AppSecret、显示名） |
| `scheme` | Deep Link 协议名 |
| `poll_seconds` | 未读轮询间隔（秒） |
| `low_frequency` | 低频轮询模式 |
| `close_to_tray` | 关闭窗口时最小化到托盘 |
| `toast_enabled` | 弹出 Windows 系统通知 |
| `sound_enabled` | 新消息提示音 |
| `custom_headers` | 自定义请求头（每行一条 `名字: 值`，`#` 开头为注释） |
| `quick_replies` | 快捷回复短语 |

> 卸载程序**不会**删除该文件，重新安装后原来的绑定仍然可用。

---

## 项目结构

```
Windowsyikefu.Desktop.sln
├── src/Windowsyikefu.App/
│   ├── Assets/            应用与托盘图标
│   ├── Models/            接口数据模型
│   ├── Services/
│   │   ├── KefuApiClient    接口客户端（HMAC-SHA256 签名、自定义请求头）
│   │   ├── KefuService      轮询调度、心跳、通知派发
│   │   ├── AppConfig        本地配置读写与老配置迁移
│   │   ├── DeepLinkParser   easykefu:// 链接解析
│   │   ├── ProtocolRegistrar 注册表协议注册
│   │   ├── SingleInstanceIpc 单实例互斥与参数转交
│   │   ├── ToastService     原生 Toast 通知
│   │   ├── TrayService      系统托盘
│   │   ├── UpdateService    在线检查更新
│   │   ├── AutoStartManager 开机自启
│   │   └── WindowActivation / WindowEffects 窗口激活与视觉特效
│   ├── Styles/            WPF 主题与控件样式
│   ├── ViewModels/        列表项模型、图片工具
│   ├── Views/             主窗口、绑定、设置、转发、订单搜索、发红包、信息弹窗
│   └── App.xaml(.cs)      启动流程、单实例、Deep Link、通知激活处理
├── installer/             Inno Setup 安装包脚本
├── build-setup.ps1        一键发布 + 打包
└── global.json            .NET SDK 版本锁定
```

---

## 技术要点

### 接口签名

请求参数除 `sign` 外按字段名升序排列，拼成 `k1=v1&k2=v2`（键值均做 URL 编码），以 `AppSecret` 为密钥计算 **HMAC-SHA256**，输出小写十六进制字符串作为 `sign`。

### 轮询模型

- **未读汇总轮询**：默认 3 秒一次，拿到未读总数与未读会话，驱动托盘角标与通知
- **会话增量轮询**：打开会话后按 `last_id` 拉增量消息
- **心跳**：维持在线状态，后台绑定页据此判断客户端是否在线
- **多站点错峰**：多个站点在启动时随机延迟 0～1.5 秒，避免同一瞬间并发请求

### 健壮性处理

- 服务端常把数值字段返回成字符串（如 `"id": "246"`），JSON 解析已放宽兼容
- 兼容旧版单站点配置，升级后自动迁移为站点列表
- 配置损坏时回退默认值，不影响启动

---

## 常见问题

**Q：客户端被 CDN / WAF 拦截怎么办？**

客户端 IP 不固定，无法使用 IP 白名单。进入「设置 → 防 CDN 拦截」，填一条自定义请求头（例如 `X-Kefu-Key: 你的暗号`），然后在 CDN 控制台配置规则：

> 请求头 `X-Kefu-Key` 等于「你的暗号」→ 跳过防护

同时可以打开「低频轮询」，降低请求频率。

**Q：关闭窗口后程序还在运行？**

默认行为是最小化到托盘继续接收消息，从托盘菜单退出才会真正结束。如果想关闭即退出，在「设置 → 启动与关闭」中关掉「关闭窗口时最小化到托盘」。

**Q：重复启动会开出多个窗口吗？**

不会。程序以单实例方式运行，第二次启动只会把已有窗口提到最前；如果第二次启动带着 Deep Link 参数，参数会被转交给已运行的实例处理。

**Q：换电脑后要重新绑定吗？**

配置存在 `%AppData%\Windowsyikefu\config.json`，换电脑需要重新绑定，或把该文件一并拷贝过去。

---

## 技术栈

| 组件 | 用途 |
| --- | --- |
| .NET 8 + WPF | 界面框架 |
| Windows Forms | 系统托盘（`NotifyIcon`） |
| [MahApps.Metro.IconPacks](https://github.com/MahApps/MahApps.Metro.IconPacks) | Lucide 矢量图标 |
| [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | 原生 Toast 通知（含通知内快捷回复） |
| Inno Setup 6 | 安装包制作 |

---

## 许可证

本项目暂未指定开源许可证。如需开源分发，请先补充 `LICENSE` 文件。
