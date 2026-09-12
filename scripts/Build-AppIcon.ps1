param([string]$Source = 'src\FolderLens.App\Assets\FolderLens.png', [string]$Destination = 'src\FolderLens.App\Assets\FolderLens.ico')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$original=[Drawing.Image]::FromFile([IO.Path]::GetFullPath($Source))
try {
    $images=@(foreach($size in @(16,20,24,32,40,48,64,128,256)) {
        $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $stream=[IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($original,0,0,$size,$size)
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
        } finally {$stream.Dispose();$graphics.Dispose();$bitmap.Dispose()}
    })
    $file=[IO.File]::Create([IO.Path]::GetFullPath($Destination));$writer=[IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
        $offset=6+16*$images.Count
        foreach($item in $images) {
            $dimension=if($item.Size -eq 256){0}else{$item.Size}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$item.Bytes.Length);$writer.Write([uint32]$offset)
            $offset+=$item.Bytes.Length
        }
        foreach($item in $images){$writer.Write([byte[]]$item.Bytes)}
    } finally {$writer.Dispose()}
} finally {$original.Dispose()}
