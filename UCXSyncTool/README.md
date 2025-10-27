# UCX SyncTool

Простой WPF-приложение для запуска фоновой синхронизации по логике, аналогичной `DUSync-4.ps1`.

Особенности:
- Запускает по одному процессу robocopy для каждой пары Node/Share
- Отслеживает время последнего изменения и останавливает robocopy при простое
- Пишет логи в папку `<DestRoot>\\Logs`

Требования:
- .NET 8 SDK (или изменить TargetFramework в csproj на установленную версию)
- Запуск на Windows (WPF)
- robocopy доступен в PATH (обычно присутствует в Windows)

Как собрать:

```powershell
dotnet build .\\UCXSyncTool\\UCX.SyncTool.csproj
```

Как запустить (отладка): откройте проект в Visual Studio 2022/2023 или выполните `dotnet run --project .\\UCXSyncTool\\UCX.SyncTool.csproj`.

Примечание по правам доступа: если для доступа к удалённым узлам требуются другие учётные данные, настройте Windows Credentials Manager или запустите приложение под учётной записью, имеющей доступ.
