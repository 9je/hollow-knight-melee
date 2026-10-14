# Inside winvm (see windows-test.sh): a player's Windows install of Hollow Knight: Melee, end to end, unattended.
# Steam's Hollow Knight folder (the game's code only: the installer touches nothing else), the player's ISO in Downloads,
# a save; then the release's installer as a player runs it, and the core it built plays 300 frames of Fox.
$ErrorActionPreference = 'Stop'
$r = 'C:\winvm\results'
New-Item -ItemType Directory -Force $r | Out-Null
function Check($ok, $what) { $line = "$(if ($ok) { 'OK  ' } else { 'FAIL' }) $what"; Add-Content "$r\checks.txt" $line; Write-Output $line }

$hk = 'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight'
New-Item -ItemType Directory -Force "$hk\hollow_knight_Data" | Out-Null
Move-Item C:\winvm\Managed "$hk\hollow_knight_Data\Managed"
$iso = "$env:USERPROFILE\Downloads\melee.iso"
Move-Item C:\winvm\melee.iso $iso
# the ISO as if picked in the installer's window before (it remembers it): nobody can click that window here
$cache = "$env:LOCALAPPDATA\HollowKnightMelee"
New-Item -ItemType Directory -Force $cache | Out-Null
Set-Content -Encoding ascii "$cache\installer.json" (@{ iso = $iso } | ConvertTo-Json)
$saves = "$env:USERPROFILE\AppData\LocalLow\Team Cherry\Hollow Knight"
New-Item -ItemType Directory -Force $saves | Out-Null
Set-Content "$saves\user1.dat" 'a save'

Write-Output "running the installer (its output: results\installer-output.txt)"
$t = Get-Date
cmd /c "C:\winvm\HollowKnightMeleeInstaller.exe --install < NUL > C:\winvm\results\installer-output.txt 2>&1"
$code = $LASTEXITCODE
$min = [math]::Round(((Get-Date) - $t).TotalMinutes, 1)
Get-Content "$r\installer-output.txt" | Select-Object -Last 25
Check ($code -eq 0) "installer exit code $code ($min min)"
Copy-Item "$cache\installer.log" $r -ErrorAction SilentlyContinue

$mod = "$hk\hollow_knight_Data\Managed\Mods\FoxMelee"
if (-not (Test-Path $mod)) { $mod = (Get-ChildItem "$hk\hollow_knight_Data\Managed\Mods" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
Check (Test-Path "$hk\hollow_knight_Data\Managed.vanilla\Assembly-CSharp.dll") "the game's Managed folder backed up"
Check (Test-Path "$([Environment]::GetFolderPath('MyDocuments'))\hollow-knight-save-backup\user1.dat") "the saves backed up"
Check ((Get-Item "$hk\hollow_knight_Data\Managed\Assembly-CSharp.dll").Length -ne (Get-Item "$hk\hollow_knight_Data\Managed.vanilla\Assembly-CSharp.dll").Length) "the Modding API installed"
foreach ($f in 'libmelee_core.dll', 'mem1.bin', 'stocks.bin', 'css.bin', 'fox.model', 'melee.sfx') { Check ($mod -and (Test-Path "$mod\$f")) "mod folder has $f" }
Check (Test-Path "$saves\FoxMod.GlobalSettings.json") "settings written (IsoPath)"
if ($mod) { Get-ChildItem $mod | Select-Object Name, Length | Out-String -Width 200 | Set-Content "$r\mod-folder.txt" }

# the core built on this machine, loaded and run as the mod does: Fox, 300 frames
if ($mod -and (Test-Path "$mod\libmelee_core.dll")) {
  $env:PATH = "$mod;$env:PATH"
  Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Core {
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int mc_init(string mem1, string iso);
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mc_error();
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void mc_solo_start(byte[] start, int character);
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int mc_start_match(byte[] start, uint seed, uint flags);
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void mc_set_input(int port, byte[] input);
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int mc_step();
  [DllImport("libmelee_core.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int mc_read_fighter(int port, int follower, byte[] f);
}
"@
  $rc = [Core]::mc_init("$mod\mem1.bin", $iso)
  Check ($rc -eq 0) "core starts ($([Runtime.InteropServices.Marshal]::PtrToStringAnsi([Core]::mc_error())))"
  $s = New-Object byte[] 0x138
  [Core]::mc_solo_start($s, 2)
  Check ([Core]::mc_start_match($s, 1, 0) -eq 0) "a match with Fox starts"
  [Core]::mc_set_input(0, (New-Object byte[] 8))
  1..3 | ForEach-Object { [Core]::mc_set_input($_, $null) }
  $bad = 0; $t = Get-Date
  for ($i = 0; $i -lt 300; $i++) { if ([Core]::mc_step() -ne 0) { $bad++ } }
  $ms = [math]::Round(((Get-Date) - $t).TotalMilliseconds / 300, 2)
  $fb = New-Object byte[] 256
  [void][Core]::mc_read_fighter(0, 0, $fb)
  Check ($bad -eq 0) "300 frames, no errors ($ms ms a frame; Fox action $([BitConverter]::ToUInt32($fb, 0)), y $([BitConverter]::ToSingle($fb, 8)))"
}
$fails = @(Get-Content "$r\checks.txt" | Where-Object { $_ -like 'FAIL*' }).Count
Write-Output "$fails failed"
exit $fails
