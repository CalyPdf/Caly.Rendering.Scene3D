# ECMA-363 block walk over raw .u3d files: header fields and a histogram of block types (descending into modifier chains).
#   pwsh U3dBlocks.ps1 -Dir <folder with .u3d> [-Out report.md]
param([Parameter(Mandatory)][string]$Dir, [string]$Out)

# Keys are hex strings: PowerShell parses a 0xFFFFFFnn literal as a negative Int32, which never equals the UInt32 read from the file.
$names = @{
    '00443355' = 'File Header';            'FFFFFF12' = 'File Reference';       'FFFFFF14' = 'Modifier Chain'
    'FFFFFF15' = 'Priority Update';        'FFFFFF16' = 'New Object Type';      'FFFFFF21' = 'Group Node'
    'FFFFFF22' = 'Model Node';             'FFFFFF23' = 'Light Node';           'FFFFFF24' = 'View Node'
    'FFFFFF31' = 'CLOD Mesh Declaration';  'FFFFFF3B' = 'CLOD Base Mesh Continuation'
    'FFFFFF3C' = 'CLOD Progressive Mesh Continuation'
    'FFFFFF36' = 'Point Set Declaration';  'FFFFFF3E' = 'Point Set Continuation'
    'FFFFFF37' = 'Line Set Declaration';   'FFFFFF3F' = 'Line Set Continuation'
    'FFFFFF41' = '2D Glyph Modifier';      'FFFFFF42' = 'Subdivision Modifier'; 'FFFFFF43' = 'Animation Modifier'
    'FFFFFF44' = 'Bone Weight Modifier';   'FFFFFF45' = 'Shading Modifier';     'FFFFFF46' = 'CLOD Modifier'
    'FFFFFF51' = 'Light Resource';         'FFFFFF52' = 'View Resource';        'FFFFFF53' = 'Lit Texture Shader'
    'FFFFFF54' = 'Material Resource';      'FFFFFF55' = 'Texture Declaration';  'FFFFFF5C' = 'Texture Continuation'
    'FFFFFF56' = 'Motion Resource'
}

function Pad([long]$n) { return ($n + 3) -band (-bnot 3L) }

function Walk([byte[]]$d, [int]$start, [int]$end, [hashtable]$counts, [System.Collections.Generic.List[string]]$notes) {
    $pos = $start
    while ($pos + 12 -le $end) {
        $type = [BitConverter]::ToUInt32($d, $pos).ToString('X8')
        $dataSize = [BitConverter]::ToUInt32($d, $pos + 4)
        $metaSize = [BitConverter]::ToUInt32($d, $pos + 8)
        $dataStart = $pos + 12
        $next = $dataStart + (Pad $dataSize) + (Pad $metaSize)
        if ($dataStart + $dataSize -gt $end) { $notes.Add("block at $pos overruns: type 0x$type"); return }

        if (-not $counts.ContainsKey($type)) { $counts[$type] = @(0, 0L) }
        $counts[$type][0]++; $counts[$type][1] += $dataSize

        if ($type -eq '00443355') {
            $notes.Add(('header v{0}.{1} profile 0x{2:X} declSize {3} fileSize {4} encoding {5}' -f `
                [BitConverter]::ToInt16($d, $dataStart), [BitConverter]::ToInt16($d, $dataStart + 2), [BitConverter]::ToUInt32($d, $dataStart + 4),
                [BitConverter]::ToUInt32($d, $dataStart + 8), [BitConverter]::ToUInt64($d, $dataStart + 12), [BitConverter]::ToUInt32($d, $dataStart + 20)))
        }
        elseif ($type -eq 'FFFFFF14') {
            # name, chain type, attributes, optional bounds, padding, modifier count, then nested declaration blocks
            $p = $dataStart + 2 + [BitConverter]::ToUInt16($d, $dataStart)
            $attrs = [BitConverter]::ToUInt32($d, $p + 4)
            $p += 8
            if ($attrs -band 1) { $p += 16 }
            if ($attrs -band 2) { $p += 24 }
            $p = $dataStart + (Pad ($p - $dataStart)) + 4
            Walk $d $p ($dataStart + $dataSize) $counts $notes
        }
        elseif ($type -eq 'FFFFFF16') {
            $len = [BitConverter]::ToUInt16($d, $dataStart)
            $notes.Add("new object type '$([Text.Encoding]::UTF8.GetString($d, $dataStart + 2, $len))'")
        }

        if ($next -gt $end) { return }
        $pos = [int]$next
    }
}

$totals = @{}
$lines = [System.Collections.Generic.List[string]]::new()
$files = Get-ChildItem $Dir -Filter *.u3d | Sort-Object Name
foreach ($f in $files) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $counts = @{}; $notes = [System.Collections.Generic.List[string]]::new()
    Walk $bytes 0 $bytes.Length $counts $notes
    $parts = foreach ($e in ($counts.GetEnumerator() | Sort-Object Key)) {
        $n = if ($names.ContainsKey($e.Key)) { $names[$e.Key] } else { "UNKNOWN 0x$($e.Key)" }
        if (-not $totals.ContainsKey($n)) { $totals[$n] = @(0, 0, 0L) }
        $totals[$n][0]++; $totals[$n][1] += $e.Value[0]; $totals[$n][2] += $e.Value[1]
        "$n x$($e.Value[0])"
    }
    $lines.Add("- **$($f.Name)** ($($bytes.Length) B): $($notes -join '; ')")
    $lines.Add("  - $($parts -join ', ')")
}

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("# U3D block survey: $Dir"); $summary.Add('')
$summary.Add("$($files.Count) files."); $summary.Add('')
$summary.Add('| Block type | Files | Blocks | Data bytes |'); $summary.Add('|---|---:|---:|---:|')
foreach ($e in ($totals.GetEnumerator() | Sort-Object { $_.Value[0] } -Descending)) { $summary.Add("| $($e.Key) | $($e.Value[0]) | $($e.Value[1]) | $($e.Value[2]) |") }
$summary.Add(''); $summary.Add('## Per file'); $summary.Add('')
if ($Out) { Set-Content -Path $Out -Value ($summary + $lines) -Encoding utf8 }
$summary
