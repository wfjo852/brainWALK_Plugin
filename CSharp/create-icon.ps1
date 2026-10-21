param(
    [string]$Source = (Join-Path $PSScriptRoot '..\icon\setting_icon.png'),
    [string]$Destination = (Join-Path $PSScriptRoot 'BrainWalk.ico')
)

$ErrorActionPreference = 'Stop'
$png = [System.IO.File]::ReadAllBytes($Source)
if ($png.Length -lt 24 -or $png[0] -ne 0x89 -or $png[1] -ne 0x50 -or $png[2] -ne 0x4E -or $png[3] -ne 0x47) {
    throw "The application icon source is not a valid PNG: $Source"
}

$width = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 16))
$height = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($png, 20))
$stream = [System.IO.File]::Create($Destination)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)                    # reserved
    $writer.Write([uint16]1)                    # ICO image type
    $writer.Write([uint16]1)                    # one image
    $writer.Write([byte]$(if ($width -ge 256) { 0 } else { $width }))
    $writer.Write([byte]$(if ($height -ge 256) { 0 } else { $height }))
    $writer.Write([byte]0)                      # color palette
    $writer.Write([byte]0)                      # reserved
    $writer.Write([uint16]1)                    # color planes
    $writer.Write([uint16]32)                   # bits per pixel
    $writer.Write([uint32]$png.Length)
    $writer.Write([uint32]22)                   # PNG data offset
    $writer.Write($png)
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}
