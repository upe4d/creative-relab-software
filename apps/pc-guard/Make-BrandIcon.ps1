param([string]$Source=(Join-Path $PSScriptRoot 'brand-avatar.png'),[string]$Destination=$PSScriptRoot,[string]$IconName='app.ico',[string]$PngName='app-icon.png')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
# Сохраняем предоставленный аватар целиком; меняются только размеры для PNG и ICO.
$original=[Drawing.Image]::FromFile($Source)
$sizes=@(16,24,32,48,64,128,256)
$images=@()
try {
 foreach($size in $sizes){
  $bitmap=New-Object Drawing.Bitmap $size,$size
  $canvas=[Drawing.Graphics]::FromImage($bitmap)
  $stream=New-Object IO.MemoryStream
  try {
   $canvas.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $canvas.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
   $canvas.DrawImage($original,0,0,$size,$size)
   $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
   $images+=,@($stream.ToArray())
   if($size-eq256){[IO.File]::WriteAllBytes((Join-Path $Destination $PngName),$stream.ToArray())}
  } finally {$canvas.Dispose();$bitmap.Dispose();$stream.Dispose()}
 }
 $writer=New-Object IO.BinaryWriter ([IO.File]::Create((Join-Path $Destination $IconName)))
 try {
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i-lt$sizes.Count;$i++){
   $dimension=if($sizes[$i]-eq256){0}else{$sizes[$i]}
   $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
   $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset)
   $offset+=$images[$i].Length
  }
  foreach($bytes in $images){$writer.Write([byte[]]$bytes)}
 } finally {$writer.Dispose()}
} finally {$original.Dispose()}
