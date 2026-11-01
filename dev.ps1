param(
    [ValidateSet('up', 'run', 'test', 'api-test', 'down', 'status')]
    [string]$Action = 'run'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'Gamertab\Gamertab.csproj'
$statePath = Join-Path $projectRoot '.local'
$keepAlivePath = Join-Path $statePath 'wsl-process.json'
$nativeDocker = Get-Command docker -ErrorAction SilentlyContinue

if ($Action -eq 'test') {
    dotnet test (Join-Path $projectRoot 'Gamertab.slnx') --verbosity minimal
    exit $LASTEXITCODE
}

function Start-DevelopmentWsl {
    if ($nativeDocker) { return }
    New-Item -ItemType Directory -Force -Path $statePath | Out-Null
    if (Test-Path -LiteralPath $keepAlivePath) {
        $savedProcess = Get-Content -LiteralPath $keepAlivePath -Raw | ConvertFrom-Json
        $existingProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$savedProcess.Id) -ErrorAction SilentlyContinue
        if ($existingProcess.CommandLine -like '*GAMERTAB_KEEPALIVE=1*') { return }
    }
    $keepAliveProcess = Start-Process -FilePath 'wsl.exe' -ArgumentList @('--distribution', 'Ubuntu-24.04', '--user', 'root', '--exec', 'env', 'GAMERTAB_KEEPALIVE=1', 'sleep', 'infinity') -WindowStyle Hidden -PassThru
    @{ Id = $keepAliveProcess.Id } | ConvertTo-Json | Set-Content -LiteralPath $keepAlivePath -Encoding utf8
}

function Invoke-DevelopmentDocker([string[]]$DockerArguments) {
    if ($nativeDocker) {
        & $nativeDocker.Source @DockerArguments
    }
    else {
        & wsl.exe --distribution Ubuntu-24.04 --user root --exec docker @DockerArguments
    }
    if ($LASTEXITCODE -ne 0) { throw 'The Docker command failed.' }
}

if ($Action -in @('up', 'run', 'api-test')) {
    New-Item -ItemType Directory -Force -Path $statePath | Out-Null
    $environmentPath = Join-Path $projectRoot '.env'
    if (-not (Test-Path -LiteralPath $environmentPath)) {
        $passwordBytes = New-Object byte[] 24
        $randomGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
        $randomGenerator.GetBytes($passwordBytes)
        $randomGenerator.Dispose()
        $databasePassword = [BitConverter]::ToString($passwordBytes).Replace('-', '')
        [IO.File]::WriteAllText($environmentPath, "POSTGRES_PASSWORD=$databasePassword`n")
    }
    $passwordLine = Get-Content -LiteralPath $environmentPath | Where-Object { $_.StartsWith('POSTGRES_PASSWORD=') } | Select-Object -First 1
    if (-not $passwordLine) { throw 'The local environment file is missing POSTGRES_PASSWORD.' }
    $databasePassword = $passwordLine.Substring('POSTGRES_PASSWORD='.Length)
    $projectXml = [xml](Get-Content -LiteralPath $projectFile -Raw)
    $secretsId = [string]$projectXml.Project.PropertyGroup.UserSecretsId
    $secretsDirectory = Join-Path $env:APPDATA ('Microsoft\UserSecrets\' + $secretsId)
    New-Item -ItemType Directory -Force -Path $secretsDirectory | Out-Null
    $secretsFile = Join-Path $secretsDirectory 'secrets.json'
    $localSecrets = @{}
    if (Test-Path -LiteralPath $secretsFile) {
        $savedSecrets = Get-Content -LiteralPath $secretsFile -Raw | ConvertFrom-Json
        foreach ($property in $savedSecrets.PSObject.Properties) {
            $localSecrets[$property.Name] = $property.Value
        }
    }
    $localSecrets['ConnectionStrings:Gamertab'] = "Host=127.0.0.1;Port=15432;Database=gamertab;Username=gamertab;Password=$databasePassword"
    $localSecrets | ConvertTo-Json | Set-Content -LiteralPath $secretsFile -Encoding utf8
    Start-DevelopmentWsl
}

if ($nativeDocker) {
    $dockerProjectRoot = $projectRoot
}
else {
    $dockerProjectRoot = (& wsl.exe --distribution Ubuntu-24.04 --exec wslpath -a $projectRoot).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The project path could not be resolved in WSL.' }
}
$composeArguments = @('compose', '--project-directory', $dockerProjectRoot, '-f', ($dockerProjectRoot.TrimEnd('/') + '/compose.yaml'))

switch ($Action) {
    'up' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'db'))
        Write-Output 'The Gamertab database is ready.'
    }
    'run' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'db'))
        dotnet run --project $projectFile
        exit $LASTEXITCODE
    }
    'api-test' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'db'))
        & (Join-Path $projectRoot 'api-tests\run.ps1')
        exit $LASTEXITCODE
    }
    'status' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('ps'))
    }
    'down' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('--profile', 'full', 'down'))
        if (Test-Path -LiteralPath $keepAlivePath) {
            $savedProcess = Get-Content -LiteralPath $keepAlivePath -Raw | ConvertFrom-Json
            $existingProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$savedProcess.Id) -ErrorAction SilentlyContinue
            if ($existingProcess.CommandLine -like '*GAMERTAB_KEEPALIVE=1*') { Stop-Process -Id $savedProcess.Id }
        }
        Write-Output 'Gamertab containers have stopped. Database data is preserved.'
    }
}
