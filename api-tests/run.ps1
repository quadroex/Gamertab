$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$projectFile = Join-Path $projectRoot 'Gamertab\Gamertab.csproj'
$statePath = Join-Path $projectRoot '.local'
$postmanPath = Join-Path $statePath 'tools\postman\postman-cli.exe'
if (-not (Test-Path -LiteralPath $postmanPath)) {
    $postmanCommand = Get-Command postman -ErrorAction SilentlyContinue
    if (-not $postmanCommand) { throw 'Install Postman CLI or place it in .local\tools\postman\postman-cli.exe.' }
    $postmanPath = $postmanCommand.Source
}

dotnet build $projectFile --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$testDatabase = 'gamertab_api_test_' + [Guid]::NewGuid().ToString('N')
$databaseCreated = $false
$server = $null
$reportPath = Join-Path $statePath ('postman\' + $testDatabase)
New-Item -ItemType Directory -Force -Path $reportPath | Out-Null
$passwordLine = Get-Content -LiteralPath (Join-Path $projectRoot '.env') | Where-Object { $_.StartsWith('POSTGRES_PASSWORD=') } | Select-Object -First 1
if (-not $passwordLine) { throw 'Run dev.ps1 api-test from the project folder.' }
$databasePassword = $passwordLine.Substring('POSTGRES_PASSWORD='.Length)
$nativeDocker = Get-Command docker -ErrorAction SilentlyContinue

function Invoke-TestSql([string]$Sql) {
    $dockerArguments = @('exec', 'gamertab-db-1', 'psql', '-U', 'gamertab', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', $Sql)
    if ($nativeDocker) { & $nativeDocker.Source @dockerArguments }
    else { & wsl.exe --distribution Ubuntu-24.04 --user root --exec docker @dockerArguments }
    if ($LASTEXITCODE -ne 0) { throw 'The test database command failed.' }
}

try {
    Invoke-TestSql "CREATE DATABASE $testDatabase"
    $databaseCreated = $true
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $serverInfo = [Diagnostics.ProcessStartInfo]::new()
    $serverInfo.FileName = (Get-Command dotnet).Source
    $serverInfo.WorkingDirectory = Join-Path $projectRoot 'Gamertab'
    $serverInfo.Arguments = '"' + (Join-Path $projectRoot 'Gamertab\bin\Debug\net10.0\Gamertab.dll') + '" --urls ' + $baseUrl
    $serverInfo.UseShellExecute = $false
    $serverInfo.CreateNoWindow = $true
    $serverInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $serverInfo.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
    $serverInfo.EnvironmentVariables['ConnectionStrings__Gamertab'] = "Host=127.0.0.1;Port=15432;Database=$testDatabase;Username=gamertab;Password=$databasePassword"
    $serverInfo.EnvironmentVariables['Igdb__ClientId'] = ''
    $serverInfo.EnvironmentVariables['Igdb__ClientSecret'] = ''
    $serverInfo.EnvironmentVariables['Authentication__Google__ClientId'] = ''
    $serverInfo.EnvironmentVariables['Authentication__Google__ClientSecret'] = ''
    $serverInfo.EnvironmentVariables['Logging__LogLevel__Default'] = 'Warning'
    $server = [Diagnostics.Process]::Start($serverInfo)
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($server.HasExited) { throw 'The API test server stopped before it was ready.' }
        try {
            $health = Invoke-RestMethod -Uri ($baseUrl + '/health') -TimeoutSec 1
            if ($health.status -eq 'ok') { $ready = $true; break }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'The API test server did not become ready.' }
    & $postmanPath collection run (Join-Path $PSScriptRoot 'Gamertab.postman_collection.json') --env-var "baseUrl=$baseUrl" --no-report-events --timeout 120000 --timeout-request 10000 --timeout-script 5000 | Tee-Object -FilePath (Join-Path $reportPath 'results.txt')
    $testExitCode = $LASTEXITCODE
    Write-Output "Postman report: $reportPath"
}
finally {
    if ($server) {
        if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
        $server.Dispose()
    }
    if ($databaseCreated) { Invoke-TestSql "DROP DATABASE $testDatabase WITH (FORCE)" }
}

exit $testExitCode
