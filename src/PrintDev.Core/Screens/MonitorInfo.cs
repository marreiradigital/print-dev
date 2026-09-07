namespace PrintDev.Core.Screens;

/// <summary>
/// Um monitor, em pixels físicos.
/// </summary>
/// <param name="Handle">Identificador do monitor no Windows.</param>
/// <param name="DeviceName">Nome do dispositivo, por exemplo <c>\\.\DISPLAY1</c>.</param>
/// <param name="Bounds">Área total, em pixels físicos do desktop virtual.</param>
/// <param name="WorkArea">Área útil, descontando a barra de tarefas.</param>
/// <param name="DpiX">Pontos por polegada na horizontal. 96 equivale a 100%.</param>
/// <param name="DpiY">Pontos por polegada na vertical.</param>
/// <param name="IsPrimary">Se é o monitor principal.</param>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    /// <summary>Fator de escala horizontal. 1,0 em 100%; 1,5 em 150%.</summary>
    public double ScaleX => DpiX / 96.0;

    /// <summary>Fator de escala vertical.</summary>
    public double ScaleY => DpiY / 96.0;

    /// <summary>
    /// Converte um ponto em pixels físicos do desktop virtual para as unidades
    /// independentes de dispositivo deste monitor — que é o que o WPF usa para desenhar
    /// dentro de uma janela posicionada nele.
    /// </summary>
    public (double X, double Y) ToDeviceIndependent(int physicalX, int physicalY)
        => ((physicalX - Bounds.Left) / ScaleX, (physicalY - Bounds.Top) / ScaleY);

    /// <summary>Caminho inverso de <see cref="ToDeviceIndependent"/>.</summary>
    public (int X, int Y) ToPhysical(double deviceIndependentX, double deviceIndependentY)
        => (
            Bounds.Left + (int)Math.Round(deviceIndependentX * ScaleX),
            Bounds.Top + (int)Math.Round(deviceIndependentY * ScaleY));

    /// <inheritdoc/>
    public override string ToString()
        => $"{DeviceName} {Bounds} a {DpiX} PPP{(IsPrimary ? " (principal)" : string.Empty)}";
}
