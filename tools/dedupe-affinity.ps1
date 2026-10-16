param([string]$Path)
$ErrorActionPreference = 'Stop'
$t = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)

$block = @'
    /// <summary>当前勾选核心的掩码。</summary>
    public ulong AffinityRuleMask
    {
        get
        {
            ulong mask = 0;
            foreach (var core in AffinityCores)
            {
                if (core.IsChecked)
                {
                    mask |= 1UL << core.Index;
                }
            }

            return mask;
        }
    }

'@

$first = $t.IndexOf($block)
if ($first -lt 0) { Write-Output 'block not found'; exit 1 }
$second = $t.IndexOf($block, $first + $block.Length)
if ($second -ge 0)
{
    $t = $t.Remove($second, $block.Length)
    Write-Output 'duplicate removed'
}
else
{
    Write-Output 'no duplicate found'
}

[System.IO.File]::WriteAllText($Path, $t, (New-Object System.Text.UTF8Encoding($true)))
