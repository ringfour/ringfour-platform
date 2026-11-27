$ErrorActionPreference = 'Stop'
$repo = 'https://github.com/ringfour/ringfour-platform'
$release = Invoke-RestMethod 'https://api.github.com/repos/ringfour/ringfour-platform/releases/latest'
$tag = $release.tag_name
if ($tag -cnotmatch '^main-\d+-\d+$') { throw 'No Ringfour main release found' }
$assets = "$repo/releases/download/$tag"
$directory = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid()))
try {
    $checksumsPath = Join-Path $directory.FullName 'SHA256SUMS'
    Invoke-WebRequest "$assets/SHA256SUMS" -OutFile $checksumsPath -UseBasicParsing
    $checksums = Get-Content -LiteralPath $checksumsPath
    $entries = @($checksums | Where-Object { $_ -cmatch '^[a-f0-9]{64}  ringfour_cli-[a-zA-Z0-9.+_]+-py3-none-any\.whl$' })
    if ($entries.Count -ne 1) { throw "Expected one CLI wheel in release $tag" }
    $hash, $wheel = $entries[0] -split '  ', 2
    $wheelPath = Join-Path $directory.FullName $wheel
    Invoke-WebRequest "$assets/$wheel" -OutFile $wheelPath -UseBasicParsing
    if ((Get-FileHash -Algorithm SHA256 $wheelPath).Hash -ne $hash) { throw 'CLI wheel checksum mismatch' }

    $uv = (Get-Command uv -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (!$uv) {
        $uvDir = Join-Path $env:USERPROFILE '.local\bin'
        $uv = Join-Path $uvDir 'uv.exe'
        if (!(Test-Path -LiteralPath $uv)) {
            $installer = Join-Path $directory.FullName 'install-uv.ps1'
            Invoke-WebRequest 'https://astral.sh/uv/0.12.1/install.ps1' -OutFile $installer -UseBasicParsing
            $env:UV_INSTALL_DIR = $uvDir
            $env:UV_NO_MODIFY_PATH = '1'
            try {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
                if ($LASTEXITCODE -ne 0) { throw 'uv installation failed' }
            } finally {
                Remove-Item Env:UV_INSTALL_DIR, Env:UV_NO_MODIFY_PATH -ErrorAction SilentlyContinue
            }
        }
    }
    & $uv tool install --force --python 3.13 $wheelPath
    if ($LASTEXITCODE -ne 0) { throw 'Ringfour CLI installation failed' }
    & $uv tool update-shell
    if ($LASTEXITCODE -ne 0) { throw 'Unable to add CLI tools to PATH' }
    Write-Output 'Open a new terminal and run ringfour-cli login.'
} finally {
    [IO.Directory]::Delete($directory.FullName, $true)
}
