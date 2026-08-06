param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($resolvedMsiPath, 0)

function Get-ShortcutRows {
    $view = $database.OpenView('SELECT `Shortcut`,`Directory_`,`Name`,`Component_` FROM `Shortcut`')
    $view.Execute()
    $rows = @()
    $record = $view.Fetch()
    while ($null -ne $record) {
        $rows += [PSCustomObject]@{
            Id = $record.StringData(1)
            Directory = $record.StringData(2)
            Name = $record.StringData(3)
            Component = $record.StringData(4)
        }
        $record = $view.Fetch()
    }

    return $rows
}

$shortcuts = Get-ShortcutRows
$desktopShortcuts = @($shortcuts | Where-Object Directory -eq 'DesktopFolder')
if ($desktopShortcuts.Count -gt 0) {
    $names = $desktopShortcuts.Name -join ', '
    throw "Machine-wide MSI must not author DesktopFolder shortcuts: $names"
}

$startMenuShortcuts = @($shortcuts | Where-Object Directory -eq 'ProgramMenuFolder')
if ($startMenuShortcuts.Count -eq 0) {
    throw 'The MSI must retain a ProgramMenuFolder shortcut.'
}

$versionView = $database.OpenView("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'")
$versionView.Execute()
$versionRecord = $versionView.Fetch()
if ($null -eq $versionRecord) {
    throw 'The MSI ProductVersion property is missing.'
}

Write-Output "PASS: ProductVersion=$($versionRecord.StringData(1)); StartMenuShortcuts=$($startMenuShortcuts.Count); DesktopShortcuts=$($desktopShortcuts.Count)"
