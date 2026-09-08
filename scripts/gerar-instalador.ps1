#requires -Version 5.1
<#
.SYNOPSIS
    Publica o Print Dev e compila o instalador .exe.

.DESCRIPTION
    Faz as duas etapas na ordem certa. Compilar o instalador sozinho empacotaria a
    publicacao ANTERIOR - um instalador com o programa velho dentro, que compila sem
    erro nenhum e so aparece como "a correcao nao entrou" muito depois.

    Precisa do Inno Setup 6:
        winget install --id JRSoftware.InnoSetup --exact

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/gerar-instalador.ps1
#>
[CmdletBinding()]
param(
    [switch]$PularPublicacao
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $raiz

# ---- 1. Encontrar o compilador ----
$candidatos = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $candidatos | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 nao encontrado. Instale com: winget install --id JRSoftware.InnoSetup --exact"
}

Write-Output "Compilador: $iscc"

# ---- 2. Publicar ----
if (-not $PularPublicacao) {
    # O executavel fica travado enquanto o programa roda, e o build falha na copia -
    # com o erro perdido no meio da saida.
    Get-Process PrintDev -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    # A chave do servico de nuvem entra AQUI, lida do arquivo protegido na hora.
    # Ela nunca aparece no repositorio nem no historico do shell. Sem ela o programa
    # compila igual, so com o envio para a nuvem desativado.
    $arquivoDaChave = Join-Path $env:USERPROFILE '.secrets\printdev-chave-nuvem.txt'
    $chaveDaNuvem = ''

    if (Test-Path $arquivoDaChave) {
        $chaveDaNuvem = (Get-Content $arquivoDaChave -Raw).Trim()
        Write-Output 'Chave da nuvem: encontrada, sera embutida no executavel.'
    } else {
        Write-Warning "Chave da nuvem nao encontrada em $arquivoDaChave - o envio para a nuvem ficara desativado nesta compilacao."
    }

    Write-Output ''
    Write-Output 'Publicando (Release, arquivo unico)...'

    & dotnet publish src/PrintDev.App -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:PublishReadyToRun=true "-p:ChaveDaNuvem=$chaveDaNuvem" `
        -o publish/win-x64 --nologo

    if ($LASTEXITCODE -ne 0) { throw "A publicacao falhou." }
}

$exe = Join-Path $raiz 'publish\win-x64\PrintDev.exe'
if (-not (Test-Path $exe)) { throw "Executavel publicado nao encontrado em $exe" }

Write-Output ("Executavel: {0:N1} MB, compilado em {1}" -f ((Get-Item $exe).Length / 1MB), (Get-Item $exe).LastWriteTime)

# ---- 3. Compilar o instalador ----
Write-Output ''
Write-Output 'Compilando o instalador...'

& $iscc /Qp "installer\PrintDev.iss"
if ($LASTEXITCODE -ne 0) { throw "A compilacao do instalador falhou." }

$saida = Get-ChildItem 'publish\installer\*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Output ''
Write-Output ('Instalador pronto: {0}' -f $saida.FullName)
Write-Output ('  {0:N1} MB' -f ($saida.Length / 1MB))
