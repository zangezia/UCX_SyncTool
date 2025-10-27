# ==========================================================
# Реал-тайм синхронизация проекта с 14 воркеров
# Один процесс robocopy на каждый диск
# Автоматическая остановка при отсутствии новых файлов
# Авторизация через Credential Manager
# ==========================================================

param (
    [Parameter(Mandatory = $true)]
    [string]$Project,      # имя проекта (например Flight_001)
    [Parameter(Mandatory = $true)]
    [string]$DestRoot,     # путь назначения (например D:\Collected)
    [int]$IdleTimeoutMinutes = 5  # время простоя для автоостановки
)

$Nodes = @("WU01","WU02","WU03","WU04","WU05","WU06","WU07",
           "WU08","WU09","WU10","WU11","WU12","WU13","CU")
$Shares = @("E$","F$")

$LogRoot = Join-Path $DestRoot "Logs"
if (!(Test-Path $LogRoot)) { New-Item -Path $LogRoot -ItemType Directory | Out-Null }

Write-Host "-------------------------------------------------------"
Write-Host "  Реал-тайм синхронизация проекта $Project"
Write-Host "  Назначение: $DestRoot"
Write-Host "  Таймаут простоя: $IdleTimeoutMinutes минут"
Write-Host "-------------------------------------------------------"

# Таблица активных процессов и времени последнего изменения
$ActiveCopies = @{}
$LastChange = @{}

while ($true) {
    foreach ($Node in $Nodes) {
        foreach ($Share in $Shares) {

            $Src = "\\$Node\$Share\$Project"
            $Key = "$Node-$Share"

            # Проверяем, есть ли процесс
            if ($ActiveCopies.ContainsKey($Key)) {
                $proc = $ActiveCopies[$Key]

                # Проверка завершения
                if ($proc.HasExited) {
                    Write-Host "[$Node][$Share] Копирование завершено." -ForegroundColor Yellow
                    $ActiveCopies.Remove($Key)
                    $LastChange.Remove($Key)
                    continue
                }

                # Проверяем время последнего изменения файлов
                if (Test-Path $Src) {
                    $latest = (Get-ChildItem -Path $Src -Recurse -ErrorAction SilentlyContinue |
                        Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
                    if ($latest) {
                        $LastChange[$Key] = $latest
                    }
                }

                # Если долго нет новых файлов — останавливаем robocopy
                if ($LastChange.ContainsKey($Key)) {
                    $idleMinutes = (New-TimeSpan -Start $LastChange[$Key] -End (Get-Date)).TotalMinutes
                    if ($idleMinutes -ge $IdleTimeoutMinutes) {
                        Write-Host "[$Node][$Share] Нет новых файлов $IdleTimeoutMinutes минут — останавливаю robocopy." -ForegroundColor Cyan
                        try {
                            Stop-Process -Id $proc.Id -Force
                            $ActiveCopies.Remove($Key)
                            $LastChange.Remove($Key)
                        } catch {}
                    }
                }

                continue
            }

            # Если проекта нет — пропускаем
            if (!(Test-Path $Src)) {
                continue
            }

            # Создаем папки назначения и логов
            $Dest = Join-Path $DestRoot $Project
            if (!(Test-Path $Dest)) { New-Item -Path $Dest -ItemType Directory | Out-Null }

            $LogPath = Join-Path $LogRoot "$Node`_$Share.log"

            Write-Host "[$Node][$Share] Найден проект, запускаю синхронизацию..." -ForegroundColor Green

            # Запускаем robocopy в фоне (один процесс на источник)
            $Args = "`"$Src`" `"$Dest`" /S /E /MON:1 /MOT:2 /FFT /R:2 /W:3 /Z /MT:8 /XD 'System Volume Information' 'RECYCLER' 'RECYCLED' /LOG+:`"$LogPath`""
            $proc = Start-Process -NoNewWindow -FilePath "robocopy.exe" -ArgumentList $Args -PassThru

            # Сохраняем процесс и время последнего изменения
            $ActiveCopies[$Key] = $proc
            if (Test-Path $Src) {
                $LastChange[$Key] = (Get-Date)
            }
        }
    }

    # Проверка каждые 30 секунд
    Start-Sleep -Seconds 30
}
