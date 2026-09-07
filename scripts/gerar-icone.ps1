#requires -Version 5.1
<#
.SYNOPSIS
    Gera o icone do Print Dev (.ico multirresolucao) a partir do desenho da marca.

.DESCRIPTION
    A marca sao quatro marcas de corte formando um quadrado vazado, com uma tarja magenta
    cobrindo o canto inferior esquerdo: moldura mais segredo escondido, que e o produto
    inteiro num simbolo.

    O desenho e gerado por codigo, e nao desenhado a mao num editor, por dois motivos:
    fica identico em todas as resolucoes, e mudar a marca e mudar uma constante aqui em
    vez de refazer oito arquivos.

    ESCADA DE SIMPLIFICACAO: em 16 e 20 pixels a tarja magenta vira um bloco de poucos
    pixels sem forma reconhecivel e o traco fino some. Nesses tamanhos o desenho e so os
    quatro colchetes, mais grossos. Insistir no desenho completo em tamanho pequeno
    produz uma mancha, nao um icone.

    FORMATO DOS QUADROS: PNG. O Windows le PNG dentro de .ico desde o Vista, e e assim
    que o Explorador, a barra de tarefas e o WPF carregam este arquivo. A API antiga
    System.Drawing.Icon NAO le quadro em PNG - por isso a conferencia no fim usa o
    decodificador do WPF, que e o caminho que o programa realmente percorre.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/gerar-icone.ps1
#>
[CmdletBinding()]
param(
    [string]$Saida = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore

$Magenta = [System.Drawing.Color]::FromArgb(255, 0xEC, 0x00, 0x8C)
$Claro = [System.Drawing.Color]::FromArgb(255, 0xF3, 0xF5, 0xF9)
$Tamanhos = @(16, 20, 24, 32, 48, 64, 128, 256)

function New-Glifo {
    param([int]$Lado)

    $bmp = New-Object System.Drawing.Bitmap($Lado, $Lado, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pequeno = $Lado -le 20
    $recuo = [double]$Lado * 0.16
    $braco = [double]$Lado * $(if ($pequeno) { 0.30 } else { 0.26 })
    $tracoLargura = [Math]::Max(1.0, [double]$Lado * $(if ($pequeno) { 0.11 } else { 0.085 }))

    $caneta = New-Object System.Drawing.Pen($Claro, $tracoLargura)
    $caneta.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $caneta.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $e = $recuo
    $d = $Lado - $recuo

    # Quatro colchetes de canto.
    $g.DrawLine($caneta, $e, ($e + $braco), $e, $e); $g.DrawLine($caneta, $e, $e, ($e + $braco), $e)
    $g.DrawLine($caneta, ($d - $braco), $e, $d, $e); $g.DrawLine($caneta, $d, $e, $d, ($e + $braco))
    $g.DrawLine($caneta, $d, ($d - $braco), $d, $d); $g.DrawLine($caneta, $d, $d, ($d - $braco), $d)
    $g.DrawLine($caneta, ($e + $braco), $d, $e, $d); $g.DrawLine($caneta, $e, $d, $e, ($d - $braco))

    if (-not $pequeno) {
        # Tarja magenta sobre o canto inferior esquerdo.
        $pincel = New-Object System.Drawing.SolidBrush($Magenta)
        $barraX = [double]$Lado * 0.12
        $barraY = [double]$Lado * 0.60
        $barraL = [double]$Lado * 0.46
        $barraA = [Math]::Max(2.0, [double]$Lado * 0.15)
        $g.FillRectangle($pincel, $barraX, $barraY, $barraL, $barraA)
        $pincel.Dispose()
    }

    $caneta.Dispose()
    $g.Dispose()
    return $bmp
}

# ---- Monta o .ico ----
$imagens = @()
foreach ($lado in $Tamanhos) {
    $bmp = New-Glifo -Lado $lado
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $imagens += , @{ Lado = $lado; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# O caminho e resolvido AQUI, e nao no valor padrao do parametro: $PSScriptRoot nao e
# confiavel na hora de vincular parametros, e o resultado foi um icone gravado na raiz do
# disco em vez de dentro do projeto.
if ([string]::IsNullOrWhiteSpace($Saida)) {
    $raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Saida = Join-Path $raiz '..\src\PrintDev.App\Assets\PrintDev.ico'
}

$destino = [System.IO.Path]::GetFullPath($Saida)
New-Item -ItemType Directory -Force -Path (Split-Path $destino) | Out-Null
$fs = [System.IO.File]::Create($destino)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                      # reservado
$bw.Write([UInt16]1)                      # tipo: icone
$bw.Write([UInt16]$imagens.Count)

$deslocamento = 6 + (16 * $imagens.Count)
foreach ($img in $imagens) {
    # 256 e gravado como 0: o campo de tamanho tem um byte so.
    $bw.Write([Byte]$(if ($img.Lado -ge 256) { 0 } else { $img.Lado }))
    $bw.Write([Byte]$(if ($img.Lado -ge 256) { 0 } else { $img.Lado }))
    $bw.Write([Byte]0)                    # cores da paleta
    $bw.Write([Byte]0)                    # reservado
    $bw.Write([UInt16]1)                  # planos
    $bw.Write([UInt16]32)                 # bits por pixel
    $bw.Write([UInt32]$img.Bytes.Length)
    $bw.Write([UInt32]$deslocamento)
    $deslocamento += $img.Bytes.Length
}

foreach ($img in $imagens) { $bw.Write($img.Bytes) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

# ---- Conferencia ----
# Pelo decodificador do WPF, que e o caminho real: se ele ler todos os quadros, o
# Explorador e a barra de tarefas leem tambem.
$stream = [System.IO.File]::OpenRead($destino)
$decoder = New-Object System.Windows.Media.Imaging.IconBitmapDecoder(
    $stream,
    [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
    [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)

$lidos = @()
foreach ($frame in $decoder.Frames) { $lidos += "$($frame.PixelWidth)x$($frame.PixelHeight)" }
$quadros = $decoder.Frames.Count
$stream.Dispose()

Write-Output ("Icone gerado: {0}" -f $destino)
Write-Output ("  {0} bytes, {1} quadros lidos: {2}" -f (Get-Item $destino).Length, $quadros, ($lidos -join ', '))

if ($quadros -ne $imagens.Count) {
    throw "Gravados $($imagens.Count) quadros, mas so $quadros foram lidos de volta."
}
