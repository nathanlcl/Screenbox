# Screenbox.Mpv

libmpv 的精简 P/Invoke 绑定层（SPEC §D2）。零 NuGet 依赖；`LibraryImport` 源生成器 +
`[UnmanagedCallersOnly]` 回调 + 手工 UTF-8 封送，满足 NativeAOT 与
`DisableRuntimeMarshalling` 约束。

## 原生库放置约定（SPEC §D7 MSIX 布局）

- 本项目**不含**原生二进制。`libmpv-2.dll` 由 `scripts/fetch-libmpv.ps1 -Platform <x86|x64|arm64>`
  下载到 `Screenbox/Native/<Platform>/libmpv-2.dll`（该目录已入 `.gitignore`，二进制不入库）。
- `Screenbox/Screenbox.csproj` 以 `<Content Include="Native\$(Platform)\libmpv-2.dll">` +
  `<Link>libmpv-2.dll</Link>` 将其打进 MSIX **包根**（与 exe 同级），进程默认 DLL 搜索路径
  即可命中，无需 `SetDllDirectory`。
- 版本由仓库根 `libmpv.version` 钉死（tag + 三架构文件名 + SHA256），CI 只从本仓库
  镜像 Release 下载，shinchiro/mpv-winbuild-cmake 为镜像源与回退源。
- 本地首次构建前必须先跑 fetch 脚本；否则 `MpvHandle` 静态构造会抛出带指引的
  `DllNotFoundException`。

## 目录

- `Interop/` — client.h / render.h / stream_cb.h 的 internal P/Invoke 声明与结构体
  （全部显式 `CallingConvention.Cdecl`、blittable、手工 UTF-8 封送）。
- `MpvHandle.cs` — 公开门面：选项/命令/属性/observe/事件泵/日志/stream 协议注册。
- `Rendering/MpvRenderContext.cs` — render 上下文封装（OpenGL / SW），线程亲和断言。
- `Streams/` — `IMpvStream` / `IMpvStreamFactory`（screenbox:// 自定义协议抽象）。

## 关键约定

- 所有事件（`PropertyChanged`/`FileLoaded`/`EndFile`/`StartFile`/`LogMessage`）与
  `MpvRenderContext.FrameReady` 均在后台线程触发；订阅方自行切 UI 线程。
- wakeup / update / stream 回调内**禁止调用任何 mpv API**（libmpv 文档硬约束），
  只允许置标志/发信号。
- `MpvRenderContext` 的方法只能在创建它的渲染线程调用。
