# WPF + MahApps.Metro 项目基础框架搭建

## 当前进度

当前项目已经完成以下步骤：

```text
新 WPF (.NET 10)
      ↓
安装 MahApps.Metro 2.4.11
      ↓
配置 App.xaml
      ↓
Window → MetroWindow
      ↓
F5 确认正常
```

下一步建议先搭建 **MVVM 基础骨架**，暂时不要急着加入 DI、日志、数据库等内容。

目标是尽量避免把业务逻辑写进 `MainWindow.xaml.cs`，让项目从一开始就保持清晰的结构。

---

## 1. 安装 CommunityToolkit.Mvvm

通过 NuGet 安装：

```text
CommunityToolkit.Mvvm
```

它主要提供：

- `ObservableObject`
- `ObservableProperty`
- `RelayCommand`
- `AsyncRelayCommand`
- Messenger 等 MVVM 常用能力

后续可以减少大量 `INotifyPropertyChanged`、`ICommand` 等模板代码。

---

## 2. 整理项目目录

建议先整理为如下结构：

```text
你的项目
│
├─ Models
│
├─ ViewModels
│   └─ MainViewModel.cs
│
├─ Views
│
├─ Services
│
├─ Controls
│
├─ Converters
│
├─ Resources
│
├─ App.xaml
├─ App.xaml.cs
├─ MainWindow.xaml
└─ MainWindow.xaml.cs
```

各目录职责建议如下：

| 目录 | 用途 |
|---|---|
| `Models` | 数据模型、实体对象 |
| `ViewModels` | 页面状态、命令、业务交互逻辑 |
| `Views` | 页面、窗口、UserControl |
| `Services` | 数据库、HTTP、串口、设备通信等服务 |
| `Controls` | 自定义控件 |
| `Converters` | WPF ValueConverter |
| `Resources` | 样式、图标、主题等资源 |

项目初期没有必要拆成很多程序集。

等项目规模真正变大后，再考虑：

```text
MyApp
MyApp.Core
MyApp.Infrastructure
MyApp.Device
```

---

## 3. 创建 MainViewModel

创建文件：

```text
ViewModels/MainViewModel.cs
```

代码：

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace 你的项目.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = "我的 WPF 应用";

    [ObservableProperty]
    private string userName = "";

    [ObservableProperty]
    private string statusText = "系统就绪";

    [RelayCommand]
    private void Test()
    {
        StatusText = $"你好，{UserName}";
    }
}
```

---

## 4. ObservableProperty 的作用

例如：

```csharp
[ObservableProperty]
private string statusText = "";
```

`CommunityToolkit.Mvvm` 会通过 Source Generator 自动生成对应的公开属性，并实现属性变更通知。

可以把它理解成自动生成了类似：

```csharp
public string StatusText
{
    get => statusText;
    set
    {
        if (statusText != value)
        {
            statusText = value;
            OnPropertyChanged();
        }
    }
}
```

因此不需要手动实现大量 `INotifyPropertyChanged` 代码。

---

## 5. RelayCommand 的作用

例如：

```csharp
[RelayCommand]
private void Test()
{
    StatusText = $"你好，{UserName}";
}
```

会自动生成可以供 XAML 绑定的：

```text
TestCommand
```

XAML 中即可：

```xml
<Button Command="{Binding TestCommand}" />
```

不需要自己实现 `ICommand`。

---

## 6. 注意 partial

使用：

```csharp
[ObservableProperty]
```

和：

```csharp
[RelayCommand]
```

时，ViewModel 类需要写成：

```csharp
public partial class MainViewModel : ObservableObject
```

注意：

```text
partial
```

不能漏掉。

因为 CommunityToolkit.Mvvm 的 Source Generator 需要通过 `partial` 为类补充自动生成的代码。

---

## 7. 修改 MainWindow.xaml.cs

将 `MainViewModel` 设置为窗口的 `DataContext`：

```csharp
using MahApps.Metro.Controls;
using 你的项目.ViewModels;

namespace 你的项目;

public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();

        DataContext = new MainViewModel();
    }
}
```

这样：

```text
MainWindow.xaml
      ↓ Binding
MainViewModel
```

XAML 就可以直接绑定 ViewModel 中的属性和命令。

---

## 8. 修改 MainWindow.xaml 测试 MVVM

可以暂时使用下面的界面进行测试：

```xml
<Grid Margin="30">

    <StackPanel Width="400"
                HorizontalAlignment="Center"
                VerticalAlignment="Center">

        <TextBlock Text="{Binding Title}"
                   FontSize="30"
                   FontWeight="SemiBold"
                   HorizontalAlignment="Center"
                   Margin="0,0,0,30"/>

        <TextBox Text="{Binding UserName, UpdateSourceTrigger=PropertyChanged}"
                 mah:TextBoxHelper.Watermark="请输入用户名"
                 Margin="0,0,0,15"/>

        <Button Content="测试 MVVM"
                Command="{Binding TestCommand}"
                Height="40"
                Margin="0,0,0,20"/>

        <TextBlock Text="{Binding StatusText}"
                   HorizontalAlignment="Center"
                   FontSize="16"/>

    </StackPanel>

</Grid>
```

---

## 9. 测试

运行项目：

```text
F5
```

在文本框输入：

```text
张三
```

点击：

```text
测试 MVVM
```

界面下方应该显示：

```text
你好，张三
```

如果能够正常显示，说明以下内容已经全部工作正常：

```text
View
 ↓
Binding
 ↓
ViewModel
 ↓
ObservableProperty
 ↓
RelayCommand
```

---

## 10. 当前项目结构变化

之前如果把逻辑全部写进窗口后台代码，通常会变成：

```text
MainWindow.xaml
      ↓
MainWindow.xaml.cs
      ↓
UI逻辑
业务逻辑
设备通信
数据库
网络请求
配置读取
……
```

项目一大后，`MainWindow.xaml.cs` 很容易变得非常混乱。

搭建 MVVM 后：

```text
MainWindow.xaml
      ↓
   Binding
      ↓
MainViewModel
      ↓
   Services
      ↓
设备 / 数据库 / HTTP / 文件 / 配置
```

职责会更加清晰。

---

# 后续推荐架构

完成基础 MVVM 后，建议继续搭建真正的桌面软件主框架：

```text
┌─────────────────────────────────────────────┐
│ Logo / 软件名称                  最小化 □ X │
├────────────┬────────────────────────────────┤
│            │                                │
│  首页      │                                │
│            │        当前页面                │
│  设备管理  │                                │
│            │                                │
│  数据      │                                │
│            │                                │
│  设置      │                                │
│            │                                │
├────────────┴────────────────────────────────┤
│ ● 系统正常           当前用户 / 软件版本     │
└─────────────────────────────────────────────┘
```

推荐逐步增加：

```text
MahApps.Metro
      +
CommunityToolkit.Mvvm
      +
页面导航
      +
Dependency Injection
      +
Serilog
      +
配置文件
      +
全局异常捕获
      +
业务 Services
```

---

# 推荐开发顺序

建议按照以下顺序推进：

```text
1. WPF (.NET 10)
        ↓
2. MahApps.Metro
        ↓
3. CommunityToolkit.Mvvm
        ↓
4. View / ViewModel 分离
        ↓
5. 左侧菜单 + 页面导航
        ↓
6. Services 层
        ↓
7. Dependency Injection
        ↓
8. 配置系统
        ↓
9. Serilog 日志
        ↓
10. 全局异常处理
        ↓
11. 数据库 / HTTP / 串口 / PLC / 设备通信
        ↓
12. Self-contained 客户发布
```

---

# 当前阶段目标

现在先确保做到：

- [x] WPF `.NET 10`
- [x] MahApps.Metro
- [x] `MetroWindow`
- [x] MahApps 主题正常
- [ ] 安装 `CommunityToolkit.Mvvm`
- [ ] 创建 `MainViewModel`
- [ ] 配置 `DataContext`
- [ ] 属性 Binding 正常
- [ ] Command Binding 正常

以上全部完成后，再开始做：

> **左侧菜单 + 多页面导航 + 首页 / 设备 / 数据 / 设置**

这会作为整个客户端软件后续功能开发的基础壳层。
