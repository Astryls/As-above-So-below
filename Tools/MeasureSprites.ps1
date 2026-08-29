# Alpha bounding box of every sprite passed in, in PIXELS and in CELLS.
#
# WHY THIS EXISTS: ABStairAnim.ArtOffsets and the graphicData drawOffsets are both "where is
# the art actually drawn relative to the cell it occupies", and both were previously eyeballed.
# Modmixer ships no imagemagick / PIL / sharp, but Windows PowerShell has System.Drawing, so
# this is the cheapest exact answer available.
#
#   powershell -ExecutionPolicy Bypass -File Tools/MeasureSprites.ps1 -CellsX 2 -CellsZ 2 Textures/Things/Building/AB_Stairs*.png
#
# CellsX/CellsZ are the DRAW size in cells (drawSize), not the footprint - the sprite is
# rasterised across the whole image, so pixels-per-cell = imageWidth / drawSize.x.
param(
  [double]$CellsX = 2,
  [double]$CellsZ = 2,
  [Parameter(ValueFromRemainingArguments = $true)][string[]]$Paths
)

Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class ABBox {
  // minx,miny,maxx,maxy,w,h  (-1s when fully transparent)
  public static int[] Of(string p) {
    using (Bitmap bm = new Bitmap(p)) {
      BitmapData d = bm.LockBits(new Rectangle(0,0,bm.Width,bm.Height),
                                 ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      int n = d.Stride * bm.Height;
      byte[] buf = new byte[n];
      Marshal.Copy(d.Scan0, buf, 0, n);
      bm.UnlockBits(d);
      int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;
      for (int y = 0; y < bm.Height; y++) {
        int row = y * d.Stride;
        for (int x = 0; x < bm.Width; x++) {
          if (buf[row + x*4 + 3] > 8) {
            if (x < minx) minx = x;
            if (x > maxx) maxx = x;
            if (y < miny) miny = y;
            if (y > maxy) maxy = y;
          }
        }
      }
      return new int[] { minx, miny, maxx, maxy, bm.Width, bm.Height };
    }
  }
}
"@ -ReferencedAssemblies System.Drawing

foreach ($spec in $Paths) {
  foreach ($f in Get-ChildItem -Path $spec -ErrorAction SilentlyContinue) {
    $b = [ABBox]::Of($f.FullName)
    if ($b[3] -lt 0) { "{0,-32} EMPTY" -f $f.Name; continue }
    $iw = $b[4]; $ih = $b[5]
    $ppcX = $iw / $CellsX; $ppcZ = $ih / $CellsZ
    $wpx = $b[2] - $b[0] + 1; $hpx = $b[3] - $b[1] + 1
    # Image centre -> bbox centre. Screen y grows DOWN, map z grows UP, so z is negated.
    $cx = ($b[0] + $b[2] + 1) / 2.0 - $iw / 2.0
    $cy = ($b[1] + $b[3] + 1) / 2.0 - $ih / 2.0
    $offX = $cx / $ppcX
    $offZ = -$cy / $ppcZ
    "{0,-30} px bbox {1,3},{2,3} -> {3,3},{4,3}  size {5,3}x{6,3}   cells: size {7,5:N2} x {8,5:N2}   centre offset ({9,6:N3}, {10,6:N3})" -f `
      $f.Name, $b[0], $b[1], $b[2], $b[3], $wpx, $hpx, ($wpx/$ppcX), ($hpx/$ppcZ), $offX, $offZ
  }
}
