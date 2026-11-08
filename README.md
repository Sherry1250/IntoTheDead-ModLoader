# IntoTheDead-ModLoader

**BepInEx installer & modding notes for *Into the Dead: Our Darkest Days* (IL2CPP metadata v107)**

> 让《Into the Dead: Our Darkest Days》能正常加载 BepInEx 插件的安装脚本 + 踩坑记录。
> **本仓库不包含任何游戏文件、反编译程序集或第三方闭源二进制。**

## 这个仓库解决什么问题

游戏使用 Unity 6000.5.11f1 打包，IL2CPP 元数据版本为 **v107**。
BepInEx 6 内置的 Cpp2IL 解析器最高只支持到 **v106**，直接装会在启动时报：

```
Unsupported metadata version found! We support 23-106, got 107
```

解析器一挂，interop 程序集无法生成，插件也就加载不了 —— 这就是大部分人「加了框架也打不上 mod」的真实原因。

MoonSweepers ModEngine 是绕过这个版本限制的补丁。本脚本按正确顺序装好整套环境，并靠首次启动让游戏自己生成 interop。

## 安装

```powershell
git clone https://github.com/Sherry1250/IntoTheDead-ModLoader.git
cd IntoTheDead-ModLoader
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

指定游戏路径：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -GamePath "D:\steam\steamapps\common\Into the Dead Our Darkest Days"
```

脚本做的事：

1. 从 Steam 库配置里自动定位游戏目录
2. 下载 BepInEx 6.0.0-be.791（Unity.IL2CPP 版）并解压
3. 写入唯一能启动的配置项 `UnityLogListening = false`
4. 提示你手动下载 MoonSweepers ModEngine（Nexus 需登录，无法脚本化）
5. 通过 Steam 启动游戏，首次运行生成 `interop\` 与 `dummy\`

## 装完的目录

```
游戏根目录\
├─ BepInEx\
│  ├─ core\             BepInEx 本体、0Harmony、Il2CppInterop
│  ├─ patchers\         MoonSweepers.ModEngine（手动下载）
│  ├─ interop\          首次启动生成，编译插件时引用
│  ├─ dummy\            首次启动生成，反查类结构时用
│  └─ plugins\          插件 DLL 放这里
├─ dotnet\              .NET 6 运行时
├─ winhttp.dll          doorstop 注入器
└─ doorstop_config.ini
```

## 写插件的坑（这个游戏特有）

1. **元数据 v107**：控制台会有三条红字（Il2CppClass 布局校准失败、MathematicsModule / PhysicsCore2DModule 预加载失败）。**正常现象**，只影响注入新类，不影响 Harmony 改已有方法。
2. **Harmony 参数名**：IL2CPP 参数名全是 `__0`，写 `state` 之类会报 `Parameter "state" not found`，必须用位置参数。
3. **接口实现的方法名**是拼接形式，例如 `DRS_Game_Damage_IDamageReceiver_Damage`，按 `Damage` 找不到。
4. **类名**不能靠 `GetType().Name`（只返回接口代理名），要走 `IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`。
5. **输入**：`UnityEngine.Input` 被禁用，热键用 `Keyboard.current` / `Mouse.current`。
6. **interop 只保留 `NativeFieldInfoPtr_*`**，原字段反射取不到值。
7. **字体**：游戏只有 `LegacyRuntime`，`CreateDynamicFontFromOSFont` 全部失败，UI 文案只能用英文。
8. **每帧驱动**：`Time.get_deltaTime` 是最稳的钩子点。
9. **编译失败会静默部署旧 DLL**：改完插件务必确认 `已挂补丁: xxx` 出现在日志里，否则你跑的是旧版本。

## 反查类结构

```powershell
Add-Type -Path "dnlib\lib\net6.0\dnlib.dll"
$m = [dnlib.DotNet.ModuleDefMD]::Load("...\BepInEx\dummy\DRS.Game.dll")
$t = @($m.GetTypes() | Where-Object { [string]$_.FullName -eq "DRS.Game.Character.CharacterRoot" })[0]
$t.Methods | ForEach-Object {
    Write-Output ([string]$_.Name + "(" + (($_.MethodSig.Params | ForEach-Object { [string]$_.TypeName }) -join ",") + ")")
}
```

dnlib 的 `Name` 是 `UTF8String`，拼接前必须 `[string]` 强转，否则报 `op_Addition` 不存在。
读结构用 `dummy\`，确认可见性和真实 IL 名用 `interop\`。

## 不分发

- 游戏本体及其资源、代码、反编译程序集
- MoonSweepers ModEngine（闭源，请到其发布页自行下载）
- .NET 运行时（请从微软官方渠道获取）

## 免责声明

本项目仅用于**单人游戏**的本地功能增强，请勿在联机或竞技场景使用。
使用 Mod 可能导致存档异常，请自行备份。
本项目与 PikPok、Steam 均无关联，所有商标与版权归其各自所有者。

## License

MIT
