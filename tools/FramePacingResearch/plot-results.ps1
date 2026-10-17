param([Parameter(Mandatory=$true)][string]$ResearchDirectory)
# Uses Windows/.NET Framework's standard Charting library. No package installation.
# Run with Windows PowerShell 5.1 (powershell.exe -NoProfile -File ...).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms.DataVisualization
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath($ResearchDirectory)
function Save-Curve([string]$Scenario, [int]$Limit) {
    $destination = Join-Path $root ($Scenario + '-frame-times.png')
    if (Test-Path -LiteralPath $destination) { throw 'Plot already exists.' }
    $chart = New-Object System.Windows.Forms.DataVisualization.Charting.Chart
    $chart.Width = 1400; $chart.Height = 760; $chart.BackColor = [Drawing.Color]::White
    $area = New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea 'Timing'
    $area.AxisX.Title = 'Frame index (synthetic clock)'; $area.AxisY.Title = 'Frame interval (ms)'
    $area.AxisX.MajorGrid.LineColor = [Drawing.Color]::Gainsboro; $area.AxisY.MajorGrid.LineColor = [Drawing.Color]::Gainsboro
    $area.AxisY.Minimum = 0
    $chart.ChartAreas.Add($area)
    $legend = New-Object System.Windows.Forms.DataVisualization.Charting.Legend 'Legend'
    $legend.Docking = [Windows.Forms.DataVisualization.Charting.Docking]::Bottom
    $chart.Legends.Add($legend)
    $title = $chart.Titles.Add('Synthetic pacing: ' + $Scenario + ' | NOT Delta Force / NVIDIA measurements')
    $title.Font = New-Object Drawing.Font('Segoe UI', 16)
    # Draw absolute last and mark long frames so overlapping spikes cannot hide its 35ms stall.
    foreach ($mode in @('relative','absolute')) {
        $series = New-Object System.Windows.Forms.DataVisualization.Charting.Series $mode
        $series.ChartType = [Windows.Forms.DataVisualization.Charting.SeriesChartType]::Line
        $series.BorderWidth = 2
        $series.Color = if ($mode -eq 'absolute') { [Drawing.Color]::FromArgb(20,100,170) } else { [Drawing.Color]::FromArgb(210,100,30) }
        $data = Import-Csv -LiteralPath (Join-Path $root ($mode + '-' + $Scenario + '.csv')) | Select-Object -First $Limit
        foreach ($frame in $data) {
            if ($frame.WaitStatus -eq 'completed' -and [double]$frame.PresentIntervalMs -gt 0) {
                $index = $series.Points.AddXY([double]$frame.FrameIndex,[double]::Parse($frame.PresentIntervalMs,[Globalization.CultureInfo]::InvariantCulture))
                if ([double]$frame.PresentIntervalMs -gt 20) {
                    $series.Points[$index].MarkerStyle = [Windows.Forms.DataVisualization.Charting.MarkerStyle]::Circle
                    $series.Points[$index].MarkerSize = 8
                }
            }
        }
        $chart.Series.Add($series)
    }
    $chart.SaveImage($destination, [Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)
    $chart.Dispose()
    Write-Output $destination
}
Save-Curve 'jitter' 500
Save-Curve 'long-frame' 500
