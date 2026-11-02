param(
    [ValidateSet('up', 'run', 'test', 'browser-test', 'api-test', 'queue-test', 'blob-test', 'down', 'status')]
    [string]$Action = 'run'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'Gamertab\Gamertab.csproj'
$statePath = Join-Path $projectRoot '.local'
$keepAlivePath = Join-Path $statePath 'wsl-process.json'
$nativeDocker = Get-Command docker -ErrorAction SilentlyContinue

if ($Action -eq 'test') {
    dotnet test (Join-Path $projectRoot 'Gamertab.slnx') --artifacts-path (Join-Path $statePath 'artifacts') --filter 'Category!=Browser' --verbosity minimal
    exit $LASTEXITCODE
}

if ($Action -eq 'browser-test') {
    $resultsPath = Join-Path $statePath ('browser-tests\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    dotnet test (Join-Path $projectRoot 'Gamertab.slnx') --artifacts-path (Join-Path $statePath 'artifacts') --filter 'Category=Browser' --collect:'XPlat Code Coverage' --results-directory $resultsPath --verbosity minimal
    $testExitCode = $LASTEXITCODE
    $coverageFile = Get-ChildItem -LiteralPath $resultsPath -Filter 'coverage.cobertura.xml' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($coverageFile) {
        $coverage = [xml](Get-Content -LiteralPath $coverageFile.FullName -Raw)
        $culture = [Globalization.CultureInfo]::InvariantCulture
        $lineCoverage = 100 * [double]::Parse($coverage.coverage.'line-rate', $culture)
        $branchCoverage = 100 * [double]::Parse($coverage.coverage.'branch-rate', $culture)
        Write-Output ('Application line coverage: {0:0.0}%' -f $lineCoverage)
        Write-Output ('Application branch coverage: {0:0.0}%' -f $branchCoverage)
        Write-Output ('Coverage report: ' + $coverageFile.FullName)
    }
    exit $testExitCode
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

if ($Action -in @('up', 'run', 'api-test', 'queue-test', 'blob-test')) {
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
    $integrationKeys = @{
        IGDB_CLIENT_ID = 'Igdb:ClientId'
        IGDB_CLIENT_SECRET = 'Igdb:ClientSecret'
        GOOGLE_CLIENT_ID = 'Authentication:Google:ClientId'
        GOOGLE_CLIENT_SECRET = 'Authentication:Google:ClientSecret'
    }
    foreach ($line in (Get-Content -LiteralPath $environmentPath)) {
        foreach ($item in $integrationKeys.GetEnumerator()) {
            if ($line.StartsWith($item.Key + '=')) {
                $value = $line.Substring($item.Key.Length + 1)
                if (-not [string]::IsNullOrWhiteSpace($value)) { $localSecrets[$item.Value] = $value }
            }
        }
    }
    $localSecrets['ConnectionStrings:Gamertab'] = "Host=127.0.0.1;Port=15432;Database=gamertab;Username=gamertab;Password=$databasePassword"
    if ($Action -in @('up', 'run', 'queue-test', 'blob-test')) {
        $localSecrets['ConnectionStrings:QueueStorage'] = 'UseDevelopmentStorage=true'
        $localSecrets['NewsUpdates:Enabled'] = 'true'
    }
    $localSecrets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $secretsFile -Encoding utf8
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
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'db', 'storage'))
        Write-Output 'The Gamertab database and local storage are ready.'
    }
    'run' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'db', 'storage'))
        $runEnvironment = @{
            DOTNET_ENVIRONMENT = 'Development'
            ASPNETCORE_ENVIRONMENT = 'Development'
        }
        foreach ($name in @('Igdb:ClientId', 'Igdb:ClientSecret', 'Authentication:Google:ClientId', 'Authentication:Google:ClientSecret')) {
            if (-not [string]::IsNullOrWhiteSpace($localSecrets[$name])) {
                $runEnvironment[$name.Replace(':', '__')] = $localSecrets[$name]
            }
        }
        $savedEnvironment = @{}
        try {
            foreach ($item in $runEnvironment.GetEnumerator()) {
                $savedEnvironment[$item.Key] = [Environment]::GetEnvironmentVariable($item.Key)
                [Environment]::SetEnvironmentVariable($item.Key, $item.Value)
            }
            $igdbReady = -not [string]::IsNullOrWhiteSpace($env:Igdb__ClientId) -and -not [string]::IsNullOrWhiteSpace($env:Igdb__ClientSecret)
            Write-Output ('Local integration settings: ' + (Join-Path $projectRoot '.env'))
            Write-Output ('Gamertab local environment: Development. Live discovery configured: ' + $igdbReady)
            dotnet run --project $projectFile --launch-profile Gamertab
            $runExitCode = $LASTEXITCODE
        }
        finally {
            foreach ($item in $savedEnvironment.GetEnumerator()) {
                [Environment]::SetEnvironmentVariable($item.Key, $item.Value)
            }
        }
        exit $runExitCode
    }
    'api-test' {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'db'))
        & (Join-Path $projectRoot 'api-tests\run.ps1')
        exit $LASTEXITCODE
    }
    { $_ -in @('queue-test', 'blob-test') } {
        Invoke-DevelopmentDocker -DockerArguments ($composeArguments + @('up', '--detach', '--wait', 'storage'))
        $connectionVariable = if ($Action -eq 'queue-test') { 'GAMERTAB_QUEUE_TEST_CONNECTION' } else { 'GAMERTAB_BLOB_TEST_CONNECTION' }
        $testFilter = if ($Action -eq 'queue-test') { 'FullyQualifiedName~NewsQueueTests' } else { 'FullyQualifiedName~CollectionCoverTests|FullyQualifiedName~ApiTests.CollectionsSupportMembershipAndKeepLibraryDataOnDeletion' }
        $savedTestConnection = [Environment]::GetEnvironmentVariable($connectionVariable)
        try {
            [Environment]::SetEnvironmentVariable($connectionVariable, 'UseDevelopmentStorage=true')
            dotnet test (Join-Path $projectRoot 'Gamertab.slnx') --filter $testFilter --verbosity minimal
            $testExitCode = $LASTEXITCODE
        }
        finally {
            [Environment]::SetEnvironmentVariable($connectionVariable, $savedTestConnection)
        }
        exit $testExitCode
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
