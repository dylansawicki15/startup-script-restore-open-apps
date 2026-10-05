<#
  startup-apps.ps1 - At sign-in, reopen the apps that were open when the PC was
  last shut down, each on the monitor and Win+Tab desktop it was on.

  There is no fixed list. A recorder copy of this script (-Watch) runs in the
  background all session and saves the open windows to startup-apps-layout.json
  every 20 s; sign-in reopens exactly what it last saved. Windows' own "restart my
  apps" only covers apps that register with the Restart Manager, and it opens
  every window on the current desktop.

  Modes
    -AtLogon  What the Startup-folder shortcut runs: reopen the saved apps, put
              every saved window back, then start the recorder.
    -Watch    The recorder. Started by -AtLogon; at most one per session.
    (none)    By hand, to top up a session: reopen saved apps that aren't running
              and place only the windows it opened. Safe to repeat.

  Apps Windows already starts at sign-in (Run key: Discord, Steam, ...) are not
  launched a second time, but their windows are still put back. Elevated windows
  are never recorded, so a restore never raises a UAC prompt.
#>
[CmdletBinding(DefaultParameterSetName = 'TopUp')]
param(
    [Parameter(ParameterSetName = 'AtLogon')] [switch]$AtLogon,
    [Parameter(ParameterSetName = 'Watch')]   [switch]$Watch
)
$ErrorActionPreference = 'Stop'

$layoutFile    = Join-Path $env:LOCALAPPDATA 'startup-apps-layout.json'
$logFile       = Join-Path $env:LOCALAPPDATA 'startup-apps.log'
$stagger       = 3     # seconds between launches, so sign-in isn't one big disk fight
$placeTimeout  = 120   # seconds to wait for reopened apps to show their windows
$settleSeconds = 8     # an app must stop opening windows this long before they are paired with saved ones
$watchInterval = 20    # seconds between recordings

function Write-Log {
    param([string]$Message)
    $line = ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Add-Content -LiteralPath $logFile -Value $line
    Write-Host $line
}

function Format-Title([string]$Title) {
    if ($Title.Length -le 50) { $Title } else { $Title.Substring(0, 47) + '...' }
}

# ------------------------------------------------------------------ recording

# The app windows open right now, as records that still mean something after a reboot.
function Get-Layout {
    $folders = $null
    foreach ($w in [StartupApps.Windows]::List()) {
        if ($w.Elevated) { continue }
        $folder = $null
        if ($w.ProcessName -eq 'explorer') {
            if ($null -eq $folders) { $folders = Get-ExplorerFolders }
            $tabs = $folders[[long]$w.Hwnd]
            if (-not $tabs) { continue }   # not a folder window (e.g. a shell dialog) - nothing to reopen
            $shown = @($tabs | Where-Object { $w.Title.StartsWith("$($_.Name) - ") })
            $folder = if ($shown) { $shown[0].Path } else { $tabs[0].Path }
        }
        [pscustomobject]@{
            ProcessName = $w.ProcessName
            Exe         = $w.Exe
            Aumid       = $w.Aumid
            Folder      = $folder
            Title       = $w.Title
            Desktop     = $w.Desktop
            Monitor     = $w.Monitor
            MonLeft     = $w.MonLeft;  MonTop = $w.MonTop;  MonRight = $w.MonRight;  MonBottom = $w.MonBottom
            ShowCmd     = $w.ShowCmd;  Flags  = $w.Flags
            Left        = $w.Left;     Top    = $w.Top;     Right    = $w.Right;     Bottom    = $w.Bottom
        }
    }
}

# Folders shown by each File Explorer window, keyed by window handle. Explorer tabs
# share one window, so a window can have several; Get-Layout keeps the tab whose
# name is in the window title - the one on screen.
function Get-ExplorerFolders {
    $map = @{}
    foreach ($tab in (New-Object -ComObject Shell.Application).Windows()) {
        $folder = $tab.Document.Folder
        if ($null -eq $folder) { continue }   # still navigating, or not a folder view
        $key = [long]$tab.HWND
        if (-not $map.ContainsKey($key)) { $map[$key] = [Collections.Generic.List[object]]::new() }
        $map[$key].Add([pscustomobject]@{ Name = $tab.LocationName; Path = $folder.Self.Path })
    }
    $map
}

function Save-Layout([string]$Json) {
    # Write, then rename over the old file: a crash mid-write leaves the last good one.
    $tmp = "$layoutFile.tmp"
    Set-Content -LiteralPath $tmp -Value $Json -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination $layoutFile -Force
}

function Watch-Layout {
    $mutex = [Threading.Mutex]::new($false, 'Local\startup-apps-recorder')
    try { $owned = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $owned = $true }   # the previous recorder died holding it; it's ours now
    if (-not $owned) { Write-Log 'recorder already running in this session - exiting'; return }

    [StartupApps.Session]::ExitBeforeOtherApps()
    Write-Log ("recording open windows every {0}s to {1} (PID {2})" -f $watchInterval, $layoutFile, $PID)

    $previousShape = $null
    $written = $null
    $failures = 0
    while ($true) {
        try {
            $layout = @(Get-Layout)
            # Belt and braces with ExitBeforeOtherApps: only save a layout seen on two
            # samples in a row, so windows caught mid-close are never saved. Titles
            # are left out of the comparison - they change all the time.
            $shape = ($layout | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.ProcessName, $_.Desktop, $_.Monitor, $_.ShowCmd } | Sort-Object) -join "`n"
            if ($shape -eq $previousShape) {
                $json = ConvertTo-Json -InputObject $layout -Depth 3
                if ($json -ne $written) { Save-Layout $json; $written = $json }
            }
            $previousShape = $shape
            $failures = 0
        } catch {
            $failures++
            Write-Log ("recorder error ($failures of 3 in a row): " + $_.Exception.Message)
            if ($failures -ge 3) { Write-Log 'recorder stopping'; throw }
        }
        Start-Sleep -Seconds $watchInterval
    }
}

function Start-Recorder {
    # CreateNoWindow: no console window at all. -WindowStyle Hidden can't promise
    # that when Windows Terminal is the default terminal, and this runs all session.
    $psi = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    foreach ($arg in '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Watch') { $psi.ArgumentList.Add($arg) }
    $recorder = [Diagnostics.Process]::Start($psi)
    Write-Log "started recorder  PID $($recorder.Id)"
}

# ------------------------------------------------------------------ restoring

function Read-Layout {
    if (-not (Test-Path -LiteralPath $layoutFile)) { Write-Log "no recording yet ($layoutFile) - nothing to reopen"; return }
    $records = @(Get-Content -LiteralPath $layoutFile -Raw | ConvertFrom-Json)
    $required = 'ProcessName', 'Exe', 'Title', 'Desktop', 'MonLeft', 'MonTop', 'MonRight', 'MonBottom', 'ShowCmd', 'Flags', 'Left', 'Top', 'Right', 'Bottom'
    foreach ($r in $records) {
        $missing = @($required | Where-Object { $null -eq $r.$_ })
        if ($missing) { throw "$layoutFile has a record for '$($r.ProcessName)' without $($missing -join ', ') - it wasn't written by this script's recorder" }
    }
    $records
}

# Command lines Windows itself runs at sign-in: the Run keys, minus entries switched
# off in Task Manager > Startup apps (StartupApproved first byte 03 = off).
function Get-RunCommands {
    foreach ($hive in 'HKCU:', 'HKLM:') {
        $run = "$hive\Software\Microsoft\Windows\CurrentVersion\Run"
        if (-not (Test-Path -LiteralPath $run)) { continue }
        $approvedPath = "$hive\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
        $approved = if (Test-Path -LiteralPath $approvedPath) { Get-Item -LiteralPath $approvedPath } else { $null }
        $key = Get-Item -LiteralPath $run
        foreach ($name in $key.GetValueNames()) {
            $state = if ($approved) { $approved.GetValue($name) } else { $null }
            if ($state -is [byte[]] -and ($state[0] -band 1)) { continue }
            $key.GetValue($name)
        }
    }
}

function Test-StartsItself([string]$ProcessName, [string[]]$RunCommands) {
    # Match the exe name anywhere in the command: Discord's entry runs
    # "Update.exe --processStart Discord.exe", not Discord.exe itself.
    $pattern = '(^|[\\/"\s])' + [regex]::Escape("$ProcessName.exe") + '($|["\s])'
    [bool]($RunCommands | Where-Object { $_ -match $pattern })
}

# Squirrel-installed apps (Discord, YAPA, ...) run from app-<version>\ and delete
# old versions after updating. The same-named exe one folder up always starts the
# newest one, so prefer it over the recorded path.
function Resolve-Launcher([string]$Exe) {
    $dir = Split-Path -Parent $Exe
    if ((Split-Path -Leaf $dir) -match '^app-\d+(\.\d+)*$') {
        $stub = Join-Path (Split-Path -Parent $dir) (Split-Path -Leaf $Exe)
        if (Test-Path -LiteralPath $stub) { return $stub }
    }
    $Exe
}

function Start-App([string]$Name, [object]$Saved) {
    try {
        if ($Saved.Aumid) {
            # Store/MSIX apps can't be started from their exe; go through the shell.
            Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($Saved.Aumid)"
        } else {
            $exe = Resolve-Launcher $Saved.Exe
            if (-not (Test-Path -LiteralPath $exe)) { Write-Log ("FAILED  $Name -> path does not exist (uninstalled or moved?): $exe"); return $false }
            Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
        }
        Write-Log "launched          $Name"
        Start-Sleep -Seconds $stagger
        $true
    } catch {
        Write-Log ("FAILED  $Name -> " + $_.Exception.Message)
        $false
    }
}

function Start-Folder([string]$Path) {
    # '::{GUID}' paths are shell places like This PC - they have no Test-Path.
    if (-not $Path.StartsWith('::') -and -not (Test-Path -LiteralPath $Path)) { Write-Log "FAILED  folder $Path -> no longer exists"; return $false }
    try {
        Start-Process -FilePath explorer.exe -ArgumentList ('"{0}"' -f $Path)
        Write-Log "opened            folder $Path"
        $true
    } catch {
        Write-Log ("FAILED  folder $Path -> " + $_.Exception.Message)
        $false
    }
}

function Restore-Session([switch]$PlaceEverything) {
    $saved = @(Read-Layout)
    if ($saved.Count -eq 0) { return }
    $runCommands = @(Get-RunCommands)
    $toPlace = [Collections.Generic.List[object]]::new()

    # 1. Apps. One launch per app - a browser with three windows reopens all three itself.
    foreach ($app in ($saved | Where-Object ProcessName -ne 'explorer' | Group-Object ProcessName)) {
        $name = $app.Name
        $launched = $false
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) { Write-Log "already running   $name" }
        elseif (Test-StartsItself $name $runCommands)               { Write-Log "starts itself     $name (Windows Run key)" }
        else {
            $launched = Start-App $name $app.Group[0]
            if (-not $launched) { continue }
        }
        # At sign-in every saved window goes back. By hand, only the ones this run
        # opened: moving windows you already have open would undo what you just did.
        if ($launched -or $PlaceEverything) { $toPlace.AddRange([object[]]$app.Group) }
    }

    # 2. Folder windows - unless Windows reopens them itself
    #    (Folder Options > "Restore previous folder windows at logon").
    $folders = @($saved | Where-Object ProcessName -eq 'explorer')
    if ($folders) {
        $windowsReopens = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced').PersistBrowsers -eq 1
        $openFolders = @((Get-ExplorerFolders).Values | ForEach-Object { $_.Path })
        foreach ($f in $folders) {
            $opened = $false
            if ($openFolders -contains $f.Folder) { Write-Log "already open      folder $($f.Folder)" }
            elseif ($windowsReopens)              { Write-Log "Windows reopens   folder $($f.Folder)" }
            else                                  { $opened = Start-Folder $f.Folder }
            if ($opened -or $PlaceEverything) { $toPlace.Add($f) }
        }
    }

    # 3. Screens and desktops.
    Restore-Placement $toPlace
}

function Restore-Placement([Collections.Generic.List[object]]$Saved) {
    if ($Saved.Count -eq 0) { return }
    $jobs = @(foreach ($s in $Saved) { [pscustomobject]@{ Saved = $s; Done = $false } })
    $desktops = Connect-VirtualDesktops
    try {
        $claimed = [Collections.Generic.HashSet[long]]::new()
        $lastCount = @{}
        $changedAt = @{}
        $deadline = (Get-Date).AddSeconds($placeTimeout)
        while ((Get-Date) -lt $deadline) {
            $pending = @($jobs | Where-Object { -not $_.Done })
            if ($pending.Count -eq 0) { break }
            $live = @([StartupApps.Windows]::List())
            foreach ($app in ($pending | Group-Object { $_.Saved.ProcessName })) {
                $name = $app.Name
                $mine = @($live | Where-Object { $_.ProcessName -eq $name -and -not $claimed.Contains([long]$_.Hwnd) })
                if ($name -eq 'explorer') {
                    $pairs = @(Select-FolderPairs $mine $app.Group)
                } else {
                    if ($lastCount[$name] -ne $mine.Count) { $lastCount[$name] = $mine.Count; $changedAt[$name] = Get-Date }
                    if (((Get-Date) - $changedAt[$name]).TotalSeconds -ge $settleSeconds) {
                        $pairs = @(Select-WindowPairs $mine $app.Group)
                    } elseif ($app.Group.Count -eq 1) {
                        # Only one window left to place: one with its exact title is it - no need to wait.
                        $job = $app.Group[0]
                        $pairs = @($mine | Where-Object Title -ceq $job.Saved.Title | Select-Object -First 1 |
                                   ForEach-Object { [pscustomobject]@{ Window = $_; Job = $job } })
                    } else {
                        # Several: a title can't settle it until they're all open ("Zen
                        # Browser" x3 while tabs load), so wait for the app to finish.
                        $pairs = @()
                    }
                }
                foreach ($p in $pairs) {
                    Move-SavedWindow $p.Window $p.Job.Saved $desktops
                    [void]$claimed.Add([long]$p.Window.Hwnd)
                    $p.Job.Done = $true
                }
            }
            Start-Sleep -Seconds 2
        }
        foreach ($job in @($jobs | Where-Object { -not $_.Done })) {
            Write-Log ("no window         {0} '{1}' - none appeared within {2}s" -f $job.Saved.ProcessName, (Format-Title $job.Saved.Title), $placeTimeout)
        }
    } finally {
        if ($desktops) { $desktops.Dispose() }
    }
}

# Which of an app's open windows is which saved one. After a reboot titles are weak
# evidence - a browser's windows come back on other pages, or all say "Zen Browser"
# until their tabs load - so every possible pair is scored and the best pairs win:
#   8  same title
#   4  same state (maximized/normal/minimized) - apps restore it per window
#   2  already on the saved desktop - if the app put it back itself, keep it there
#   1  already on the saved screen
# Ties keep front-to-back order.
function Select-WindowPairs([object[]]$Windows, [object[]]$Jobs) {
    $candidates = for ($j = 0; $j -lt $Jobs.Count; $j++) {
        $s = $Jobs[$j].Saved
        foreach ($w in $Windows) {
            $sameScreen = $w.MonLeft -eq $s.MonLeft -and $w.MonTop -eq $s.MonTop -and $w.MonRight -eq $s.MonRight -and $w.MonBottom -eq $s.MonBottom
            [pscustomobject]@{
                Window = $w
                Job    = $j
                Score  = 8 * [int]($w.Title -ceq $s.Title) + 4 * [int]($w.ShowCmd -eq $s.ShowCmd) + 2 * [int]($w.Desktop -eq $s.Desktop) + [int]$sameScreen
            }
        }
    }
    $usedWindows = [Collections.Generic.HashSet[long]]::new()
    $usedJobs = [Collections.Generic.HashSet[int]]::new()
    foreach ($c in ($candidates | Sort-Object Score -Descending -Stable)) {
        if ($usedWindows.Contains([long]$c.Window.Hwnd) -or $usedJobs.Contains($c.Job)) { continue }
        [void]$usedWindows.Add([long]$c.Window.Hwnd)
        [void]$usedJobs.Add($c.Job)
        [pscustomobject]@{ Window = $c.Window; Job = $Jobs[$c.Job] }
    }
}

# A folder window is the saved one if it shows the saved folder - that folder is
# what was reopened. Not by title: the recorder can catch a window mid-navigation,
# with one folder's path and the previous folder's title.
function Select-FolderPairs([object[]]$Windows, [object[]]$Jobs) {
    if ($Windows.Count -eq 0) { return }
    $shown = Get-ExplorerFolders
    $used = [Collections.Generic.HashSet[long]]::new()
    foreach ($job in $Jobs) {
        $w = $Windows | Where-Object { -not $used.Contains([long]$_.Hwnd) -and $shown[[long]$_.Hwnd].Path -contains $job.Saved.Folder } |
             Select-Object -First 1
        if ($w) {
            [void]$used.Add([long]$w.Hwnd)
            [pscustomobject]@{ Window = $w; Job = $job }
        }
    }
}

function Move-SavedWindow([StartupApps.WindowInfo]$Window, [object]$Saved, [StartupApps.VirtualDesktops]$Desktops) {
    $s = $Saved
    $what = "{0} '{1}'" -f $s.ProcessName, (Format-Title $s.Title)
    $did = [Collections.Generic.List[string]]::new()
    try {
        if (-not [StartupApps.Windows]::IsOnMonitor($Window.Hwnd, $s.MonLeft, $s.MonTop, $s.MonRight, $s.MonBottom)) {
            if ([StartupApps.Windows]::MonitorExists($s.MonLeft, $s.MonTop, $s.MonRight, $s.MonBottom)) {
                [StartupApps.Windows]::Place($Window.Hwnd, $s.ShowCmd, $s.Flags, $s.Left, $s.Top, $s.Right, $s.Bottom)
                $did.Add("screen $($s.Monitor)")
            } else {
                $did.Add("screen $($s.Monitor) isn't connected - left where it is")
            }
        }
        if ($s.Desktop -gt 0 -and $Window.Desktop -ne $s.Desktop) {
            if (-not $Desktops)                    { $did.Add("desktop $($s.Desktop) skipped - desktop moves unavailable (see above)") }
            elseif ($s.Desktop -gt $Desktops.Count) { $did.Add("desktop $($s.Desktop) no longer exists - left on this one") }
            else { $Desktops.MoveWindow($Window.Hwnd, $s.Desktop); $did.Add("desktop $($s.Desktop)") }
        }
        Write-Log ("placed            $what -> " + $(if ($did.Count) { $did -join ', ' } else { 'already in place' }))
    } catch {
        Write-Log ("FAILED  place $what -> " + $_.Exception.GetBaseException().Message)
    }
}

# The shell's desktop service can still be starting right after sign-in, so retry
# for a while. NotSupportedException means this Windows build changed the interface
# IDs - no amount of retrying fixes that, so give up at once. Either way, windows
# still go back to the right screen.
function Connect-VirtualDesktops {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try { return [StartupApps.VirtualDesktops]::Open() }
        catch {
            $err = $_.Exception.GetBaseException()
            if ($err -is [NotSupportedException] -or (Get-Date) -ge $deadline) {
                Write-Log ("desktop moves unavailable, windows stay on the current desktop: " + $err.Message)
                return $null
            }
            Start-Sleep -Seconds 2
        }
    }
}

# ------------------------------------------------------------------ main

try {
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowLayout.cs')
    if ($Watch) { Watch-Layout; return }

    Write-Log ('--- startup-apps run ({0}) ---' -f $PSCmdlet.ParameterSetName)
    Restore-Session -PlaceEverything:$AtLogon
    Write-Log '--- done ---'
    if ($AtLogon) { Start-Recorder }
} catch {
    Write-Log ("FATAL  " + $_.Exception.Message + "`n" + $_.ScriptStackTrace)
    throw
}
