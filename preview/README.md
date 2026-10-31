# 饥荒风格 UI 参考

独立工程：`DstUiPreview/DstUiPreview.csproj`。使用示例数据和本机只读图片，不读取正式版凭证、配置或存档，不启动服务器和通道。

```powershell
dotnet run --project preview/DstUiPreview -c Release
dotnet publish preview/DstUiPreview -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o outputs/SakuraCamp-DST-UI-Preview
```

版本固定为 0.0.0-preview，与正式版版本号分离。主题通过 WPF 矢量绘制，不内置游戏图片。
