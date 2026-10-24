param(
    [string]$NativeHostPath,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$hostName = "com.independentdownloadmanager.bridge"
$extensionId = "nbadibcelhhpkkdjlocddlhgdbcbappn"
$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
$manifestDirectory = Join-Path $env:LOCALAPPDATA "IndependentDownloadManager"
$manifestPath = Join-Path $manifestDirectory "$hostName.json"

if ($Uninstall) {
    if (Test-Path $registryPath) {
        Remove-Item -Path $registryPath -Recurse -Force
    }
    if (Test-Path $manifestPath) {
        Remove-Item -Path $manifestPath -Force
    }
    Write-Output "Removed the Chrome native messaging host registration."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($NativeHostPath)) {
    $workspaceRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $workspaceRoot "bin\Release\net8.0-windows\win-x64\publish\BridgeReady\NativeMessagingHost.exe"),
        (Join-Path $workspaceRoot "bin\Release\net8.0-windows\win-x64\publish\Updated\NativeMessagingHost.exe"),
        (Join-Path $workspaceRoot "native-host\bin\Release\net8.0-windows\win-x64\publish\NativeMessagingHost.exe")
    )
    $NativeHostPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($NativeHostPath) -or -not (Test-Path $NativeHostPath -PathType Leaf)) {
    throw "Could not find NativeMessagingHost.exe. Publish the app bundle, or pass -NativeHostPath."
}

$NativeHostPath = (Resolve-Path $NativeHostPath).Path
$desktopPath = Join-Path (Split-Path -Parent $NativeHostPath) "DownloadManager.exe"
if (-not (Test-Path $desktopPath -PathType Leaf)) {
    throw "DownloadManager.exe must be installed beside NativeMessagingHost.exe."
}
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
$manifest = @{
    name = $hostName
    description = "Connects the independent download manager Chrome extension to its desktop app."
    path = $NativeHostPath
    type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}
$manifestJson = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    $manifestPath,
    $manifestJson,
    [System.Text.UTF8Encoding]::new($false))

New-Item -Path $registryPath -Force | Out-Null
Set-Item -Path $registryPath -Value $manifestPath

Write-Output "Registered $hostName for Chrome extension $extensionId."
Write-Output "Native host executable: $NativeHostPath"
Write-Output "Restart Chrome after loading or updating the extension."
