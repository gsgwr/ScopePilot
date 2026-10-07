param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$DataDirectory
)

$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskExecutable = Join-Path $taskRepository "bin\$Configuration\net10.0-windows\ScopePilot.exe"
if (-not (Test-Path -LiteralPath $taskExecutable)) {
    throw "ビルド済みアプリがありません。dotnet build ScopePilot.csproj -c $Configuration を実行してください。"
}
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path $taskRepository 'artifacts\ScopePilot-data'
}
$taskDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
$taskRuntime = Join-Path $taskRepository '.cache\dotnet'
$taskVariables = @('SCOPEPILOT_DATA_DIRECTORY', 'DOTNET_ROOT', 'DOTNET_ROOT_ARM64', 'DOTNET_ROOT_X64')
$taskPreviousValues = @{}
foreach ($taskVariable in $taskVariables) {
    $taskPreviousValues[$taskVariable] = [Environment]::GetEnvironmentVariable($taskVariable, 'Process')
}
try {
    [Environment]::SetEnvironmentVariable('SCOPEPILOT_DATA_DIRECTORY', $taskDataDirectory, 'Process')
    if (Test-Path -LiteralPath (Join-Path $taskRuntime 'dotnet.exe')) {
        foreach ($taskVariable in @('DOTNET_ROOT', 'DOTNET_ROOT_ARM64', 'DOTNET_ROOT_X64')) {
            [Environment]::SetEnvironmentVariable($taskVariable, $taskRuntime, 'Process')
        }
    }
    $taskApplication = Start-Process -FilePath $taskExecutable -WorkingDirectory $taskRepository -WindowStyle Normal -PassThru
    [pscustomobject]@{ ProcessId = $taskApplication.Id; DataDirectory = $taskDataDirectory }
}
finally {
    foreach ($taskVariable in $taskVariables) {
        [Environment]::SetEnvironmentVariable($taskVariable, $taskPreviousValues[$taskVariable], 'Process')
    }
}
