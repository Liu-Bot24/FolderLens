[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppRoot,[Parameter(Mandatory=$true)][string]$Image,[Parameter(Mandatory=$true)][string]$EvidenceRoot,[ValidateSet('native','soft','glass')][string[]]$Style=@('native','soft','glass'))
# Real on-screen check: the app takes the foreground and toggles F11. With the
# title bar extended into content, the full-screen presenter once left the XAML
# island one pixel below the screen top, showing a line of window background.
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Text;using System.Collections.Generic;
public static class FullScreenEdges{
 [StructLayout(LayoutKind.Sequential)]public struct RECT{public int L,T,R,B;public override string ToString(){return L+","+T+" "+(R-L)+"x"+(B-T);}}
 [StructLayout(LayoutKind.Sequential)]public struct MI{public int cb;public RECT M,Wk;public int F;}
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32")]static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32")]static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr l);
 [DllImport("user32")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32")]public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32")]public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32")]static extern IntPtr MonitorFromWindow(IntPtr h,int f);
 [DllImport("user32")]static extern bool GetMonitorInfo(IntPtr m,ref MI i);
 [DllImport("user32")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
 public static IntPtr Main(uint pid){var found=IntPtr.Zero;EnumWindows((h,_)=>{uint p;GetWindowThreadProcessId(h,out p);var s=new StringBuilder(64);GetWindowText(h,s,64);if(p==pid&&IsWindowVisible(h)&&s.ToString()=="FolderLens")found=h;return true;},IntPtr.Zero);return found;}
 public static RECT? Child(IntPtr w,string cls){RECT? r=null;EnumChildWindows(w,(h,_)=>{var s=new StringBuilder(256);GetClassName(h,s,256);RECT x;if(s.ToString()==cls&&IsWindowVisible(h)&&GetWindowRect(h,out x)&&x.R>x.L)r=x;return true;},IntPtr.Zero);return r;}
 public static RECT Monitor(IntPtr h){var i=new MI{cb=40};GetMonitorInfo(MonitorFromWindow(h,2),ref i);return i.M;}
}
'@
if(-not(Test-Path -LiteralPath $Image -PathType Leaf)){throw "Image not found: $Image"}
$exe=Join-Path $AppRoot 'FolderLens.App.exe'
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$results=@();$failed=$false
foreach($appearance in $Style){
 $data=Join-Path $EvidenceRoot "$appearance\data";New-Item -ItemType Directory -Force -Path (Join-Path $data 'config') | Out-Null
 Set-Content -LiteralPath (Join-Path $data 'config\appearance.json') -Value ('{"Style":"'+$appearance+'"}') -NoNewline
 $process=Start-Process -FilePath $exe -ArgumentList @('--data-dir',"`"$data`"",'--open',"`"$Image`"") -PassThru
 try{
  $window=[IntPtr]::Zero;$deadline=(Get-Date).AddSeconds(30)
  while($window -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 300;$window=[FullScreenEdges]::Main([uint32]$process.Id)}
  if($window -eq [IntPtr]::Zero){throw "No FolderLens window for $appearance."}
  Start-Sleep -Seconds 4
  $steps=@()
  # F11 enters, F11 returns to window view, F11 re-enters, Esc returns to the list.
  foreach($step in @(@{key=0x7A;full=$true;name='enter'},@{key=0x7A;full=$false;name='window-view'},@{key=0x7A;full=$true;name='re-enter'},@{key=0x1B;full=$false;name='return'})){
   [FullScreenEdges]::SetForegroundWindow($window) | Out-Null;Start-Sleep -Milliseconds 300
   [FullScreenEdges]::keybd_event([byte]$step.key,0,0,[UIntPtr]::Zero);[FullScreenEdges]::keybd_event([byte]$step.key,0,2,[UIntPtr]::Zero);Start-Sleep -Seconds 3
   $monitor=[FullScreenEdges]::Monitor($window);$island=[FullScreenEdges]::Child($window,'Microsoft.UI.Content.DesktopChildSiteBridge');$caption=[FullScreenEdges]::Child($window,'ReunionWindowingCaptionControls')
   $pass=if($step.full){$null -ne $island -and $island.ToString() -eq $monitor.ToString() -and $null -eq $caption}else{$null -ne $island -and $null -ne $caption -and $island.Value.T -eq $caption.Value.T}
   if(-not $pass){$failed=$true}
   $steps+=[ordered]@{step=$step.name;fullScreen=$step.full;monitor="$monitor";island="$island";captionButtons="$caption";status=$(if($pass){'PASS'}else{'FAIL'})}
  }
  $results+=[ordered]@{appearance=$appearance;steps=$steps}
 }finally{if(-not $process.HasExited){Stop-Process -Id $process.Id}}
}
$report=[ordered]@{appRoot=$AppRoot;status=$(if($failed){'FAIL'}else{'PASS'});results=$results}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'fullscreen-edges.json')
foreach($r in $results){foreach($s in $r.steps){Write-Output ("{0,-7} {1,-12} {2}  island={3}  monitor={4}" -f $r.appearance,$s.step,$s.status,$s.island,$s.monitor)}}
if($failed){Write-Output 'FAIL';exit 1}
Write-Output 'PASS'
